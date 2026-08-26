using Slate.Models;
using Slate.Services.Graph;
using Slate.Services.Storage;

namespace Slate.Services.Planning;

public sealed record SyncSummary(int Created, int Updated, int Deleted, int Failed)
{
    public int Total => Created + Updated + Deleted;
    public bool DidNothing => Total == 0 && Failed == 0;

    /// <summary>Human-readable one-liner, e.g. "3 created, 1 updated."</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Created > 0) parts.Add($"{Created} created");
        if (Updated > 0) parts.Add($"{Updated} updated");
        if (Deleted > 0) parts.Add($"{Deleted} removed");
        return parts.Count == 0 ? "No changes." : string.Join(", ", parts) + ".";
    }
}

/// <summary>
/// Owns the set of allocations and the one-way push into Outlook. The plan is the source of
/// truth: Outlook events are created, updated and removed to match it.
/// </summary>
public sealed class PlannerService(PlanStore store, GraphCalendarClient graph, SettingsStore settings)
{
    /// <summary>
    /// Event ids whose allocation was deleted but whose calendar event still needs removing.
    /// Kept in the plan file, so closing the app before the next send does not strand them.
    /// </summary>
    private List<string> PendingDeletes => store.PendingDeletes;

    public event Action? Changed;

    public IReadOnlyList<Allocation> Allocations => store.All;

    public IEnumerable<Allocation> InRange(DateTime start, DateTime end) =>
        store.All.Where(a => a.Overlaps(start, end)).OrderBy(a => a.Start);

    public IEnumerable<Allocation> ForDay(DateTime day) => InRange(day.Date, day.Date.AddDays(1));

    public int PendingCount => store.All.Count(a => a.State is SyncState.Draft or SyncState.Modified or SyncState.Failed)
                               + PendingDeletes.Count;

    /// <summary>Blocks whose Outlook event was deleted there and which need a decision.</summary>
    public int MissingCount => store.All.Count(a => a.MissingInOutlook);

    // ---------------------------------------------------------------- mutations

    public Allocation Add(WorkItem item, DateTime start, int durationMinutes)
    {
        var allocation = new Allocation
        {
            WorkItemId = item.Id,
            WorkItemTitle = item.Title,
            WorkItemType = item.WorkItemType,
            WorkItemState = item.State,
            WorkItemUrl = item.Url,
            Project = item.Project,
            Start = Snap(start),
            DurationMinutes = Math.Max(settings.Current.Planning.SlotMinutes, durationMinutes),
        };

        store.All.Add(allocation);
        Persist();
        return allocation;
    }

    public void Move(Guid id, DateTime newStart)
    {
        var allocation = Find(id);
        if (allocation is null) return;

        allocation.Start = Snap(newStart);
        allocation.LastError = null;
        Persist();
    }

    public void Resize(Guid id, int durationMinutes)
    {
        var allocation = Find(id);
        if (allocation is null) return;

        var slot = SlotMinutes;
        allocation.DurationMinutes = Math.Max(slot, (int)(Math.Round(durationMinutes / (double)slot) * slot));
        allocation.LastError = null;
        Persist();
    }

    public void UpdateNotes(Guid id, string notes)
    {
        var allocation = Find(id);
        if (allocation is null) return;

        allocation.Notes = notes;
        allocation.LastError = null;
        Persist();
    }

    public void Remove(Guid id)
    {
        var allocation = Find(id);
        if (allocation is null) return;

        if (allocation.OutlookEventId is { Length: > 0 } eventId &&
            settings.Current.Calendar.DeleteEventWithAllocation)
        {
            PendingDeletes.Add(eventId);
        }

        store.All.Remove(allocation);
        Persist();
    }

    /// <summary>Drops the allocation from the plan but leaves its Outlook event in place.</summary>
    public void Detach(Guid id)
    {
        var allocation = Find(id);
        if (allocation is null) return;

        store.All.Remove(allocation);
        Persist();
    }

    public Allocation? Duplicate(Guid id, DateTime newStart)
    {
        var source = Find(id);
        if (source is null) return null;

        var copy = source.Clone();
        copy.Id = Guid.NewGuid();
        copy.Start = Snap(newStart);
        copy.OutlookEventId = null;
        copy.SyncedAt = null;
        copy.SyncedFingerprint = null;
        copy.LastError = null;

        store.All.Add(copy);
        Persist();
        return copy;
    }

    public void ClearRange(DateTime start, DateTime end)
    {
        foreach (var allocation in InRange(start, end).ToList())
            Remove(allocation.Id);
    }

    public Allocation? Find(Guid id) => store.All.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// Points an existing block at a different work item, keeping its time. Used when a Task
    /// is spawned to stand in for a type that cannot carry time.
    /// </summary>
    public void Repoint(Guid allocationId, WorkItem item)
    {
        var allocation = Find(allocationId);
        if (allocation is null) return;

        allocation.WorkItemId = item.Id;
        allocation.WorkItemTitle = item.Title;
        allocation.WorkItemType = item.WorkItemType;
        allocation.WorkItemState = item.State;
        allocation.WorkItemUrl = item.Url;
        allocation.Project = item.Project;
        allocation.LastError = null;

        Persist();
    }

    /// <summary>Total minutes already allocated to a work item across the whole plan.</summary>
    public int AllocatedMinutes(int workItemId) =>
        store.All.Where(a => a.WorkItemId == workItemId).Sum(a => a.DurationMinutes);

    /// <summary>
    /// How long a new block for this item should be: whatever is left on the estimate, rounded
    /// to the grid and capped at half a day, falling back to the configured default.
    /// </summary>
    public int SuggestedDuration(WorkItem item)
    {
        var slot = SlotMinutes;
        var fallback = Math.Max(slot, settings.Current.Planning.DefaultDurationMinutes);

        if (item.EstimateMinutes is not int estimate || estimate <= 0) return fallback;

        var remaining = estimate - AllocatedMinutes(item.Id);
        if (remaining <= 0) return fallback;

        var capped = Math.Min(remaining, 4 * 60);
        return Math.Max(slot, (int)Math.Round(capped / (double)slot) * slot);
    }

    /// <summary>Refreshes the denormalised work item fields after an Azure DevOps reload.</summary>
    public void RefreshSnapshots(IEnumerable<WorkItem> items)
    {
        var byId = items.ToDictionary(i => i.Id);
        var dirty = false;

        foreach (var allocation in store.All)
        {
            if (!byId.TryGetValue(allocation.WorkItemId, out var item)) continue;
            if (allocation.WorkItemTitle == item.Title &&
                allocation.WorkItemState == item.State &&
                allocation.WorkItemType == item.WorkItemType &&
                allocation.WorkItemUrl == item.Url &&
                allocation.Project == item.Project) continue;

            allocation.WorkItemTitle = item.Title;
            allocation.WorkItemState = item.State;
            allocation.WorkItemType = item.WorkItemType;
            allocation.WorkItemUrl = item.Url;
            allocation.Project = item.Project;
            dirty = true;
        }

        if (dirty) Persist();
    }

    // ---------------------------------------------------------------- sync

    /// <summary>Pushes every pending change to Outlook. Returns what actually happened.</summary>
    public async Task<SyncSummary> SyncAsync(CancellationToken ct = default)
    {
        int created = 0, updated = 0, deleted = 0, failed = 0;

        foreach (var eventId in PendingDeletes.ToList())
        {
            try
            {
                await graph.DeleteEventAsync(eventId, ct);
                PendingDeletes.Remove(eventId);
                deleted++;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        try
        {
            foreach (var allocation in store.All.ToList())
            {
                ct.ThrowIfCancellationRequested();

                var state = allocation.State;
                if (state == SyncState.Synced) continue;

                // Deleted in Outlook on purpose - leave it flagged until the user chooses.
                if (allocation.MissingInOutlook) continue;

                try
                {
                    if (allocation.OutlookEventId is null)
                    {
                        allocation.OutlookEventId = await graph.CreateEventAsync(allocation, ct);
                        created++;
                    }
                    else if (!await graph.EventExistsAsync(allocation.OutlookEventId, ct))
                    {
                        // Gone from Outlook: flag it rather than quietly putting it back.
                        allocation.MissingInOutlook = true;
                        continue;
                    }
                    else
                    {
                        await graph.UpdateEventAsync(allocation, ct);
                        updated++;
                    }

                    allocation.SyncedAt = DateTimeOffset.Now;
                    allocation.SyncedFingerprint = allocation.Fingerprint();
                    allocation.LastError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    allocation.LastError = ex.Message;
                    failed++;
                }
            }
        }
        finally
        {
            // Every event id earned in this run is written down even when the run is cut
            // short: losing one means the next send creates a second copy of that event.
            Persist();
        }

        return new SyncSummary(created, updated, deleted, failed);
    }

    /// <summary>Pushes a single allocation, used by the inline "send to Outlook" action.</summary>
    public async Task<bool> SyncOneAsync(Guid id, CancellationToken ct = default)
    {
        var allocation = Find(id);
        if (allocation is null) return false;

        try
        {
            if (allocation.OutlookEventId is null || !await graph.EventExistsAsync(allocation.OutlookEventId, ct))
                allocation.OutlookEventId = await graph.CreateEventAsync(allocation, ct);
            else
                await graph.UpdateEventAsync(allocation, ct);

            allocation.MissingInOutlook = false;

            allocation.SyncedAt = DateTimeOffset.Now;
            allocation.SyncedFingerprint = allocation.Fingerprint();
            allocation.LastError = null;
            Persist();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            allocation.LastError = ex.Message;
            Persist();
            return false;
        }
    }

    // ---------------------------------------------------------------- local priority

    /// <summary>
    /// A priority you set in this app. Deliberately local: it never goes back to Azure DevOps,
    /// so you can triage your own week without touching what the team sees. 0 means unset.
    /// </summary>
    public int LocalPriority(int workItemId) => store.Priorities.GetValueOrDefault(workItemId);

    public void SetLocalPriority(int workItemId, int priority)
    {
        if (priority is < 1 or > 4)
            store.Priorities.Remove(workItemId);
        else
            store.Priorities[workItemId] = priority;

        Persist();
    }

    public void ClearLocalPriority(int workItemId)
    {
        if (store.Priorities.Remove(workItemId)) Persist();
    }

    // ---------------------------------------------------------------- two-way sync

    /// <summary>What a pull from Outlook changed.</summary>
    public sealed record ReconcileResult(int Moved, int NewlyMissing, int Restored)
    {
        public bool AnythingHappened => Moved > 0 || NewlyMissing > 0 || Restored > 0;
    }

    /// <summary>
    /// Pulls changes made in Outlook back into the plan for the window that was just fetched.
    /// Blocks with unsent local edits are left alone - the local edit wins until it is sent.
    /// </summary>
    public ReconcileResult ReconcileFromOutlook(
        IReadOnlyList<ExistingEvent> events, DateTime windowStart, DateTime windowEnd)
    {
        var byAllocation = new Dictionary<Guid, ExistingEvent>();
        var byEventId = new Dictionary<string, ExistingEvent>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (e.AllocationId is Guid id) byAllocation[id] = e;
            byEventId[e.Id] = e;
        }

        int moved = 0, newlyMissing = 0, restored = 0, readopted = 0;

        foreach (var allocation in store.All)
        {
            if (allocation.OutlookEventId is null) continue;

            // Only judge blocks whose time actually falls inside the window we just read.
            if (allocation.Start < windowStart || allocation.Start >= windowEnd) continue;

            // Matched on our own stamp first. The fallback on the recorded event id is kept
            // for events created before the stamp existed, but only when that event is not
            // claimed by some other block - otherwise a recycled id could point this
            // allocation at somebody else's entry, and the next send would write to it.
            var match = byAllocation.TryGetValue(allocation.Id, out var stamped)
                ? stamped
                : byEventId.GetValueOrDefault(allocation.OutlookEventId) is { } byId
                  && byId.AllocationId is null
                    ? byId
                    : null;

            if (match is null)
            {
                if (!allocation.MissingInOutlook)
                {
                    allocation.MissingInOutlook = true;
                    newlyMissing++;
                }
                continue;
            }

            if (allocation.MissingInOutlook)
            {
                allocation.MissingInOutlook = false;
                restored++;
            }

            // Outlook can hand out a new id when an event is edited there. Adopting it counts
            // as a change in its own right: left unsaved, the plan keeps the dead id and the
            // next send creates a duplicate alongside the event that is already there.
            // Only ever adopt an id off an event carrying our own stamp. Slate writes to the
            // events it created and to nothing else; adopting an unstamped id would be the
            // one way a foreign entry could end up on the receiving end of an update.
            if (!string.Equals(allocation.OutlookEventId, match.Id, StringComparison.Ordinal)
                && match.AllocationId == allocation.Id)
            {
                allocation.OutlookEventId = match.Id;
                readopted++;
            }

            // Unsent local edits take priority; do not overwrite them from the calendar.
            if (allocation.State is not SyncState.Synced) continue;

            var duration = (int)Math.Round((match.End - match.Start).TotalMinutes);
            if (duration <= 0) continue;
            if (match.Start == allocation.Start && duration == allocation.DurationMinutes) continue;

            // Stored as plain wall clock like every other allocation: a Local kind would
            // serialise with an offset and read back shifted in another time zone.
            allocation.Start = DateTime.SpecifyKind(match.Start, DateTimeKind.Unspecified);
            allocation.DurationMinutes = duration;
            allocation.LastError = null;

            // Outlook is the newer truth, so treat this as already in sync.
            allocation.SyncedFingerprint = allocation.Fingerprint();
            allocation.SyncedAt = DateTimeOffset.Now;
            moved++;
        }

        var result = new ReconcileResult(moved, newlyMissing, restored);
        if (result.AnythingHappened || readopted > 0) Persist();
        return result;
    }

    /// <summary>Drops a block that was deleted in Outlook, without touching the calendar again.</summary>
    public void ForgetMissing(Guid id)
    {
        var allocation = Find(id);
        if (allocation is null || !allocation.MissingInOutlook) return;

        store.All.Remove(allocation);
        Persist();
    }

    // ---------------------------------------------------------------- time entries

    public IReadOnlyList<TimeEntry> TimeEntries => store.TimeEntries;

    /// <summary>Records that time was booked against the work item from this block.</summary>
    public TimeEntry AddTimeEntry(
        Allocation allocation, double hours, bool reducedRemaining,
        double appliedCompleted = 0, double appliedRemaining = 0)
    {
        var entry = new TimeEntry
        {
            AllocationId = allocation.Id,
            WorkItemId = allocation.WorkItemId,
            WorkItemTitle = allocation.WorkItemTitle,
            WorkItemType = allocation.WorkItemType,
            WorkItemUrl = allocation.WorkItemUrl,
            Project = allocation.Project,
            Date = allocation.Start.Date,
            Start = allocation.Start,
            BlockMinutes = allocation.DurationMinutes,
            Hours = Math.Round(hours, 2),
            ReducedRemaining = reducedRemaining,
            AppliedCompleted = appliedCompleted,
            AppliedRemaining = appliedRemaining,
            RecordedAt = DateTimeOffset.Now,
            Notes = allocation.Notes,
        };

        store.TimeEntries.Add(entry);
        Persist();
        return entry;
    }

    public void RemoveTimeEntry(Guid entryId)
    {
        if (store.TimeEntries.RemoveAll(e => e.Id == entryId) > 0) Persist();
    }

    public TimeEntry? FindTimeEntry(Guid entryId) =>
        store.TimeEntries.FirstOrDefault(e => e.Id == entryId);

    /// <summary>Entries booked against days inside the range, newest first within each day.</summary>
    public IEnumerable<TimeEntry> EntriesInRange(DateTime start, DateTime end) =>
        store.TimeEntries
            .Where(e => e.Date >= start.Date && e.Date < end.Date)
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Start);

    public IEnumerable<TimeEntry> EntriesForBlock(Guid allocationId) =>
        store.TimeEntries.Where(e => e.AllocationId == allocationId).OrderBy(e => e.RecordedAt);

    /// <summary>The entry an undo from the calendar should reverse: the most recent one.</summary>
    public TimeEntry? LatestEntryForBlock(Guid allocationId) =>
        store.TimeEntries.Where(e => e.AllocationId == allocationId)
            .OrderByDescending(e => e.RecordedAt)
            .FirstOrDefault();

    /// <summary>Total minutes recorded against a work item.</summary>
    public int RecordedMinutes(int workItemId) =>
        store.TimeEntries.Where(e => e.WorkItemId == workItemId).Sum(e => e.Minutes);

    /// <summary>Total minutes recorded from one calendar block.</summary>
    public int RecordedMinutesForBlock(Guid allocationId) =>
        store.TimeEntries.Where(e => e.AllocationId == allocationId).Sum(e => e.Minutes);

    // ---------------------------------------------------------------- helpers

    /// <summary>Rounds a time to the configured grid granularity.</summary>
    public DateTime Snap(DateTime value)
    {
        var slot = SlotMinutes;
        var minutes = (int)Math.Round(value.TimeOfDay.TotalMinutes / slot) * slot;

        // Rounding up from the last slot of the day would otherwise roll into tomorrow.
        return value.Date.AddMinutes(Math.Min(minutes, (24 * 60) - slot));
    }

    /// <summary>The grid granularity, guarded so a bad setting can never divide by zero.</summary>
    private int SlotMinutes => Math.Clamp(settings.Current.Planning.SlotMinutes, 5, 120);

    private void Persist()
    {
        store.Save();
        Changed?.Invoke();
    }
}

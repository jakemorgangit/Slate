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
    public event Action? Changed;

    /// <summary>
    /// Whether changes to the plan actually reach the disk - see <see cref="PlanStore.CanSave"/>.
    /// False for the whole session when the file that is there could not be read and must not
    /// be written over, which is why a time write is refused while it holds: the hours would go
    /// on the work item with nothing anywhere to say they had.
    /// </summary>
    public bool CanSave => store.CanSave;

    /// <summary>
    /// Whether the plan that was on disk could be read at all - see
    /// <see cref="PlanStore.PlanWasUnreadable"/>. True for the whole session when it could
    /// not, even though saving works again once the old file has been put aside: what was
    /// outstanding is in that file and nowhere else, so a time write is refused on this too.
    /// </summary>
    public bool PlanWasUnreadable => store.PlanWasUnreadable;

    public IReadOnlyList<Allocation> Allocations => store.All;

    public IEnumerable<Allocation> InRange(DateTime start, DateTime end) =>
        store.All.Where(a => a.Overlaps(start, end)).OrderBy(a => a.Start);

    public IEnumerable<Allocation> ForDay(DateTime day) => InRange(day.Date, day.Date.AddDays(1));

    /// <summary>
    /// How much is waiting to go to Outlook: the blocks with unsent changes, and the events of
    /// deleted blocks still to be removed.
    ///
    /// Counted under the store's lock, because this is asked off the UI thread as well as from
    /// the header - the auto-sync timer's callback, and every Changed a Persist raises, which
    /// includes the ones the calendar poll makes. Counting enumerates the blocks while the UI
    /// thread can be taking one out of the same list, and what that throws - "Collection was
    /// modified", or a torn read of the list - lands somewhere that swallows it: an auto-sync
    /// round silently skipped, or the calendar load's catch, which clears the events already on
    /// screen and shows an error for something that never went wrong.
    /// </summary>
    public int PendingCount => store.Edit(file =>
        file.Allocations.Count(a => a.State is SyncState.Draft or SyncState.Modified or SyncState.Failed)
        + file.PendingDeletes.Count);

    /// <summary>
    /// Blocks whose Outlook event was deleted there and which need a decision. Counted under
    /// the lock for the same reason as <see cref="PendingCount"/>.
    /// </summary>
    public int MissingCount => store.Edit(file => file.Allocations.Count(a => a.MissingInOutlook));

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

        // Through the store, under the lock a save holds. Every list in the plan file is
        // written from the UI thread and copied by a save running on one of the polling
        // timers' thread-pool callbacks, and a copy reads the count and then takes the items:
        // a list that grew in between no longer fits and the save comes apart with it. On the
        // calendar path that exception is swallowed, taking whatever was being saved with it.
        store.Edit(file => file.Allocations.Add(allocation));
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

        // Both lists in one pass under the store's lock, so no save can copy a plan that has
        // let go of the block but not yet remembered its event.
        store.Edit(file =>
        {
            if (allocation.OutlookEventId is { Length: > 0 } eventId)
            {
                // Either the event goes too, or it stays and has to be remembered as one this
                // plan has finished with - otherwise the stamp it still carries would have the
                // next refresh adopt it straight back.
                if (settings.Current.Calendar.DeleteEventWithAllocation) file.PendingDeletes.Add(eventId);
                else Disown(file, eventId);
            }

            file.Allocations.Remove(allocation);
        });

        Persist();
    }

    /// <summary>Drops the allocation from the plan but leaves its Outlook event in place.</summary>
    public void Detach(Guid id)
    {
        var allocation = Find(id);
        if (allocation is null) return;

        store.Edit(file =>
        {
            // Unlinking is the whole point here, so the event has to be remembered as let go of.
            if (allocation.OutlookEventId is { Length: > 0 } eventId) Disown(file, eventId);

            file.Allocations.Remove(allocation);
        });

        Persist();
    }

    /// <summary>Remembers an event this plan has finished with, so adoption leaves it alone.</summary>
    private static void Disown(PlanFile file, string eventId)
    {
        if (!file.Disowned.Contains(eventId, StringComparer.Ordinal))
            file.Disowned.Add(eventId);
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

        // Nothing has ever been booked from a block that did not exist a moment ago. These say
        // the opposite - hours already recorded against this block somewhere else - and nothing
        // refreshes them for the copy: its own event goes out with a recorded total of zero,
        // read from the entries, which is where recording lives. Left on, Record day showed the
        // copy as part-recorded and took that off what it would book, silently booking nothing
        // at all for a copy of a fully recorded block, and the single dialog told the user hours
        // had gone on it from another machine. The third is the running total old plans kept on
        // the block: it is only ever read to migrate one, and a copy carrying it would be
        // migrated into a time entry for hours nobody booked.
        copy.RecordedElsewhereMinutes = 0;
        copy.LastRecordedAt = null;
        copy.RecordedMinutes = 0;

        // Cleared for the same reason as the ones above, and with more reason: a newer Slate's
        // per-block members are exactly what this copy cannot read, so it cannot tell which of
        // them say what that one block is - an event, a send, hours already booked. Clearing
        // also gives the copy its own dictionary, which Clone does not: it copies the
        // reference, and the two blocks would write each other's members for the session.
        copy.Extra = [];

        store.Edit(file => file.Allocations.Add(copy));
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

        // Copied under the store's lock: this runs from the work item poll as well as from
        // the UI thread, and enumerating the live list while the other adds to it throws.
        foreach (var allocation in store.Edit(file => file.Allocations.ToList()))
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

        // Both copies taken under the store's lock: this runs from the auto-sync timer, so
        // the UI thread can be adding to either list while it does.
        foreach (var eventId in store.Edit(file => file.PendingDeletes.ToList()))
        {
            try
            {
                await graph.DeleteEventAsync(eventId, ct);
                store.Edit(file => { file.PendingDeletes.Remove(eventId); });
                deleted++;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        try
        {
            foreach (var allocation in store.Edit(file => file.Allocations.ToList()))
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
                        allocation.OutlookEventId = await graph.CreateEventAsync(allocation, RecordedMinutesForBlockLocked(allocation.Id), ct);
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
                        await graph.UpdateEventAsync(allocation, RecordedMinutesForBlockLocked(allocation.Id), ct);
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
                allocation.OutlookEventId = await graph.CreateEventAsync(allocation, RecordedMinutesForBlockLocked(allocation.Id), ct);
            else
                await graph.UpdateEventAsync(allocation, RecordedMinutesForBlockLocked(allocation.Id), ct);

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
        // A dictionary is copied by enumerating it, so a save running on a polling thread
        // while this writes brings the whole save down - see PlanStore.Edit.
        store.Edit(file =>
        {
            if (priority is < 1 or > 4) file.Priorities.Remove(workItemId);
            else file.Priorities[workItemId] = priority;
        });

        Persist();
    }

    public void ClearLocalPriority(int workItemId)
    {
        if (store.Edit(file => file.Priorities.Remove(workItemId))) Persist();
    }

    // ---------------------------------------------------------------- two-way sync

    /// <summary>What a pull from Outlook changed.</summary>
    public sealed record ReconcileResult(int Moved, int NewlyMissing, int Restored)
    {
        public bool AnythingHappened => Moved > 0 || NewlyMissing > 0 || Restored > 0;
    }

    /// <summary>
    /// Takes on any of this app's events the plan does not know about, rebuilding the block
    /// from what the event carries.
    ///
    /// This is what makes a week planned on one machine workable on another: the plan file
    /// stays where it was written, but the calendar travels, so an event that says which
    /// block it is and what that block was is enough to pick it up and carry on - moving it,
    /// resizing it or deleting it - from anywhere.
    ///
    /// Only events still carrying our stamp are considered, and only when nothing in the
    /// plan already claims them and nothing here has already let them go. Returns how many
    /// were taken on.
    /// </summary>
    public int AdoptOrphanEvents(IReadOnlyList<ExistingEvent> events)
    {
        // All four taken together under the store's lock. This runs from the calendar poll's
        // timer callback while the UI thread can be adding blocks and disowning events, and
        // each of these copies reads a count and then takes the items - see PlanStore.Edit.
        // The last two are lists on disk, lifted out of the loop because every event tests
        // against them.
        var (known, claimed, pending, disowned) = store.Edit(file => (
            file.Allocations.Select(a => a.Id).ToHashSet(),
            file.Allocations
                .Where(a => a.OutlookEventId is not null)
                .Select(a => a.OutlookEventId!)
                .ToHashSet(StringComparer.Ordinal),
            file.PendingDeletes.ToHashSet(StringComparer.Ordinal),
            file.Disowned.ToHashSet(StringComparer.Ordinal)));

        var adopted = new List<Allocation>();

        foreach (var e in events)
        {
            if ((e.Payload is null && e.Marked is null) || e.IsAllDay) continue;

            // An event deleted here but not yet sent, or one deliberately unlinked, would
            // otherwise walk straight back in on the next refresh.
            if (pending.Contains(e.Id) || disowned.Contains(e.Id)) continue;

            if (e.AllocationId is Guid id && known.Contains(id)) continue;
            if (claimed.Contains(e.Id)) continue;

            var minutes = (int)Math.Round((e.End - e.Start).TotalMinutes);
            // The stamp is the fuller record when it came through; the subject is the fallback,
            // for an event that says it is Slate's without the stamp to say what it was.
            var rebuilt = Graph.AllocationPayload.Read(e.Payload, e.Id, e.Start, minutes);
            if (rebuilt is null && minutes > 0 && e.Marked?.ToAllocation(e.Id, e.Start, minutes) is { } fromSubject)
            {
                // Keeps the stamped id when that much came through, so two-way sync still
                // matches the block to its event instead of calling it missing.
                if (e.AllocationId is Guid stampedId) fromSubject.Id = stampedId;
                rebuilt = fromSubject;
            }
            if (rebuilt is null || known.Contains(rebuilt.Id)) continue;

            adopted.Add(rebuilt);
            known.Add(rebuilt.Id);
            claimed.Add(e.Id);
        }

        if (adopted.Count == 0) return 0;

        // Swapped in rather than added in place: this runs from the calendar poll, off the
        // UI thread, while the grid may be enumerating the same list.
        store.Append(adopted);
        Persist();
        return adopted.Count;
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

        // Copied under the store's lock, for the same reason adoption takes its own copies:
        // this runs from the calendar poll and the UI thread adds to the same list.
        foreach (var allocation in store.Edit(file => file.Allocations.ToList()))
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

        store.Edit(file =>
        {
            // The event is believed gone, but "believed" is doing work there: it was judged
            // missing from one week's worth of calendar. If it turns up again, it is still one
            // this plan has finished with.
            if (allocation.OutlookEventId is { Length: > 0 } eventId) Disown(file, eventId);

            file.Allocations.Remove(allocation);
        });

        Persist();
    }

    // ---------------------------------------------------------------- time entries

    public IReadOnlyList<TimeEntry> TimeEntries => store.TimeEntries;

    /// <summary>
    /// The entry a booking from this block makes, not yet filed. The organization is passed
    /// in rather than read from the settings: its id comes from Azure DevOps itself, and this
    /// is the stamp a later Undo, check or settle judges the work item by.
    /// </summary>
    public TimeEntry BuildTimeEntry(
        Allocation allocation, OrganizationRef organization, double hours, bool reducedRemaining,
        double appliedCompleted = 0, double appliedRemaining = 0, string comment = "")
    {
        return new TimeEntry
        {
            AllocationId = allocation.Id,
            WorkItemId = allocation.WorkItemId,
            WorkItemTitle = allocation.WorkItemTitle,
            WorkItemType = allocation.WorkItemType,
            WorkItemUrl = allocation.WorkItemUrl,
            Project = allocation.Project,
            Organization = organization.Url,
            OrganizationId = organization.Id,
            Date = allocation.Start.Date,
            Start = allocation.Start,
            BlockMinutes = allocation.DurationMinutes,
            Hours = Math.Round(hours, 2),
            ReducedRemaining = reducedRemaining,
            AppliedCompleted = appliedCompleted,
            AppliedRemaining = appliedRemaining,
            RecordedAt = DateTimeOffset.Now,
            Notes = allocation.Notes,
            Comment = comment.Trim(),
        };
    }

    // ---------------------------------------------------------------- unconfirmed bookings

    /// <summary>
    /// Writes a booking down before it is sent, pinned to the revision it tests against, and
    /// again for each further send of the same one so the record always names the change that
    /// is actually out there. Nothing counts it as recorded: it may not be on the work item.
    ///
    /// Before the send rather than after the answer, because a copy that goes away in between
    /// would otherwise leave hours on a work item with nothing here pointing at them, and the
    /// block offered again as if it had never been booked.
    /// </summary>
    public void PinUnconfirmed(UnconfirmedBooking booking, TimeWritePlan plan)
    {
        booking.Plan = plan;
        booking.InFlight = true;

        // Both fields clamp at zero, so what the change actually moves is only known once it
        // has been worked out against the item as it stands.
        booking.Entry.AppliedCompleted = plan.AppliedCompleted;
        booking.Entry.AppliedRemaining = plan.AppliedRemaining;

        // Through the store, under the lock a save holds: this runs from a time write, which
        // can be going on while a polling timer is saving the plan on another thread.
        store.Edit(file =>
        {
            if (!file.UnconfirmedBookings.Contains(booking)) file.UnconfirmedBookings.Add(booking);
        });

        Persist();
    }

    /// <summary>
    /// The write came back unanswered, so what was written down before it went out is now a
    /// genuinely unconfirmed booking: something to tell the user about, and to check later.
    /// </summary>
    public void LeaveUnconfirmed(UnconfirmedBooking booking)
    {
        booking.InFlight = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// Files a booking that did go through as the entry it makes, lets go of anything its
    /// write overtook, and takes it off the unconfirmed list - all in one save. Two saves
    /// would leave a moment where a copy that died had both the entry and the booking, and
    /// the next settle would file the same hours a second time.
    /// </summary>
    public TimeEntry Confirm(UnconfirmedBooking booking, TimeRecordResult result)
    {
        booking.InFlight = false;
        booking.Entry.AppliedCompleted = result.AppliedCompleted;
        booking.Entry.AppliedRemaining = result.AppliedRemaining;

        store.Edit(file =>
        {
            file.UnconfirmedBookings.Remove(booking);
            file.TimeEntries.Add(booking.Entry);
            if (result.Plan is { } landed) DropOvertaken(file, landed, booking.Entry);
        });

        Persist();
        return booking.Entry;
    }

    /// <summary>Lets go of a booking that certainly never reached the work item.</summary>
    public void DropUnconfirmed(UnconfirmedBooking booking)
    {
        booking.InFlight = false;
        if (store.Edit(file => file.UnconfirmedBookings.Remove(booking))) Persist();
    }

    /// <summary>
    /// The bookings still to be settled. Ones whose write is still going in this copy are not
    /// among them: until it comes back there is nothing to say and nothing to check, and the
    /// block it belongs to is claimed by that write anyway.
    /// </summary>
    public IReadOnlyList<UnconfirmedBooking> UnconfirmedBookings =>
        [.. store.UnconfirmedBookings.Where(b => !b.InFlight)];

    /// <summary>The unsettled bookings from one block, oldest first.</summary>
    public IReadOnlyList<UnconfirmedBooking> UnconfirmedForBlock(Guid allocationId) =>
        [.. store.UnconfirmedBookings
            .Where(b => !b.InFlight && b.Entry.AllocationId == allocationId)
            .OrderBy(b => b.Entry.RecordedAt)];

    public bool HasUnconfirmed(Guid allocationId) =>
        store.UnconfirmedBookings.Any(b => !b.InFlight && b.Entry.AllocationId == allocationId);

    /// <summary>Still waiting to be settled, rather than filed or let go since it was read.</summary>
    public bool IsUnsettled(UnconfirmedBooking booking) => store.UnconfirmedBookings.Contains(booking);

    /// <summary>
    /// Settles one unconfirmed booking: filed as an entry when it turned out to have landed,
    /// otherwise simply let go. False when it had already gone.
    /// </summary>
    public bool SettleUnconfirmed(UnconfirmedBooking booking, bool landed)
    {
        // Removing it is also the test that it was still there to settle: whether another
        // check or the quiet settle got here first is only knowable inside the same lock that
        // takes it off the list.
        var settled = store.Edit(file =>
        {
            if (!file.UnconfirmedBookings.Remove(booking)) return false;

            if (landed)
            {
                file.TimeEntries.Add(booking.Entry);
                if (booking.Plan is { } plan) DropOvertaken(file, plan, booking.Entry);
            }

            return true;
        });

        if (settled) Persist();
        return settled;
    }

    /// <summary>
    /// Lets go of everything a write that did land has overtaken: bookings and undos alike
    /// pinned to the same revision of the same work item. Only one write can ever become the
    /// revision after it, so none of the others can land now - and checking one later would
    /// find this write's change sitting there and claim it, which for an undo means dropping
    /// an entry whose hours are still on the work item.
    ///
    /// Both lists together, because the two are pinned the same way and a booking can just as
    /// easily overtake an undo as another booking: zero-clamping alone makes any two changes
    /// that drive Completed Work to nothing from the same place identical.
    ///
    /// Only within the organization the write that landed belongs to, which
    /// <paramref name="from"/> carries. A work item number and a revision mean nothing outside
    /// one organization, so without that a booking made before a switch would be let go on the
    /// strength of a revision of some other organization's #7 - and its hours could still be
    /// sitting on the item it really went to.
    ///
    /// An entry written before the stamp existed carries no organization at all, and an empty
    /// one matches everything - which is no confinement whatever. Those are judged on the work
    /// item link they were kept with instead: it is built from the organization address, so
    /// two links to the same work item number from the same organization are the same address
    /// and one from another organization plainly is not.
    ///
    /// Anything that says nothing about where it came from is left in, as everything was
    /// before there was a stamp to go by. An overtaken pin left behind is the worse of the two
    /// mistakes: a later check would find this write's change sitting on the work item and
    /// file the same hours a second time.
    /// </summary>
    private static bool DropOvertaken(PlanFile file, TimeWritePlan landed, TimeEntry from)
    {
        var where = OrganizationRef.For(from.Organization, from.OrganizationId);

        // Only used when the stamp says nothing, which is the case BelongsTo cannot confine.
        var here = where.Url.Length == 0 && where.Id.Length == 0
            ? TimeEntry.NormaliseOrganization(from.WorkItemUrl)
            : "";

        bool Confined(TimeEntry entry)
        {
            if (here.Length == 0) return entry.BelongsTo(where);

            // The candidate's own address: its stamp when it has one, its work item link when
            // it does not. Either way the link above starts with it, unless the two are from
            // different organizations.
            var there = TimeEntry.NormaliseOrganization(
                entry.Organization.Length > 0 ? entry.Organization : entry.WorkItemUrl);

            return there.Length == 0
                   || there == here
                   || here.StartsWith(there + "/", StringComparison.Ordinal);
        }

        var dropped = file.UnconfirmedBookings.RemoveAll(
            b => b.Plan is { } plan && Overtaken(plan, landed) && Confined(b.Entry)) > 0;

        foreach (var entry in file.TimeEntries)
        {
            if (entry.UnconfirmedUndo is not { } undo || !Overtaken(undo, landed)) continue;
            if (!Confined(entry)) continue;

            entry.UnconfirmedUndo = null;
            dropped = true;
        }

        return dropped;
    }

    private static bool Overtaken(TimeWritePlan pending, TimeWritePlan landed) =>
        pending.WorkItemId == landed.WorkItemId && pending.Rev == landed.Rev;

    /// <summary>Writes down, or clears, an undo of this entry that was never confirmed.</summary>
    public void SetUnconfirmedUndo(Guid entryId, TimeWritePlan? plan)
    {
        var set = store.Edit(file =>
        {
            if (file.TimeEntries.FirstOrDefault(e => e.Id == entryId) is not { } entry) return false;

            entry.UnconfirmedUndo = plan;
            return true;
        });

        if (set) Persist();
    }

    /// <summary>
    /// Settles an undo that was never confirmed, against the very pin it was judged by.
    ///
    /// The pin is checked here rather than by the caller because the caller had to await
    /// Azure DevOps to learn the answer, and in that time another undo of the same work item
    /// can land and sweep this pin away - which says this one did not land, whatever the
    /// history seemed to say a moment ago. Checked and acted on inside one lock, so there is
    /// no gap between the two: false means somebody else has already settled it.
    ///
    /// Landed, the entry goes and everything its revision overtook goes with it, in one save.
    /// Not landed, only the pin is let go: the hours are still on the work item and the entry
    /// still stands for them.
    /// </summary>
    public bool SettleUnconfirmedUndo(Guid entryId, TimeWritePlan expected, bool landed)
    {
        var settled = store.Edit(file =>
        {
            if (file.TimeEntries.FirstOrDefault(e => e.Id == entryId) is not { } entry) return false;
            if (entry.UnconfirmedUndo != expected) return false;

            if (landed)
            {
                file.TimeEntries.Remove(entry);
                DropOvertaken(file, expected, entry);
            }
            else
            {
                entry.UnconfirmedUndo = null;
            }

            return true;
        });

        if (settled) Persist();
        return settled;
    }

    /// <summary>
    /// Drops an entry, and in the same save lets go of whatever the write that took it off
    /// overtook. One save, so no copy can die holding the entry gone and the sweep undone.
    /// </summary>
    public void RemoveTimeEntry(Guid entryId, TimeWritePlan? landed = null)
    {
        var changed = store.Edit(file =>
        {
            var gone = file.TimeEntries.FirstOrDefault(e => e.Id == entryId);
            var removed = gone is not null && file.TimeEntries.Remove(gone);
            var swept = landed is { } plan && gone is not null && DropOvertaken(file, plan, gone);
            return removed || swept;
        });

        if (changed) Persist();
    }

    /// <summary>
    /// Says that a block's unsettled bookings were made against this organization after all,
    /// whatever address they were stamped with: a rename, Microsoft's move to dev.azure.com,
    /// or a server answering to a new name all leave hours stranded behind a stamp that no
    /// longer matches anything, and the old address may not even exist to switch back to.
    ///
    /// Only the stamp changes. Nothing is written to Azure DevOps, and the booking is no more
    /// settled than it was - it can simply be checked and answered for again. Returns how many
    /// were brought over.
    ///
    /// Only the bookings that are actually refused. A block can hold one made before an
    /// organization was switched and one made after, and the user saying that the old address
    /// is this organization says nothing about the one that was already here - re-stamping
    /// that one too would quietly move a booking nobody asked about.
    /// </summary>
    public int AdoptOrganization(Guid allocationId, OrganizationRef organization)
    {
        var stamped = store.Edit(file =>
        {
            var count = 0;
            foreach (var booking in file.UnconfirmedBookings)
            {
                if (booking.Entry.AllocationId != allocationId) continue;
                if (booking.Entry.BelongsTo(organization)) continue;
                if (Stamp(booking.Entry, organization)) count++;
            }

            return count;
        });

        if (stamped > 0) Persist();
        return stamped;
    }

    /// <summary>The same for one filed entry, whose undo is the thing being refused.</summary>
    public bool AdoptOrganizationForEntry(Guid entryId, OrganizationRef organization)
    {
        var stamped = store.Edit(file =>
            file.TimeEntries.FirstOrDefault(e => e.Id == entryId) is { } entry && Stamp(entry, organization));

        if (stamped) Persist();
        return stamped;
    }

    /// <summary>
    /// Writes the organization onto one entry, keeping whatever of its id is worth keeping.
    ///
    /// The address is the whole point of adopting and is simply replaced. The id is not: it is
    /// the one part of an organization that a rename or a new address leaves alone, and it is
    /// empty until connectionData has been read for the address Slate is pointed at now -
    /// which is exactly the state just after an organization is switched, the switch that makes
    /// these hours need adopting in the first place. Writing that emptiness over the id the
    /// entry already carries would throw away the very thing the stamp exists to hold, and the
    /// next rename would strand the entry all over again. So a known id is only ever replaced
    /// by another known one; saying these hours are this organization's while it cannot say
    /// which organization that is leaves the id they were booked with standing.
    /// </summary>
    private static bool Stamp(TimeEntry entry, OrganizationRef organization)
    {
        var id = organization.Id.Length > 0 ? organization.Id : entry.OrganizationId;

        if (entry.Organization == organization.Url && entry.OrganizationId == id) return false;

        entry.Organization = organization.Url;
        entry.OrganizationId = id;
        return true;
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

    /// <summary>
    /// Total minutes recorded from one calendar block, read without taking the plan's lock.
    ///
    /// For the UI thread, which asks this of every block on the grid several times a render
    /// and of a dialog's rows as they are built. The lock stays off this path because a save
    /// holds it across serialising the whole plan and writing it to disk, and putting a render
    /// behind that for every block is not a trade worth making for a number that is only shown.
    ///
    /// Anything reading it from another thread wants
    /// <see cref="RecordedMinutesForBlockLocked"/>: the sum enumerates the entries, and an
    /// entry filed while it does brings the reader down.
    /// </summary>
    public int RecordedMinutesForBlock(Guid allocationId) =>
        store.TimeEntries.Where(e => e.AllocationId == allocationId).Sum(e => e.Minutes);

    /// <summary>
    /// The same total, taken under the lock a save holds - for the two sync paths, which run
    /// from the calendar timer's thread-pool callback while a booking is being filed from the
    /// UI thread or from the quiet settle. The enumeration throws when that happens, and the
    /// throw lands on one allocation as a sync failure reading "Collection was modified",
    /// whose event is then not sent until the next pass.
    /// </summary>
    private int RecordedMinutesForBlockLocked(Guid allocationId) =>
        store.Edit(file => file.TimeEntries.Where(e => e.AllocationId == allocationId).Sum(e => e.Minutes));

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Rounds a time to the configured grid granularity, and hands it back as plain wall
    /// clock.
    ///
    /// The kind matters as much as the rounding: a Local time serialises with this machine's
    /// offset, and a plan read back somewhere else - or after the clocks change - would slide
    /// by that offset. Nine in the morning means nine in the morning wherever the plan is
    /// opened, so the offset has no business being written down.
    /// </summary>
    public DateTime Snap(DateTime value)
    {
        var slot = SlotMinutes;
        var minutes = (int)Math.Round(value.TimeOfDay.TotalMinutes / slot) * slot;

        // Rounding up from the last slot of the day would otherwise roll into tomorrow.
        var snapped = value.Date.AddMinutes(Math.Min(minutes, (24 * 60) - slot));
        return DateTime.SpecifyKind(snapped, DateTimeKind.Unspecified);
    }

    /// <summary>The grid granularity, guarded so a bad setting can never divide by zero.</summary>
    private int SlotMinutes => Math.Clamp(settings.Current.Planning.SlotMinutes, 5, 120);

    private void Persist()
    {
        store.Save();
        Changed?.Invoke();
    }
}

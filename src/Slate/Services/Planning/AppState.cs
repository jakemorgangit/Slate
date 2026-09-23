using Slate.Models;
using Slate.Services.Auth;
using Slate.Services.AzureDevOps;
using Slate.Services.Graph;
using Slate.Services.Storage;

namespace Slate.Services.Planning;

public enum WorkItemSort { Recent, Priority, Type, Title, Allocated, Age, Id, State, Estimate, Recorded }

/// <summary>
/// Shared view state for the whole window: what is loaded, what is filtered, which week is
/// showing, and what is currently being dragged. Components subscribe to <see cref="Changed"/>.
/// </summary>
public sealed class AppState(
    SettingsStore settingsStore,
    AzureDevOpsClient ado,
    GraphCalendarClient graph,
    PlannerService planner,
    MsalAuthService auth,
    ToastService toasts,
    WorkItemsCacheStore workItemsCache)
{
    private CancellationTokenSource? _workItemLoad;
    private CancellationTokenSource? _eventLoad;

    public AppSettings Settings => settingsStore.Current;

    /// <summary>
    /// Identifies the connection everything cached here was read from. A different
    /// organization, project or way of signing in makes all of it somebody else's data.
    /// </summary>
    private string ConnectionStamp =>
        $"{Settings.Ado.OrganizationUrl}|{Settings.Ado.Project}|{Settings.Ado.AuthMode}";

    private string _cachedFor = "";

    /// <summary>
    /// Forgets anything read from a connection that is no longer the current one. Checked
    /// at the start of every load rather than driven by a settings event, so it holds no
    /// matter which of the many save paths changed the configuration.
    /// </summary>
    private void DropStaleCaches()
    {
        var stamp = ConnectionStamp;
        if (stamp == _cachedFor) return;

        _cachedFor = stamp;
        Identity = "";
        Projects = [];
        CreatableTypes = [];
        AreaTree = null;
        Members = [];
        ado.ForgetPeople();

        // A cached list is only ever a stand-in for this same connection's own list - once
        // the connection has moved on, holding onto it would let a failed load for the new
        // one keep showing the old one as if it were current, with the red banner suppressed
        // to make room for the (now wrong) "showing a cached list" notice. Raised here, once,
        // rather than left to each caller: a quiet poll or a background lookup like
        // EnsureMembersAsync can be the one to drop it, and the UI still needs to hear about
        // it even though neither of those otherwise has a reason to call Changed itself.
        if (WorkItemsAreCached)
        {
            WorkItems = [];
            WorkItemsLoadedAt = null;
            WorkItemsAreCached = false;
            Changed?.Invoke();
        }
    }
    public PlannerService Planner => planner;
    public ToastService Toasts => toasts;

    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    // ---------------------------------------------------------------- work items

    public List<WorkItem> WorkItems { get; private set; } = [];
    public bool IsLoadingWorkItems { get; private set; }
    public string? WorkItemError { get; private set; }
    public DateTimeOffset? WorkItemsLoadedAt { get; private set; }

    /// <summary>
    /// True while what is in <see cref="WorkItems"/> is left over from a previous run rather
    /// than something this session actually fetched. Cleared the moment a load succeeds, so a
    /// manual refresh of an already-fresh list is never mistaken for this.
    /// </summary>
    public bool WorkItemsAreCached { get; private set; }

    /// <summary>
    /// What to say about a cached list while its own refresh is still in flight or has just
    /// failed - null once there is nothing stale to explain, which is what lets the sidebar
    /// fall back to its ordinary loading and error handling.
    /// </summary>
    public string? StaleWorkItemsNotice =>
        !WorkItemsAreCached ? null
        : IsLoadingWorkItems ? $"Showing list from {Ui.Ago(WorkItemsLoadedAt)} · refreshing…"
        : WorkItemError is not null ? $"Showing list from {Ui.Ago(WorkItemsLoadedAt)} · couldn't refresh"
        : null;

    public string Search { get; set; } = "";
    public HashSet<string> TypeFilter { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> StateFilter { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HideFullyAllocated { get; set; }
    public WorkItemSort Sort { get; private set; } = WorkItemSort.Recent;
    public bool SortDescending { get; private set; } = true;

    public IReadOnlyList<string> AvailableTypes =>
        [.. WorkItems.Select(i => i.WorkItemType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t)];

    public IReadOnlyList<string> AvailableStates =>
        [.. WorkItems.Select(i => i.State).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s)];

    public IReadOnlyList<WorkItem> FilteredWorkItems
    {
        get
        {
            IEnumerable<WorkItem> query = WorkItems;

            if (!string.IsNullOrWhiteSpace(Search))
            {
                var terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                query = query.Where(i => terms.All(t => Matches(i, t)));
            }

            if (TypeFilter.Count > 0) query = query.Where(i => TypeFilter.Contains(i.WorkItemType));
            if (StateFilter.Count > 0) query = query.Where(i => StateFilter.Contains(i.State));

            if (HideFullyAllocated)
                query = query.Where(i => i.EstimateMinutes is not int est || planner.AllocatedMinutes(i.Id) < est);

            return [.. Order(query)];
        }
    }

    /// <summary>
    /// Applies the chosen column and direction. Every column sorts both ways: clicking the
    /// header again flips it, which is what the little arrow in the header is showing.
    /// </summary>
    private IEnumerable<WorkItem> Order(IEnumerable<WorkItem> query)
    {
        IOrderedEnumerable<WorkItem> By<TKey>(Func<WorkItem, TKey> key, IComparer<TKey>? comparer = null) =>
            SortDescending ? query.OrderByDescending(key, comparer) : query.OrderBy(key, comparer);

        return Sort switch
        {
            // A priority set in this app wins over the one from Azure DevOps, and anything
            // with no priority at all sorts to the bottom either way round.
            WorkItemSort.Priority => By(i => Rank(EffectivePriority(i)))
                .ThenByDescending(i => i.ChangedDate),
            WorkItemSort.Type => By(i => i.WorkItemType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => Rank(EffectivePriority(i))),
            WorkItemSort.Title => By(i => i.Title, StringComparer.OrdinalIgnoreCase),
            WorkItemSort.State => By(i => i.State, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(i => i.ChangedDate),
            WorkItemSort.Id => By(i => i.Id),
            WorkItemSort.Allocated => By(i => planner.AllocatedMinutes(i.Id)),
            WorkItemSort.Recorded => By(i => planner.RecordedMinutes(i.Id)),
            WorkItemSort.Estimate => By(i => i.EstimateMinutes ?? -1),
            WorkItemSort.Age => By(i => i.AgeDays ?? -1),
            _ => By(i => i.ChangedDate),
        };
    }

    /// <summary>Unset priorities sit past P4 rather than ahead of P1.</summary>
    private static int Rank(int priority) => priority is >= 1 and <= 4 ? priority : 99;

    /// <summary>The priority actually on show: your own triage first, then Azure DevOps'.</summary>
    public int EffectivePriority(WorkItem item)
    {
        var local = planner.LocalPriority(item.Id);
        return local > 0 ? local : item.Priority;
    }

    /// <summary>
    /// Picks a column to sort by. Choosing the same one again reverses it; a new column
    /// starts in whichever direction is useful first - newest, largest, or A to Z.
    /// </summary>
    public void SortByColumn(WorkItemSort sort)
    {
        if (Sort == sort)
        {
            SortDescending = !SortDescending;
            return;
        }

        Sort = sort;
        SortDescending = sort switch
        {
            WorkItemSort.Title or WorkItemSort.Type or WorkItemSort.State or WorkItemSort.Priority => false,
            _ => true,
        };
    }

    private static bool Matches(WorkItem item, string term) =>
        item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.Id.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.WorkItemType.Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.State.Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.AssignedTo.Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.IterationPath.Contains(term, StringComparison.OrdinalIgnoreCase)
        || item.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How long to wait before each retry of a load that failed for want of a connection.
    /// At launch the network, VPN or proxy is often still coming up - after a reboot, on
    /// waking, or on opening a new release - and a saved sign-in deserves the benefit of the
    /// doubt: about a minute of quiet retrying, looking like an ordinary load, before an
    /// error is shown. A load someone asked for gets a couple of quick tries instead.
    /// </summary>
    private static readonly TimeSpan[] PatientRetries =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5),
         TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(25)];

    private static readonly TimeSpan[] QuickRetries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>The "could not load" toast still on screen, cleared once a load gets through.</summary>
    private Guid? _workItemErrorToast;

    public async Task LoadWorkItemsAsync(bool showToast = false, bool atStartup = false)
    {
        if (!Settings.IsAdoConfigured)
        {
            // An unconfigured connection has nothing behind it to show as "cached" - and
            // without this, clearing the org URL would leave a previous connection's list
            // on screen under the "not configured" banner instead of showing no list at all.
            if (WorkItemsAreCached)
            {
                WorkItems = [];
                WorkItemsLoadedAt = null;
                WorkItemsAreCached = false;
            }

            WorkItemError = "Azure DevOps is not configured yet.";
            Changed?.Invoke();
            return;
        }

        await _workItemLoad.CancelAndDisposeAsync();
        var cts = new CancellationTokenSource();
        _workItemLoad = cts;

        IsLoadingWorkItems = true;
        WorkItemError = null;
        Changed?.Invoke();

        // Which connection this fetch is actually for. Declared out here rather than just
        // inside the try, so a failure below can still tell a stale attempt apart from a
        // current one; reassigned throughout, since a retry can span a settings change.
        var stamp = ConnectionStamp;

        try
        {
            DropStaleCaches();
            stamp = ConnectionStamp;

            // Before the network is even asked: if this is the first load of the session and
            // a previous run left a list behind for this same connection, show it right away
            // rather than sitting on "Loading work items…" while a network that is often still
            // coming up - after a reboot, on waking, or on a new release - catches up.
            if (atStartup && WorkItems.Count == 0 && workItemsCache.TryLoad(stamp) is { } cached)
            {
                WorkItems = cached.Items;
                WorkItemsLoadedAt = cached.LoadedAt;
                WorkItemsAreCached = true;
                Changed?.Invoke();
            }

            var retries = atStartup ? PatientRetries : QuickRetries;
            List<WorkItem> items;
            for (var attempt = 0; ; attempt++)
            {
                // Re-checked on every attempt, not just once before the loop: a retry can span
                // a settings change, and GetWorkItemsAsync always fetches for whatever
                // connection is current by then, so what it returns must be judged - and
                // saved - against that same connection, not the one this load started for.
                DropStaleCaches();
                stamp = ConnectionStamp;

                try
                {
                    items = await ado.GetWorkItemsAsync(cts.Token);
                    break;
                }
                catch (AzureDevOpsException ex) when (ex.IsTransient && attempt < retries.Length)
                {
                    await Task.Delay(retries[attempt], cts.Token);
                }
            }

            if (cts.IsCancellationRequested) return;

            // After the list rather than before it: this failing is swallowed, so if it ran
            // first on a network that is not up yet, nobody would ever be told who we are.
            await EnsureIdentityAsync(cts.Token);

            if (cts.IsCancellationRequested) return;

            if (stamp != ConnectionStamp)
            {
                // The connection moved on again between that last fetch starting and now - too
                // late to judge this answer by it, and too late to just leave, since the old
                // connection's list would otherwise sit on screen looking current with neither
                // the amber notice nor the red banner. Drop it and let a fresh load for
                // whichever connection is current now pick this back up.
                DropStaleCaches();
                _ = LoadWorkItemsAsync(showToast, atStartup);
                return;
            }

            WorkItems = items;
            WorkItemsLoadedAt = DateTimeOffset.Now;
            WorkItemsAreCached = false;
            planner.RefreshSnapshots(items);
            ClearWorkItemErrorToast();
            workItemsCache.Save(stamp, WorkItemsLoadedAt.Value, items);

            // Once a session, now that Azure DevOps is known to be answering.
            if (!_settledUnconfirmed)
            {
                _settledUnconfirmed = true;
                _ = SettleUnconfirmedQuietlyAsync();
            }

            if (showToast)
                toasts.Success($"Loaded {items.Count} work item{(items.Count == 1 ? "" : "s")}");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (stamp != ConnectionStamp)
            {
                // Same reasoning as above: this failure was for a connection that is no longer
                // current, so it is not this connection's error to show, and it is not this
                // connection's cached list sitting underneath it either.
                DropStaleCaches();
                _ = LoadWorkItemsAsync(showToast, atStartup);
                return;
            }

            WorkItemError = ex.Message;
            ClearWorkItemErrorToast();
            _workItemErrorToast = toasts.Error("Could not load work items", ex.Message);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoadingWorkItems = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Who the Azure DevOps credential belongs to. Used to decide whose work items can be
    /// edited from here. Empty when it could not be determined.
    /// </summary>
    public string Identity { get; private set; } = "";

    private async Task EnsureIdentityAsync(CancellationToken ct)
    {
        if (Identity.Length > 0) return;

        try
        {
            Identity = await ado.GetAuthenticatedUserAsync(ct);
        }
        catch (Exception)
        {
            // Not knowing is fine; editing simply falls back to being allowed.
        }
    }

    /// <summary>True when this work item is the current user's to edit.</summary>
    public bool IsMine(string assignedTo) =>
        Identity.Length == 0 ||
        string.Equals(assignedTo, Identity, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-reads the work item list without touching the loading flag or raising toasts, so a
    /// background refresh never flickers the sidebar or interrupts what the user is doing.
    /// </summary>
    public async Task RefreshWorkItemsQuietlyAsync()
    {
        if (!Settings.IsAdoConfigured || IsLoadingWorkItems) return;

        try
        {
            DropStaleCaches();
            var stamp = ConnectionStamp;

            var items = await ado.GetWorkItemsAsync(CancellationToken.None);
            await EnsureIdentityAsync(CancellationToken.None);

            // The connection may have changed while this quiet poll was in flight - and a
            // foreground load that started after it, for a newer connection, must win rather
            // than being overwritten by this older answer. Either way, a cached list left
            // over from a connection nobody is looking at any more is not this poll's to keep:
            // drop it rather than leave it looking current with neither notice nor banner.
            if (stamp != ConnectionStamp || IsLoadingWorkItems)
            {
                DropStaleCaches();
                return;
            }

            WorkItems = items;
            WorkItemsLoadedAt = DateTimeOffset.Now;
            WorkItemsAreCached = false;
            WorkItemError = null;
            planner.RefreshSnapshots(items);
            ClearWorkItemErrorToast();
            workItemsCache.Save(stamp, WorkItemsLoadedAt.Value, items);
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A background poll must stay silent about the failure itself, but a connection
            // change that happened during it still needs its stale cache dropped - and
            // DropStaleCaches raises Changed on its own once it actually clears one.
            DropStaleCaches();
        }
    }

    /// <summary>Takes back a "could not load" toast once the problem behind it has gone away.</summary>
    private void ClearWorkItemErrorToast()
    {
        if (_workItemErrorToast is Guid id) toasts.Dismiss(id);
        _workItemErrorToast = null;
    }

    // ---------------------------------------------------------------- week

    private DateTime? _weekStart;
    private DayOfWeek? _weekAnchoredOn;

    public DateTime WeekStart
    {
        // Re-anchored when the configured first day of the week changes, so the grid does
        // not go on starting on the old one until the user presses Today.
        get
        {
            if (_weekStart is null || _weekAnchoredOn != Settings.Planning.WeekStart)
            {
                _weekAnchoredOn = Settings.Planning.WeekStart;
                _weekStart = StartOfWeek(_weekStart ?? DateTime.Today);
            }

            return _weekStart.Value;
        }
        set
        {
            _weekAnchoredOn = Settings.Planning.WeekStart;
            _weekStart = StartOfWeek(value);
            Changed?.Invoke();
            _ = LoadEventsAsync();
        }
    }

    public DateTime StartOfWeek(DateTime day)
    {
        var first = Settings.Planning.WeekStart;
        var delta = ((int)day.DayOfWeek - (int)first + 7) % 7;
        return day.Date.AddDays(-delta);
    }

    /// <summary>The days rendered in the grid - working days only, in week order.</summary>
    public IReadOnlyList<DateTime> VisibleDays
    {
        get
        {
            var working = Settings.Planning.WorkingDays;
            var days = Enumerable.Range(0, 7).Select(i => WeekStart.AddDays(i));
            return [.. working.Count == 0 ? days : days.Where(d => working.Contains(d.DayOfWeek))];
        }
    }

    public bool IsCurrentWeek => WeekStart == StartOfWeek(DateTime.Today);

    public void GoToWeek(int offset) => WeekStart = WeekStart.AddDays(offset * 7);
    public void GoToToday() => WeekStart = StartOfWeek(DateTime.Today);

    // ---------------------------------------------------------------- calendar overlay

    public List<ExistingEvent> ExistingEvents { get; private set; } = [];
    public bool IsLoadingEvents { get; private set; }
    public string? EventError { get; private set; }

    public async Task LoadEventsAsync()
    {
        // The copy an update is starting reads the calendar for itself.
        if (IsHandingOver) return;

        AdoptOverlayDefault();

        // Fetched whenever the calendar can be reached, not only when the overlay is on.
        // "Show what is already in my Outlook calendar" is about what is drawn; picking up
        // blocks planned elsewhere, honouring "never plan over existing events" and pulling
        // moves back are not, and hiding them behind a display toggle meant a decluttered
        // grid silently switched all three off.
        if (!Settings.IsCalendarConfigured)
        {
            ExistingEvents = [];
            Changed?.Invoke();
            return;
        }

        await _eventLoad.CancelAndDisposeAsync();
        var cts = new CancellationTokenSource();
        _eventLoad = cts;

        IsLoadingEvents = true;
        EventError = null;
        Changed?.Invoke();

        try
        {
            var windowStart = WeekStart;
            var windowEnd = WeekStart.AddDays(7);
            var events = await graph.GetEventsAsync(windowStart, windowEnd, cts.Token);
            if (cts.IsCancellationRequested) return;

            ExistingEvents = events;

            // Both of these write the plan. A handover that began while the calendar was
            // being read skips them: the new copy may already have read the plan, and would
            // never see what was written here.
            if (!TryBeginWrite()) return;
            try
            {
                // Adoption is not two-way sync: it is this plan meeting its own blocks for the
                // first time, and a machine that has never seen them has nothing to reconcile
                // against. It runs whichever way that setting is turned.
                var adopted = planner.AdoptOrphanEvents(events);
                if (adopted > 0)
                {
                    toasts.Info(
                        adopted == 1 ? "Picked up 1 block from your calendar" : $"Picked up {adopted} blocks from your calendar",
                        "Planned on another machine. You can move or delete them here as usual.");
                }

                if (Settings.Planning.TwoWaySync)
                    ReportReconcile(planner.ReconcileFromOutlook(events, windowStart, windowEnd));
            }
            finally
            {
                EndWrite();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // The overlay is a convenience; surface it quietly rather than blocking planning.
            EventError = ex.Message;
            ExistingEvents = [];
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoadingEvents = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Events for a day that were not created by this app - the "already busy" overlay.</summary>
    public IEnumerable<ExistingEvent> BusyOn(DateTime day) =>
        !Settings.Planning.ShowExistingEvents
            ? []
            : ExistingEvents.Where(e => !e.IsFromThisApp
                                        && !e.IsAllDay
                                        && e.ShowAs is not "free"
                                        && e.Start.Date <= day.Date && e.End > day.Date);

    /// <summary>
    /// The first time the calendar is actually reachable, show what is in it. Runs once and
    /// then never again, so turning the overlay off afterwards sticks.
    /// </summary>
    private void AdoptOverlayDefault()
    {
        if (!CanUseOutlook || Settings.Planning.OverlayChosen) return;

        var draft = settingsStore.CreateDraft();
        draft.Planning.OverlayChosen = true;
        draft.Planning.ShowExistingEvents = true;
        settingsStore.Save(draft);
    }

    /// <summary>True when something already in the calendar covers any of this span.</summary>
    public bool IsBusy(DateTime start, DateTime end) =>
        ExistingEvents.Any(e => e.BlocksTime && e.Start < end && start < e.End);

    /// <summary>The same test against a set of events that was fetched for some other window.</summary>
    private static bool IsBusy(IReadOnlyList<ExistingEvent> events, DateTime start, DateTime end) =>
        events.Any(e => e.BlocksTime && e.Start < end && start < e.End);

    /// <summary>
    /// Calendar entries covering a span, fetching them when it reaches outside the week that
    /// is loaded.
    ///
    /// Without this the overlap rule was only ever true for the week on screen: booking into
    /// next month consulted a list that could not contain next month and so found it free.
    /// A failure here returns nothing rather than throwing - the caller then behaves exactly
    /// as it did before there was a check at all.
    /// </summary>
    private async Task<IReadOnlyList<ExistingEvent>> EventsCovering(DateTime start, DateTime end)
    {
        if (!Settings.IsCalendarConfigured) return [];

        var loadedStart = WeekStart;
        var loadedEnd = WeekStart.AddDays(7);
        if (start >= loadedStart && end <= loadedEnd) return ExistingEvents;

        try
        {
            return await graph.GetEventsAsync(start.Date, end.Date.AddDays(1));
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Why a block placed here would never be drawn, or null when it would be.
    ///
    /// A block outside the days or hours the grid covers still counts towards the plan and
    /// still goes to Outlook, but there is no cell to draw it in - so it cannot be selected,
    /// moved or deleted. Better to say why and let the person widen the view than to accept
    /// work that then vanishes.
    /// </summary>
    public string? WhyNotVisible(DateTime start, DateTime end)
    {
        var p = Settings.Planning;

        if (p.WorkingDays.Count > 0 && !p.WorkingDays.Contains(start.DayOfWeek))
            return $"{start:dddd} is not one of your working days.";

        var open = TimeSpan.FromHours(p.GridStartHour);
        var close = TimeSpan.FromHours(p.GridEndHour);

        if (start.TimeOfDay < open || end.TimeOfDay > close && end.Date == start.Date)
        {
            return p.ShowFullDay
                ? $"The grid only runs to {p.GridEndHour:00}:00."
                : $"Your working day runs {p.GridStartHour:00}:00 to {p.GridEndHour:00}:00. " +
                  "Turn on \"Show the full 24 hours\" in Settings to plan outside it.";
        }

        // A block that runs past midnight has no second day to spill into on the grid.
        if (end.Date > start.Date) return "A block cannot run past the end of the day.";

        return null;
    }

    /// <summary>True when the overlap rule should stop work being planned here at all.</summary>
    public bool BlocksPlacement(DateTime start, DateTime end) =>
        Settings.Planning.PreventOverlap && IsBusy(start, end);

    public bool HasConflict(DateTime start, DateTime end) =>
        Settings.Planning.WarnOnConflict && IsBusy(start, end);

    private void ReportReconcile(PlannerService.ReconcileResult result)
    {
        if (!result.AnythingHappened) return;

        if (result.Moved > 0)
            toasts.Info($"{result.Moved} block(s) updated from Outlook",
                "Times were changed in your calendar, so the plan now matches.");

        if (result.NewlyMissing > 0)
            toasts.Warning($"{result.NewlyMissing} block(s) deleted in Outlook",
                "They are flagged in the plan - remove them or send them again.");
    }

    // ---------------------------------------------------------------- sync

    public bool IsSyncing { get; private set; }

    /// <summary>
    /// Pushes every pending allocation to Outlook and reports the outcome as a toast.
    /// Shared by the toolbar button and the Ctrl+S shortcut.
    /// </summary>
    // ---------------------------------------------------------------- automatic sync

    private Timer? _autoSync;
    private bool _autoSyncFailed;

    /// <summary>True when the last automatic push did not get everything through.</summary>
    public bool AutoSyncFailed => _autoSyncFailed;

    public bool AutoSyncOn => Settings.Planning.AutoSync;

    /// <summary>
    /// Called whenever the plan changes. Waits for the edits to stop rather than writing on
    /// every drag, so rearranging a week is one write per block instead of a burst per nudge.
    /// </summary>
    public void NudgeAutoSync()
    {
        if (IsHandingOver || !AutoSyncOn || !CanUseOutlook) return;
        if (IsSyncing || planner.PendingCount == 0) return;

        var delay = TimeSpan.FromSeconds(Math.Clamp(Settings.Planning.AutoSyncSeconds, 1, 60));

        if (_autoSync is null)
            _autoSync = new Timer(_ => _ = AutoSyncNowAsync(), null, delay, Timeout.InfiniteTimeSpan);
        else
            _autoSync.Change(delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The background push. Silent when it works - the status in the header is the feedback -
    /// and it never re-arms itself on failure, so a calendar that is refusing writes cannot
    /// turn into a retry loop. The next edit, or the Retry in the header, tries again.
    /// </summary>
    private async Task AutoSyncNowAsync()
    {
        if (IsSyncing || !CanUseOutlook || planner.PendingCount == 0) return;
        if (!TryBeginWrite()) return;

        IsSyncing = true;
        Changed?.Invoke();

        try
        {
            var summary = await planner.SyncAsync();
            _autoSyncFailed = summary.Failed > 0;

            if (summary.Failed > 0)
                toasts.Warning($"{summary.Failed} block(s) did not reach Outlook",
                    "Select a block to see why, or press Retry in the header.");

            await LoadEventsAsync();
        }
        catch (Exception ex)
        {
            _autoSyncFailed = true;
            toasts.Error("Could not update Outlook", ex.Message);
        }
        finally
        {
            IsSyncing = false;
            EndWrite();
            Changed?.Invoke();
        }
    }

    public async Task SyncAsync()
    {
        if (IsSyncing || IsHandingOver) return;

        if (!Settings.IsCalendarConfigured)
        {
            toasts.Error("Outlook is not connected", "Add your Entra ID client ID and sign in from Settings.");
            return;
        }

        if (planner.PendingCount == 0)
        {
            toasts.Info("Nothing to send", "Every allocation already matches Outlook.");
            return;
        }

        if (!TryBeginWrite()) return;

        IsSyncing = true;
        Changed?.Invoke();

        try
        {
            var summary = await planner.SyncAsync();
            _autoSyncFailed = summary.Failed > 0;

            if (summary.Failed == 0)
                toasts.Success("Outlook updated", summary.Describe());
            else
                toasts.Warning($"{summary.Failed} allocation(s) failed",
                    summary.Describe() + " Select a block to see the error.");

            await LoadEventsAsync();
        }
        catch (Exception ex)
        {
            toasts.Error("Sync failed", ex.Message);
        }
        finally
        {
            IsSyncing = false;
            EndWrite();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Sends a single block, for the inline "send to Outlook" action. Counted like the full
    /// sync, because it too creates an event and only then writes down its id. False when it
    /// did not go; the reason is on the block, unless an update is taking over.
    /// </summary>
    public async Task<bool> SyncOneAsync(Guid id)
    {
        if (!TryBeginWrite()) return false;

        try
        {
            return await planner.SyncOneAsync(id);
        }
        finally
        {
            EndWrite();
        }
    }

    // ---------------------------------------------------------------- auto-placement

    /// <summary>
    /// Books the item into the first gap that fits, scanning forward from now across working
    /// days. Returns null when nothing free turns up inside the search window.
    /// </summary>
    /// <param name="minutes">
    /// How long the block should be. Null falls back to whatever is left on the estimate,
    /// which is what the callers with nowhere to ask the question want.
    /// </param>
    public async Task<Allocation?> ScheduleNextFree(WorkItem item, int? minutes = null, int searchDays = 14)
    {
        var duration = minutes is > 0 ? minutes.Value : planner.SuggestedDuration(item);
        var p = Settings.Planning;

        // The booking window, not the grid: someone who keeps the first hour of the day for
        // catching up wants the grid to show it and automatic scheduling to leave it alone.
        var slot = Math.Clamp(p.SlotMinutes, 5, 120);
        var dayStart = TimeSpan.FromHours(p.BookFromHour);
        var dayEnd = TimeSpan.FromHours(p.BookUntilHour);
        var windowMinutes = (dayEnd - dayStart).TotalMinutes;

        if (duration > windowMinutes) return null;

        // The scan runs a fortnight ahead but ExistingEvents only ever holds the week on
        // screen, so the whole stretch is fetched up front - otherwise "the first gap that
        // fits, skipping anything already in your calendar" was only true for seven days.
        var busy = await EventsCovering(DateTime.Today, DateTime.Today.AddDays(searchDays));

        for (var d = 0; d < searchDays; d++)
        {
            var day = DateTime.Today.AddDays(d);
            if (p.WorkingDays.Count > 0 && !p.WorkingDays.Contains(day.DayOfWeek)) continue;

            for (var i = 0; i * slot + duration <= windowMinutes; i++)
            {
                var start = day + dayStart + TimeSpan.FromMinutes(i * slot);
                if (start < DateTime.Now) continue;

                var end = start.AddMinutes(duration);
                if (planner.InRange(start, end).Any()) continue;

                // The busy check here is IsBusy, not HasConflict: "next free" means free,
                // and HasConflict is gated on the unrelated "warn me" preference.
                if (IsBusy(busy, start, end)) continue;

                return planner.Add(item, start, duration);
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- scheduling

    /// <summary>The work item the schedule dialog is open for.</summary>
    public WorkItem? SchedulingFor { get; private set; }

    public void BeginSchedule(WorkItem item)
    {
        SchedulingFor = item;
        Changed?.Invoke();
    }

    public void CancelSchedule()
    {
        if (SchedulingFor is null) return;
        SchedulingFor = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Books the next free gap. Reports for itself rather than leaving each caller to say the
    /// same thing three different ways.
    /// </summary>
    public async Task<bool> ScheduleIntoNextFree(WorkItem item, int? minutes = null)
    {
        var allocation = await ScheduleNextFree(item, minutes);

        if (allocation is null)
        {
            toasts.Warning("No free slot in the next two weeks",
                "Widen the hours work can be booked into in Settings, or pick a time yourself.");
            return false;
        }

        AfterScheduled(item, allocation, "Scheduled");
        return true;
    }

    /// <summary>
    /// Books a chosen time. The time was asked for explicitly, so an overlap with another
    /// block is allowed - but something already in the calendar is still refused when the
    /// setting says never to plan over one, which is the whole point of that setting.
    ///
    /// The checks run on where the block will actually land, not on what was typed: Add
    /// snaps to the grid and will not go below one slot, so testing the raw values would
    /// clear a time the block does not end up occupying.
    /// </summary>
    public async Task<bool> ScheduleAt(WorkItem item, DateTime start, int minutes)
    {
        if (minutes <= 0) return false;

        var placed = planner.Snap(start);
        var end = placed.AddMinutes(Math.Max(Settings.Planning.SlotMinutes, minutes));

        if (WhyNotVisible(placed, end) is { } reason)
        {
            toasts.Warning("That time is not on the grid", reason);
            return false;
        }

        if (Settings.Planning.PreventOverlap &&
            IsBusy(await EventsCovering(placed, end), placed, end))
        {
            toasts.Warning("Something is already in the calendar then",
                "Pick another time, or turn off \"Never plan over existing events\" in Settings.");
            return false;
        }

        AfterScheduled(item, planner.Add(item, start, minutes), "Booked");
        return true;
    }

    private void AfterScheduled(WorkItem item, Allocation allocation, string verb)
    {
        SchedulingFor = null;
        SelectAllocation(allocation.Id);
        if (allocation.Start.Date != WeekStart.Date) WeekStart = allocation.Start;
        OfferTaskIfUntrackable(allocation);

        toasts.Success($"{verb} #{item.Id}",
            $"{Ui.RelativeDay(allocation.Start)} at {allocation.Start:HH:mm} for " +
            $"{Ui.Duration(allocation.DurationMinutes)}.");

        // Same courtesy the grid extends when a block is dragged onto something: overlapping
        // is allowed here, but it should never be a surprise.
        if (HasConflict(allocation.Start, allocation.End))
        {
            toasts.Warning("Overlaps something in Outlook",
                $"{Ui.RelativeDay(allocation.Start)} {Ui.TimeRange(allocation.Start, allocation.End)} already has an event.");
        }

        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- sign-in

    public SignInStatus SignIn { get; private set; } = new(false, null, null);

    public async Task RefreshSignInAsync()
    {
        SignIn = await auth.GetStatusAsync();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- what is on show

    /// <summary>
    /// How far the Outlook half has got. Nothing about it is shown at Off - without an Entra
    /// application there is no sign-in to offer and no calendar to reach, so presenting the
    /// buttons would only be advertising a dead end.
    /// </summary>
    public OutlookState Outlook =>
        !Settings.Entra.IsConfigured ? OutlookState.Off
        : SignIn.IsSignedIn ? OutlookState.Ready
        : OutlookState.NeedsSignIn;

    /// <summary>True once there is an Entra application to sign in to at all.</summary>
    public bool ShowOutlook => Outlook != OutlookState.Off;

    /// <summary>True when events can actually be read and written.</summary>
    public bool CanUseOutlook => Outlook == OutlookState.Ready;

    public AppMode Mode => Settings.Ui.Mode;

    /// <summary>
    /// Advanced is the whole app. Basic is deliberately read-only against Azure DevOps: it
    /// lists work items and plans time against them, and the only thing it writes is your own
    /// plan. One rule, so what is missing is predictable rather than a list to remember.
    /// </summary>
    public bool IsAdvanced => Mode == AppMode.Advanced;

    public bool CanRecordTime => IsAdvanced;
    public bool CanCreateWorkItems => IsAdvanced;
    public bool CanEditWorkItems => IsAdvanced;
    public bool CanDiscuss => IsAdvanced;
    public bool CanSetAdoPriority => IsAdvanced;

    /// <summary>Switches mode and persists it, so the app opens the way it was left.</summary>
    public void SetMode(AppMode mode)
    {
        if (Settings.Ui.Mode == mode) return;

        var draft = settingsStore.CreateDraft();
        draft.Ui.Mode = mode;
        settingsStore.Save(draft);

        // Basic has no Time tab; being left standing on it would show an empty page.
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- people

    /// <summary>Colleagues who can be @-mentioned in a discussion.</summary>
    public IReadOnlyList<OrgMember> Members { get; private set; } = [];

    private bool _loadingMembers;

    /// <summary>
    /// Fetches the mention list once, quietly. Names already on screen are passed along so
    /// the picker still works on a token too narrow to read the organization's people.
    /// </summary>
    public async Task EnsureMembersAsync()
    {
        if (_loadingMembers || !Settings.IsAdoConfigured) return;

        DropStaleCaches();
        _loadingMembers = true;

        try
        {
            var known = WorkItems.Select(i => i.AssignedTo)
                .Concat(Comments.Select(c => c.Author))
                .Append(Identity)
                .Where(name => !string.IsNullOrWhiteSpace(name));

            Members = await ado.GetOrgMembersAsync(known);
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // The picker is a convenience; typing the name by hand still works.
        }
        finally
        {
            _loadingMembers = false;
        }
    }

    // ---------------------------------------------------------------- priority

    /// <summary>A pending change to Azure DevOps' priority, waiting to be confirmed.</summary>
    public sealed record PriorityChange(int WorkItemId, string Title, int From, int To);

    public PriorityChange? PriorityPrompt { get; private set; }
    public bool IsSavingPriority { get; private set; }

    /// <summary>
    /// Asks before moving Azure DevOps' priority. Everyone on the team sees that field -
    /// unlike the triage priority, which stays on this machine - so it should never move by
    /// accident on the way past.
    /// </summary>
    public void BeginAdoPriorityChange(int workItemId, int to)
    {
        if (!CanSetAdoPriority) return;

        var item = WorkItems.FirstOrDefault(i => i.Id == workItemId);
        var from = item?.Priority ?? 0;

        if (from == to)
        {
            PriorityPrompt = null;
            Changed?.Invoke();
            return;
        }

        PriorityPrompt = new PriorityChange(workItemId, item?.Title ?? $"#{workItemId}", from, to);
        Changed?.Invoke();
    }

    public void CancelPriorityChange()
    {
        if (PriorityPrompt is null) return;
        PriorityPrompt = null;
        Changed?.Invoke();
    }

    public async Task<bool> ConfirmPriorityChangeAsync()
    {
        if (PriorityPrompt is not { } prompt || IsSavingPriority) return false;

        IsSavingPriority = true;
        Changed?.Invoke();

        try
        {
            var updated = await ado.SetAdoPriorityAsync(prompt.WorkItemId, prompt.To);

            if (updated is not null)
            {
                var index = WorkItems.FindIndex(i => i.Id == prompt.WorkItemId);
                if (index >= 0) WorkItems[index] = WorkItems[index] with { Priority = updated.Priority };
                planner.RefreshSnapshots([index >= 0 ? WorkItems[index] : updated]);
            }

            PriorityPrompt = null;
            toasts.Success($"Priority on #{prompt.WorkItemId} is now {Describe(prompt.To)}",
                "This one is in Azure DevOps, so the rest of the team sees it too.");

            // Repaint on the write, not on the re-read that follows it: the pills already
            // know the new value, and re-reading the whole work item is the slow part.
            Changed?.Invoke();

            if (DetailWorkItemId == prompt.WorkItemId) await RefreshDetailQuietlyAsync();
            return true;
        }
        catch (Exception ex)
        {
            toasts.Error("Could not change the priority", ex.Message);
            return false;
        }
        finally
        {
            IsSavingPriority = false;
            Changed?.Invoke();
        }
    }

    private static string Describe(int priority) => priority is >= 1 and <= 4 ? $"P{priority}" : "unset";

    // ---------------------------------------------------------------- work item state

    /// <summary>
    /// States by "project|type". A process template's states do not change while the app is
    /// open, and the same handful of types come round again and again, so they are worth
    /// keeping rather than re-fetching every time a menu opens.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<WorkItemStateOption>> _statesByType =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _statesLoading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The work item whose state is being written, so its picker can show it.</summary>
    public int? SavingStateFor { get; private set; }

    private static string StateKey(string project, string type) => $"{project}|{type}";

    /// <summary>What is already known, for a picker that has to render before any fetch lands.</summary>
    public IReadOnlyList<WorkItemStateOption> KnownStates(string project, string type) =>
        _statesByType.TryGetValue(StateKey(project, type), out var states) ? states : [];

    /// <summary>
    /// Loads the states for a type if they are not already in hand. Failure is quiet: the
    /// picker simply has nothing to offer, which is better than an error over a menu.
    /// </summary>
    public async Task EnsureStatesAsync(string project, string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return;

        var key = StateKey(project, type);
        if (_statesByType.ContainsKey(key) || !_statesLoading.Add(key)) return;

        try
        {
            var states = await ado.GetStatesAsync(project, type);
            if (states.Count > 0)
            {
                _statesByType[key] = states;
                Changed?.Invoke();
            }
        }
        catch (Exception)
        {
            // Nothing to offer. The work item's own state is still shown as text.
        }
        finally
        {
            _statesLoading.Remove(key);
        }
    }

    /// <summary>
    /// Moves a work item to another state in Azure DevOps. Everyone sees this field, so the
    /// blocks that quote it are brought back in step too - which marks them for re-sending,
    /// since the state travels in the calendar event.
    /// </summary>
    public async Task<bool> SetStateAsync(int workItemId, string state)
    {
        if (!CanEditWorkItems || SavingStateFor is not null) return false;

        var item = WorkItems.FirstOrDefault(i => i.Id == workItemId);
        if (item is not null && string.Equals(item.State, state, StringComparison.OrdinalIgnoreCase))
            return true;

        SavingStateFor = workItemId;
        Changed?.Invoke();

        try
        {
            var updated = await ado.SetStateAsync(workItemId, state);

            if (updated is not null)
            {
                var index = WorkItems.FindIndex(i => i.Id == workItemId);
                if (index >= 0) WorkItems[index] = WorkItems[index] with { State = updated.State };
                planner.RefreshSnapshots([index >= 0 ? WorkItems[index] : updated]);
            }

            toasts.Success($"#{workItemId} is now {state}",
                "This is the work item's own state, so the rest of the team sees it too.");

            Changed?.Invoke();

            if (DetailWorkItemId == workItemId) await RefreshDetailQuietlyAsync();
            return true;
        }
        catch (Exception ex)
        {
            toasts.Error($"Could not move #{workItemId} to {state}", ex.Message);
            return false;
        }
        finally
        {
            SavingStateFor = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Re-reads the open work item without disturbing the discussion below it.</summary>
    private async Task RefreshDetailQuietlyAsync()
    {
        if (DetailWorkItemId is not int id) return;

        try
        {
            Detail = await ado.GetWorkItemDetailAsync(id, CancellationToken.None);
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // What is already on screen stays; it is only a revision behind.
        }
    }

    // ---------------------------------------------------------------- raising new work

    public NewWorkItem? Creating { get; private set; }
    public bool IsCreating { get; private set; }
    public IReadOnlyList<string> CreatableTypes { get; private set; } = [];
    public IReadOnlyList<AdoProject> Projects { get; private set; } = [];

    /// <summary>The project's area tree, so the form can offer the levels underneath it.</summary>
    public AreaNode? AreaTree { get; private set; }

    /// <summary>
    /// Flips the "only my work items" filter and re-runs the query.
    ///
    /// It has to be a refetch rather than a filter over what is loaded: with it on, other
    /// people's items were never asked for, so there is nothing local to reveal - and asking
    /// for everyone's up front would run into the 500-row cap, which would then hide some of
    /// your own behind other people's.
    /// </summary>
    public async Task SetOnlyMineAsync(bool onlyMine)
    {
        if (Settings.Ado.OnlyMine == onlyMine) return;

        var draft = settingsStore.CreateDraft();
        draft.Ado.OnlyMine = onlyMine;
        settingsStore.Save(draft);

        Changed?.Invoke();
        await LoadWorkItemsAsync();
    }

    /// <summary>Why the area tree could not be read, for a picker that has to explain itself.</summary>
    public string? AreaTreeError { get; private set; }

    public bool IsLoadingAreaTree { get; private set; }

    /// <summary>The project the tree in hand belongs to, so switching project reloads it.</summary>
    private string _areaTreeProject = "";

    /// <summary>
    /// Loads the project's area tree for the pickers that need it.
    ///
    /// Reloads when the project changes and retries after a failure, neither of which the
    /// first version did - so a tree that failed to arrive once stayed missing for the rest
    /// of the session and the picker silently became a text box. Typing a path by hand is a
    /// poor substitute: an area has to be project-qualified to match anything, and nothing
    /// on screen says so.
    /// </summary>
    public async Task EnsureAreaTreeAsync(bool force = false)
    {
        var project = Settings.Ado.Project;
        if (string.IsNullOrWhiteSpace(project) || !Settings.IsAdoConfigured)
        {
            AreaTreeError = string.IsNullOrWhiteSpace(project)
                ? "Pick a project first to browse its areas."
                : "Connect to Azure DevOps first to browse areas.";
            return;
        }

        if (!force && AreaTree is not null && _areaTreeProject == project) return;
        if (IsLoadingAreaTree) return;

        IsLoadingAreaTree = true;
        AreaTreeError = null;
        Changed?.Invoke();

        try
        {
            AreaTree = await ado.GetAreaTreeAsync(project);
            _areaTreeProject = project;
            AreaTreeError = AreaTree is null ? "Azure DevOps returned no areas for this project." : null;
        }
        catch (Exception ex)
        {
            AreaTree = null;
            _areaTreeProject = "";
            AreaTreeError = ex.Message;
        }
        finally
        {
            IsLoadingAreaTree = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Opens the new work item form. The project and type lists are fetched in the
    /// background, so the form is usable the moment it appears rather than after a round trip.
    /// </summary>
    public async Task BeginCreateAsync(WorkItem? parent = null)
    {
        if (!CanCreateWorkItems) return;

        var project = parent?.Project is { Length: > 0 } fromParent
            ? fromParent
            : string.IsNullOrWhiteSpace(Settings.Ado.Project)
                ? WorkItems.Select(i => i.Project).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? ""
                : Settings.Ado.Project;

        Creating = new NewWorkItem
        {
            Project = project,
            WorkItemType = Settings.Planning.SpawnedTaskType,
            AssignedTo = Identity,
            AreaPath = parent?.AreaPath ?? "",
            IterationPath = parent?.IterationPath ?? "",
            ParentId = parent?.Id,
        };

        CreatableTypes = [];
        AreaTree = null;
        Changed?.Invoke();

        await LoadCreateOptionsAsync(project);
    }

    /// <summary>Why a dropdown on the new work item form came back empty, if it did.</summary>
    public string CreateOptionsNote { get; private set; } = "";

    /// <summary>
    /// Re-reads the project and type lists for the form. The two are fetched independently:
    /// a token that cannot list projects can often still list a project's types, and losing
    /// one dropdown is no reason to lose the other. Either way the field falls back to free
    /// text rather than blocking, and says why.
    /// </summary>
    public async Task LoadCreateOptionsAsync(string project)
    {
        DropStaleCaches();
        var trouble = new List<string>();

        if (Projects.Count == 0)
        {
            try
            {
                Projects = await ado.GetProjectsAsync();
            }
            catch (Exception ex)
            {
                trouble.Add("projects (" + ex.Message + ")");
            }
        }

        try
        {
            CreatableTypes = await ado.GetWorkItemTypesAsync(project);
        }
        catch (Exception ex)
        {
            CreatableTypes = [];
            trouble.Add("work item types (" + ex.Message + ")");
        }

        try
        {
            AreaTree = await ado.GetAreaTreeAsync(project);
        }
        catch (Exception ex)
        {
            AreaTree = null;
            trouble.Add("area paths (" + ex.Message + ")");
        }

        CreateOptionsNote = trouble.Count == 0
            ? ""
            : "Could not read " + string.Join(" or ", trouble) + ". Type it in by hand instead.";

        Changed?.Invoke();
    }

    public void CancelCreate()
    {
        if (Creating is null) return;
        Creating = null;
        Changed?.Invoke();
    }

    /// <summary>Raises the work item and drops it straight into the loaded list.</summary>
    public async Task<WorkItem?> CreateWorkItemAsync()
    {
        if (Creating is not { } request || IsCreating) return null;

        IsCreating = true;
        Changed?.Invoke();

        try
        {
            var created = await ado.CreateWorkItemAsync(request);

            // Put it on screen straight away rather than waiting for the next poll.
            if (WorkItems.All(i => i.Id != created.Id)) WorkItems.Insert(0, created);
            planner.RefreshSnapshots([created]);

            Creating = null;
            toasts.Success($"Created #{created.Id}", $"{created.WorkItemType}: {created.Title}");
            return created;
        }
        catch (Exception ex)
        {
            toasts.Error("Could not create that work item", ex.Message);
            return null;
        }
        finally
        {
            IsCreating = false;
            Changed?.Invoke();
        }
    }

    // ---------------------------------------------------------------- work item details

    public int? DetailWorkItemId { get; private set; }
    public WorkItemDetail? Detail { get; private set; }
    public bool IsLoadingDetail { get; private set; }
    public string? DetailError { get; private set; }

    private CancellationTokenSource? _detailLoad;

    /// <summary>Opens the details modal for a work item and fetches the full record.</summary>
    public async Task ShowWorkItemAsync(int workItemId)
    {
        await _detailLoad.CancelAndDisposeAsync();
        var cts = new CancellationTokenSource();
        _detailLoad = cts;

        DetailWorkItemId = workItemId;
        Detail = null;
        DetailError = null;
        IsLoadingDetail = true;
        Changed?.Invoke();

        try
        {
            var detail = await ado.GetWorkItemDetailAsync(workItemId, cts.Token);
            if (cts.IsCancellationRequested) return;
            Detail = detail;

            // The discussion is a separate call; let the modal paint before it arrives.
            IsLoadingDetail = false;
            Changed?.Invoke();
            await LoadCommentsAsync(workItemId, detail.Project, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            DetailError = ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoadingDetail = false;
                Changed?.Invoke();
            }
        }
    }

    public void CloseWorkItem()
    {
        if (DetailWorkItemId is null) return;
        DetailWorkItemId = null;
        Detail = null;
        DetailError = null;
        Comments = [];
        CommentError = null;
        Changed?.Invoke();
    }

    public bool IsSavingDescription { get; private set; }
    public bool IsSavingTitle { get; private set; }

    /// <summary>Writes a new description for the work item on show.</summary>
    public Task<bool> SaveDescriptionAsync(string html) =>
        SaveFieldAsync("System.Description", html, "Description", saving => IsSavingDescription = saving);

    /// <summary>Renames the work item on show.</summary>
    public Task<bool> SaveTitleAsync(string title) =>
        string.IsNullOrWhiteSpace(title)
            ? Task.FromResult(false)
            : SaveFieldAsync("System.Title", title.Trim(), "Title", saving => IsSavingTitle = saving);

    /// <summary>
    /// One field, written with the revision the modal was opened at, so an edit made
    /// elsewhere in the meantime is refused rather than quietly overwritten.
    /// </summary>
    private async Task<bool> SaveFieldAsync(string field, string value, string what, Action<bool> setBusy)
    {
        if (DetailWorkItemId is not int id || Detail is not { } detail) return false;
        if (IsSavingDescription || IsSavingTitle) return false;

        setBusy(true);
        Changed?.Invoke();

        try
        {
            Detail = await ado.UpdateFieldsAsync(
                id, detail.Rev, new Dictionary<string, object?> { [field] = value });

            var index = WorkItems.FindIndex(i => i.Id == id);
            if (index >= 0 && field == "System.Title")
                WorkItems[index] = WorkItems[index] with { Title = Detail.Title };

            toasts.Success($"{what} updated on #{id}");
            return true;
        }
        catch (Exception ex)
        {
            toasts.Error($"Could not save the {what.ToLowerInvariant()}", ex.Message);
            return false;
        }
        finally
        {
            setBusy(false);
            Changed?.Invoke();
        }
    }

    // ---------------------------------------------------------------- discussion

    public List<WorkItemComment> Comments { get; private set; } = [];
    public bool IsLoadingComments { get; private set; }
    public string? CommentError { get; private set; }
    public bool IsPostingComment { get; private set; }

    private async Task LoadCommentsAsync(int workItemId, string project, CancellationToken ct)
    {
        IsLoadingComments = true;
        CommentError = null;
        Comments = [];
        Changed?.Invoke();

        try
        {
            var comments = await ado.GetCommentsAsync(workItemId, project, ct);
            if (ct.IsCancellationRequested) return;
            Comments = comments;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // The discussion is extra; the rest of the modal stays usable without it.
            CommentError = ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingComments = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Adds to the work item's discussion in Azure DevOps, rendered according to the format
    /// the box was in and with every picked colleague turned into a real mention.
    /// </summary>
    public async Task<bool> AddCommentAsync(string text, TextFormat format, IReadOnlyList<OrgMember> mentioned)
    {
        if (DetailWorkItemId is not int id || Detail is not { } detail) return false;
        if (string.IsNullOrWhiteSpace(text) || IsPostingComment) return false;

        IsPostingComment = true;
        Changed?.Invoke();

        try
        {
            var comment = await ado.AddCommentAsync(id, detail.Project, Html.ToCommentHtml(text, format, mentioned));
            Comments = [.. Comments, comment];
            CommentError = null;
            toasts.Success($"Added to the discussion on #{id}");
            return true;
        }
        catch (Exception ex)
        {
            toasts.Error("Could not add that comment", ex.Message);
            return false;
        }
        finally
        {
            IsPostingComment = false;
            Changed?.Invoke();
        }
    }

    /// <summary>The list entry for the work item on show, when it is one of the loaded ones.</summary>
    public WorkItem? DetailListEntry =>
        DetailWorkItemId is int id ? WorkItems.FirstOrDefault(i => i.Id == id) : null;

    // ---------------------------------------------------------------- recording time

    public Allocation? RecordingFor { get; private set; }

    /// <summary>
    /// Opens the single-block recording dialog.
    ///
    /// Only from the calendar itself, the same rule "Record today" goes by: from under another
    /// dialog this one would open behind it, and under a Record day pass it would read the
    /// block as unrecorded while that pass was still writing it. The two being asymmetric is
    /// the kind of difference that turns into a way of booking a block twice.
    /// </summary>
    public void BeginRecordTime(Allocation allocation)
    {
        if (!CanRecordTime || IsHandingOver) return;
        if (RecordDayFor is not null || DetailWorkItemId is not null || SchedulingFor is not null
            || PriorityPrompt is not null || Creating is not null || SpawnFor is not null) return;

        RecordingFor = allocation;
        Changed?.Invoke();
    }

    /// <summary>The day the "Record today" dialog is open for, or null while it is closed.</summary>
    public DateTime? RecordDayFor { get; private set; }

    /// <summary>
    /// Opens the whole-day recording dialog for the given day - "today" by default, but any
    /// day the calendar has on screen works the same way.
    ///
    /// Only over the calendar itself: Ctrl+R is ignored while any other dialog is open. They
    /// are all drawn above this one, so that a work item opened from one of its rows lands on
    /// top, which means this opened from under one of them would sit hidden behind it. Under
    /// "Record time…" it would also read that block as unrecorded before the booking went in.
    /// </summary>
    public void BeginRecordDay(DateTime day)
    {
        if (!CanRecordTime || IsHandingOver) return;
        if (RecordingFor is not null || DetailWorkItemId is not null || SchedulingFor is not null
            || PriorityPrompt is not null || Creating is not null || SpawnFor is not null) return;

        RecordDayFor = day.Date;
        Changed?.Invoke();
    }

    public void CancelRecordDay()
    {
        if (RecordDayFor is null) return;
        RecordDayFor = null;
        Changed?.Invoke();
    }

    public void CancelRecordTime()
    {
        if (RecordingFor is null) return;
        RecordingFor = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Books time against the work item in Azure DevOps, then re-reads that item so the
    /// planner shows the new Completed and Remaining Work straight away.
    ///
    /// An optional note is posted to the work item's discussion afterwards. The hours are
    /// the point of this operation and they are already written by then, so a discussion
    /// that will not take the note says so and leaves the booking standing rather than
    /// unwinding a good write over a failed extra.
    ///
    /// The dialog is closed only while it is still showing this block: shut and reopened for
    /// another one while this went through, that one is left open.
    /// </summary>
    public async Task<TimeWriteOutcome> RecordTimeAsync(
        Allocation allocation, double hours, bool reduceRemaining,
        string note = "", TextFormat noteFormat = TextFormat.Markdown)
    {
        if (TryClaimBlock(allocation.Id) is { } refused)
        {
            toasts.Error("Could not record that time", refused);
            return TimeWriteOutcome.Failed;
        }

        try
        {
            var result = await WriteTimeAsync(allocation, hours, reduceRemaining, note);
            var noted = await PostTimeNoteAsync(allocation, note, noteFormat);

            toasts.Success($"Recorded {hours:0.##}h on #{allocation.WorkItemId}",
                $"Completed Work is now {result.CompletedWork:0.##}h, Remaining {result.RemainingWork:0.##}h."
                + (noted ? " Your note is on the discussion." : ""));

            if (RecordingFor?.Id == allocation.Id) RecordingFor = null;
            Changed?.Invoke();

            // Re-read the item in the background. It only refreshes the numbers already
            // shown, so the dialog must not sit on "Saving..." waiting for it.
            _ = RefreshWorkItemAsync(allocation.WorkItemId);
            return TimeWriteOutcome.Recorded;
        }
        catch (TimeWriteUnconfirmedException ex)
        {
            // An error rather than a warning, so it stays until dismissed: the dialog it came
            // from may already be shut, and this is something to act on.
            toasts.Error($"{hours:0.##}h on #{allocation.WorkItemId} may already be booked",
                ex.Message + (string.IsNullOrWhiteSpace(note) ? "" : " Your note has not been posted."));
            Changed?.Invoke();
            return TimeWriteOutcome.Unconfirmed;
        }
        catch (BlockUnsettledException ex)
        {
            // Nothing was sent, and nothing should be until the earlier booking is settled -
            // so this is not a failure to try again.
            toasts.Error("Could not record that time", ex.Message);
            Changed?.Invoke();
            return TimeWriteOutcome.Unconfirmed;
        }
        catch (Exception ex)
        {
            toasts.Error("Could not record that time", ex.Message);
            return TimeWriteOutcome.Failed;
        }
        finally
        {
            ReleaseBlock(allocation.Id);
        }
    }

    /// <summary>
    /// The batch counterpart to <see cref="RecordTimeAsync"/>, used by "Record today" to book
    /// several blocks in one pass. Same write, same time entry, same note - it just hands the
    /// outcome back instead of toasting it, so a run of many rows can show its own per-row
    /// result and end in one summary toast rather than one per block.
    ///
    /// The note is kept on the time entry every time, but <paramref name="postNote"/> decides
    /// whether it also goes to the discussion: a day with two blocks of the same item books
    /// both, and the item should still get the comment once.
    /// </summary>
    public async Task<(TimeWriteOutcome Outcome, string? Error)> RecordTimeSilentAsync(
        Allocation allocation, double hours, bool reduceRemaining,
        string note = "", TextFormat noteFormat = TextFormat.Markdown, bool postNote = true)
    {
        // A row still to come when an update starts taking over is left for the new copy,
        // which shows it as not yet recorded.
        if (TryClaimBlock(allocation.Id) is { } refused) return (TimeWriteOutcome.Failed, refused);

        try
        {
            await WriteTimeAsync(allocation, hours, reduceRemaining, note);
            if (postNote) await PostTimeNoteAsync(allocation, note, noteFormat);

            Changed?.Invoke();
            _ = RefreshWorkItemAsync(allocation.WorkItemId);
            return (TimeWriteOutcome.Recorded, null);
        }
        catch (TimeWriteUnconfirmedException ex)
        {
            Changed?.Invoke();
            return (TimeWriteOutcome.Unconfirmed, ex.Message);
        }
        catch (BlockUnsettledException ex)
        {
            // The row is left the way a row whose own booking went unconfirmed is left -
            // unticked, with a check offered - because that is exactly what it is now.
            Changed?.Invoke();
            return (TimeWriteOutcome.Unconfirmed, ex.Message);
        }
        catch (Exception ex)
        {
            return (TimeWriteOutcome.Failed, ex.Message);
        }
        finally
        {
            ReleaseBlock(allocation.Id);
        }
    }

    /// <summary>
    /// Blocks with a booking - or a check on one - on its way to Azure DevOps at this moment,
    /// whichever dialog sent it. A block's entry only exists once its write is back, so
    /// without this "Record time…" could book a block a Record day pass left running behind a
    /// closed dialog was still writing, or the other way round.
    /// </summary>
    private readonly HashSet<Guid> _recordingBlocks = [];

    private const string BlockBusy =
        "This block is already being recorded. Wait for that to finish, then check what it booked before recording any more.";

    private const string BlockUnsettled =
        "A booking from this block was never confirmed and may already be on the work item. Check it before recording any more against this block.";

    /// <summary>
    /// Why time cannot be written while the plan file cannot be. Azure DevOps would take the
    /// hours either way; the record that says it did - the pin written before the send, the
    /// entry written after it - has nowhere to go, so the block would be offered again as
    /// never booked and the same hours would go on twice.
    /// </summary>
    private const string PlanUnwritable =
        "Your plan cannot be written at the moment, so time booked now could not be recorded here and could go on the work item twice. Close Slate, make sure nothing else is holding the plan file, and start it again.";

    /// <summary>
    /// Why time cannot be written after a plan that would not parse was put aside. Saving
    /// works again - it is a new, empty plan - but the bookings Azure DevOps never confirmed
    /// were in the old file and nowhere else, while the blocks come back from their Outlook
    /// events reading as never recorded. Recording one of those is how hours already on a work
    /// item go on it a second time, and nothing here can tell which blocks those are.
    /// </summary>
    private const string PlanUnreadable =
        "Your plan could not be read when Slate started, so it cannot tell which hours are already on a work item. The old file is kept beside it in the Slate data folder: sort that out, then start Slate again.";

    /// <summary>
    /// A time write refused here, before anything went to Azure DevOps, because the block
    /// already has a booking nothing has settled. Its own kind, so every caller can tell it
    /// apart from a failure and offer the check rather than a one-click retry.
    /// </summary>
    private sealed class BlockUnsettledException() : Exception(BlockUnsettled);

    /// <summary>
    /// Counts a time write in, or says why it cannot go ahead. Every null must be paired with
    /// an <see cref="EndWrite"/>.
    ///
    /// Three refusals, for the three ways a write would end up on a work item with nothing
    /// here to show for it: a handover, where the copy that will carry on has already read the
    /// plan; a plan file that cannot be written at all, where nothing is read back by anybody;
    /// and one that could not be read this time, where what was outstanding is in a file
    /// nothing will open again. Every path that books, undoes, checks or answers for time goes
    /// through this or through <see cref="TryClaimBlock"/>, which starts here.
    /// </summary>
    private string? TryBeginTimeWrite()
    {
        if (!TryBeginWrite()) return RestartingForUpdate;
        if (planner.CanSave && !planner.PlanWasUnreadable) return null;

        EndWrite();
        return planner.CanSave ? PlanUnreadable : PlanUnwritable;
    }

    /// <summary>
    /// Counts a time write in and claims its block, or says why it cannot go ahead. Every
    /// null must be paired with a <see cref="ReleaseBlock"/>.
    /// </summary>
    private string? TryClaimBlock(Guid allocationId)
    {
        if (TryBeginTimeWrite() is { } refused) return refused;

        lock (_recordingBlocks)
        {
            if (_recordingBlocks.Add(allocationId)) return null;
        }

        EndWrite();
        return BlockBusy;
    }

    private void ReleaseBlock(Guid allocationId)
    {
        lock (_recordingBlocks) _recordingBlocks.Remove(allocationId);
        EndWrite();
    }

    /// <summary>
    /// The write itself: books the hours in Azure DevOps and keeps the local entry for it -
    /// or, when Azure DevOps could not say whether they went on, leaves the booking standing
    /// as unconfirmed, so the block is not offered again as though nothing had happened.
    /// Callers claim the block with <see cref="TryClaimBlock"/>, which also counts the write
    /// in: a copy that exits between the two halves leaves hours booked that the plan knows
    /// nothing about, and the next copy offers to book them again.
    ///
    /// The booking is written to the plan before each send rather than once the answer is
    /// back, so the gap the claim cannot cover - a copy that dies with the PATCH already on
    /// its way - leaves the next one something it can settle against the work item.
    /// </summary>
    private async Task<TimeRecordResult> WriteTimeAsync(
        Allocation allocation, double hours, bool reduceRemaining, string note)
    {
        // A booking from this block that nothing has settled may be on the work item already,
        // so more hours on top of it are exactly how the same time goes on twice. Refused here
        // rather than in each dialog, so no caller can get past it - a list built before the
        // booking existed, a form that was already open, a retry beside a failed row. Settling
        // it, by a check or by hand, is the way out.
        if (planner.HasUnconfirmed(allocation.Id)) throw new BlockUnsettledException();

        var pending = new UnconfirmedBooking
        {
            Entry = planner.BuildTimeEntry(allocation, CurrentOrganization, hours, reduceRemaining, comment: note),
        };

        TimeRecordResult result;
        try
        {
            result = await ado.RecordTimeAsync(allocation.WorkItemId, hours, reduceRemaining,
                plan => PinQuietly(() => planner.PinUnconfirmed(pending, plan), allocation.WorkItemId));
        }
        catch (TimeWriteUnconfirmedException)
        {
            // Already written down, pinned to what went out. All that is left is to stop
            // calling it in flight, so the dialogs start saying so.
            planner.LeaveUnconfirmed(pending);
            throw;
        }
        catch (AzureDevOpsException)
        {
            // Turned away, or never sent: this one certainly is not on the work item, so what
            // was written down for it has nothing to settle and would only lock the block.
            PinQuietly(() => planner.DropUnconfirmed(pending), allocation.WorkItemId);
            throw;
        }
        catch (Exception ex)
        {
            // Anything else is not a refusal this code understands, so it is not taken as one.
            // Once something has gone out, the only answer that cannot end in the same hours
            // going on twice is the unconfirmed one - which is also what stops the caller
            // offering the block back with a one-click retry.
            planner.LeaveUnconfirmed(pending);

            if (pending.Plan is not { } pinned) throw;

            throw new TimeWriteUnconfirmedException(
                $"Something went wrong after the change to #{allocation.WorkItemId} had gone out, so whether it " +
                $"landed could not be settled: {ex.Message} Check the work item before trying again.", pinned, ex);
        }

        // The hours are on the work item by now, so a plan file that will not take the entry
        // must not be reported as a failure: that is a one-click retry of hours already booked.
        PinQuietly(() => planner.Confirm(pending, result), allocation.WorkItemId);
        return result;
    }

    /// <summary>
    /// Runs one of the plan-file steps around a time write. A plan that cannot be saved costs
    /// the safety net for this one write, and says so in the log; it must never stop the hours
    /// going on, and never turn an unconfirmed booking into a failure that invites a retry.
    /// The change is in memory either way, so this session carries on knowing about it.
    /// </summary>
    private static void PinQuietly(Action step, int workItemId)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            // Deliberately broad: nothing this does is worth losing the outcome of a write.
            CrashLog.WriteLine($"Could not write the plan down around the time write on #{workItemId}: {ex}");
        }
    }

    /// <summary>
    /// Looks again, without writing anything, for the bookings from a block that Azure DevOps
    /// never confirmed. One that turns out to have landed is filed as the entry it would have
    /// made; one that did not is let go, and the block can be booked again. Its note is not
    /// posted either way: arriving this late, whether it is still wanted is the user's call.
    ///
    /// Null when there was nothing left to check - the quiet settle, or another dialog, got
    /// there first. That is not the same as nothing having landed, and a caller that treated
    /// it as such would tick the block for booking and then refuse the booking.
    /// </summary>
    public async Task<TimeWriteOutcome?> CheckUnconfirmedAsync(Guid allocationId)
    {
        if (TryClaimBlock(allocationId) is { } refused)
        {
            toasts.Error("Could not check that booking", refused);
            return TimeWriteOutcome.Unconfirmed;
        }

        try
        {
            var bookings = planner.UnconfirmedForBlock(allocationId);
            if (bookings.Count == 0)
            {
                toasts.Info("Nothing left to check on that block",
                    "It had already been settled - what is shown here is up to date.");
                return null;
            }

            var workItemId = bookings[0].Entry.WorkItemId;

            // Judged one booking at a time, inside the settle. A block can hold bookings made
            // before and after an organization was switched, and taking the oldest one's
            // answer for all of them put every later booking permanently out of reach.
            var settled = await SettleBlockAsync(bookings);

            if (settled.LandedMinutes > 0)
                toasts.Success($"{Ui.Hours(settled.LandedMinutes)} did go on #{workItemId}",
                    "It is recorded here now as well."
                    + (settled.UnpostedNote ? " Its note is kept on the entry; it was not posted to the discussion." : ""));

            if (settled.Elsewhere is { } elsewhere)
                toasts.Error($"That booking on #{workItemId} is not from this organization", elsewhere);
            else if (settled.Unsure > 0)
                toasts.Error($"Still cannot tell whether that time went on #{workItemId}",
                    "Azure DevOps could not be asked, or the change may still be on its way. Look at the work " +
                    "item itself, or check again in a few minutes. If it is not there, use \"Never went on\" to let it go.");
            else if (settled.LandedMinutes == 0)
                toasts.Info($"Nothing was booked on #{workItemId}",
                    "That time never reached the work item, so it can be recorded again.");

            return settled.Unsure > 0 ? TimeWriteOutcome.Unconfirmed
                : settled.LandedMinutes > 0 ? TimeWriteOutcome.Recorded
                : TimeWriteOutcome.Failed;
        }
        catch (Exception ex)
        {
            toasts.Error("Could not check that booking", ex.Message);
            return TimeWriteOutcome.Unconfirmed;
        }
        finally
        {
            ReleaseBlock(allocationId);
        }
    }

    /// <summary>
    /// The Azure DevOps organization hours are being booked to at this moment: the address it
    /// is reached at, and the id the service gives it once it has been read. Both are stamped
    /// onto every entry, and both are what a later undo, check or settle judges by.
    /// </summary>
    public OrganizationRef CurrentOrganization =>
        OrganizationRef.For(Settings.Ado.OrganizationUrl, ado.OrganizationId);

    /// <summary>
    /// Why this entry's hours are not this connection's to act on, or null when they are.
    /// Work item numbers only mean anything within one organization, so an undo or a check
    /// run after switching would otherwise read, and change, a different item altogether.
    ///
    /// The way out is named as well as the refusal: an organization can be renamed or moved,
    /// and after that there is no switching back to what no longer exists. Someone who knows
    /// it is the same organization can say so from the Time tab, and everything works again.
    /// </summary>
    private string? WrongConnection(TimeEntry entry)
    {
        if (entry.BelongsTo(CurrentOrganization)) return null;

        var where = entry.Organization.Length > 0 ? entry.Organization : entry.WorkItemUrl;
        var here = CurrentOrganization.Url is { Length: > 0 } url ? url : "somewhere else";

        return $"#{entry.WorkItemId} \"{entry.WorkItemTitle}\" was booked against {where}, and Slate is " +
               $"connected to {here} now, where #{entry.WorkItemId} is a different work item. Switch back " +
               "to settle it - or, if this is that same organization under a new address, say so from the " +
               "Time tab and Slate will settle it here.";
    }

    /// <summary>
    /// The bookings of a block this connection may answer for, with <paramref name="elsewhere"/>
    /// set to why the others were left out when any were.
    ///
    /// One booking at a time, the way <see cref="SettleBlockAsync"/> has always judged them: a
    /// block can hold one booking made before an organization was switched and one made after,
    /// and refusing the whole block on the strength of the foreign one left the booking that is
    /// plainly this organization's with no way to be answered at all.
    /// </summary>
    private List<UnconfirmedBooking> MineOf(
        IReadOnlyList<UnconfirmedBooking> bookings, out string? elsewhere)
    {
        var mine = new List<UnconfirmedBooking>(bookings.Count);
        string? why = null;

        foreach (var booking in bookings)
        {
            if (WrongConnection(booking.Entry) is { } refused) why ??= refused;
            else mine.Add(booking);
        }

        elsewhere = why;
        return mine;
    }

    /// <summary>
    /// One block's unconfirmed bookings, split into the ones this connection may act on and
    /// the address the rest were booked against.
    ///
    /// What every opening onto those bookings shows is decided from this, so that none of them
    /// can offer an action the answer paths will then refuse - and none of them can hide one
    /// that would work. A block can hold a booking made before an organization was switched
    /// and one made after: the check and the two answers are for <see cref="Mine"/>, and
    /// "same organization" is the way out for the others.
    /// </summary>
    public sealed record UnsettledBlock(
        Guid AllocationId,
        IReadOnlyList<UnconfirmedBooking> Bookings,
        IReadOnlyList<UnconfirmedBooking> Mine,
        string? Elsewhere)
    {
        /// <summary>The oldest booking of the block, which names the work item for all of them.</summary>
        public TimeEntry First => Bookings[0].Entry;

        /// <summary>The newest, which is the one "sent ten minutes ago" is about.</summary>
        public TimeEntry Latest => Bookings[^1].Entry;

        public int Minutes => Bookings.Sum(b => b.Entry.Minutes);

        /// <summary>Nothing on this block can be checked or answered for from here.</summary>
        public bool AllElsewhere => Mine.Count == 0;

        /// <summary>Only once none of the ones that can be answered could still be arriving.</summary>
        public bool CanBeLetGo => Mine.Count > 0 && Mine.All(b => b.CanBeLetGo);
    }

    /// <summary>
    /// The unconfirmed bookings of one block as <see cref="UnsettledBlock"/> reads them, or
    /// null when the block has none left. For the two record dialogs, which ask about the one
    /// block they are open on.
    /// </summary>
    public UnsettledBlock? UnsettledFor(Guid allocationId) =>
        planner.UnconfirmedForBlock(allocationId) is { Count: > 0 } bookings
            ? Split(allocationId, bookings)
            : null;

    /// <summary>
    /// The same split, for a caller that already has the bookings in hand - the Time tab,
    /// which reads every block's at once and must judge them all from the one snapshot.
    /// </summary>
    public UnsettledBlock Split(Guid allocationId, IReadOnlyList<UnconfirmedBooking> bookings)
    {
        var mine = new List<UnconfirmedBooking>(bookings.Count);
        string? elsewhere = null;

        // The same test the answers themselves are refused by, so what is offered and what is
        // accepted cannot drift apart; only the wording differs, this one being what the
        // address is rather than why it is a refusal.
        foreach (var booking in bookings)
        {
            if (WrongConnection(booking.Entry) is null) mine.Add(booking);
            else elsewhere ??= BookedAgainst(booking.Entry);
        }

        return new UnsettledBlock(allocationId, bookings, mine, elsewhere);
    }

    /// <summary>Where a set of hours says it was booked, however little it was stamped with.</summary>
    public static string BookedAgainst(TimeEntry entry) =>
        entry.Organization is { Length: > 0 } stamp ? stamp : entry.WorkItemUrl;

    /// <summary>
    /// Lets go of a block's unconfirmed bookings without asking Azure DevOps again: the user
    /// has looked at the work item and the hours are not on it. The way out of a booking that
    /// can never be settled - a work item restored from the recycle bin, a revision the
    /// service will not give up, an identity it names in a way this app cannot match.
    ///
    /// Only once no send of it could still be arriving, so this can never be overtaken by the
    /// change landing a moment later; and the block is claimed first, so it cannot run beside
    /// a write of its own.
    ///
    /// Never for a booking made against another organization. This answer rests entirely on
    /// the user having looked at the work item - and that is the one case where they cannot
    /// have, because Slate is not connected to the organization the hours went to, and #7 over
    /// here is a different work item altogether.
    ///
    /// Judged one booking at a time, the way the check and the settle judge them. A block can
    /// hold bookings from either side of an organization switch, and refusing all of them on
    /// the strength of the foreign one leaves the local one with no answer at all.
    /// </summary>
    public bool LetGoUnconfirmed(Guid allocationId)
    {
        if (TryClaimBlock(allocationId) is { } refused)
        {
            toasts.Error("Could not let that booking go", refused);
            return false;
        }

        try
        {
            var all = planner.UnconfirmedForBlock(allocationId);
            if (all.Count == 0) return false;

            var bookings = MineOf(all, out var elsewhere);
            if (bookings.Count == 0)
            {
                toasts.Error("That booking is not this organization's to let go", elsewhere!);
                return false;
            }

            if (bookings.Any(b => !b.CanBeLetGo))
            {
                toasts.Error("Too soon to let that booking go",
                    "It was sent only moments ago and could still be arriving. Give it five minutes, check the " +
                    "work item, then try again.");
                return false;
            }

            var minutes = 0;
            foreach (var booking in bookings)
                if (planner.SettleUnconfirmed(booking, landed: false)) minutes += booking.Entry.Minutes;

            if (minutes == 0) return false;

            toasts.Info($"{Ui.Hours(minutes)} on #{bookings[0].Entry.WorkItemId} let go",
                "Slate has stopped waiting on it. Nothing was written to Azure DevOps either way."
                + (elsewhere is null
                    ? " The block can be recorded again."
                    : " Another booking on that block was made against a different organization and is still" +
                      " waiting, so the block cannot be recorded again until that one is settled too."));

            Changed?.Invoke();
            return true;
        }
        finally
        {
            ReleaseBlock(allocationId);
        }
    }

    /// <summary>
    /// The other half of <see cref="LetGoUnconfirmed"/>: the user has looked at the work item
    /// and the hours are on it, so the booking is filed as the entry it would have made.
    ///
    /// Without this, a booking Azure DevOps can never settle could only be let go - which
    /// leaves the block offering the same hours again, with them already on the work item.
    /// Nothing is written to Azure DevOps; this only writes down what is already there.
    ///
    /// Refused for another organization's booking for the same reason letting one go is: the
    /// work item this claims to have looked at is not one this connection can even show - and
    /// one booking at a time, so a block holding one from either side of a switch still has an
    /// answer for the one that is here.
    /// </summary>
    public bool FileUnconfirmed(Guid allocationId)
    {
        if (TryClaimBlock(allocationId) is { } refused)
        {
            toasts.Error("Could not record that booking", refused);
            return false;
        }

        try
        {
            var all = planner.UnconfirmedForBlock(allocationId);
            if (all.Count == 0) return false;

            var bookings = MineOf(all, out var elsewhere);
            if (bookings.Count == 0)
            {
                toasts.Error("That booking is not this organization's to settle", elsewhere!);
                return false;
            }

            // Same gate as letting one go: while a send could still be arriving, a check can
            // still settle it for certain, and that is better than anybody's reading of the
            // work item at this moment.
            if (bookings.Any(b => !b.CanBeLetGo))
            {
                toasts.Error("Too soon to settle that booking by hand",
                    "It was sent only moments ago and could still be arriving. Give it five minutes and check " +
                    "again - Azure DevOps may yet answer for it.");
                return false;
            }

            var workItemId = bookings[0].Entry.WorkItemId;
            var minutes = 0;
            foreach (var booking in bookings)
                if (planner.SettleUnconfirmed(booking, landed: true)) minutes += booking.Entry.Minutes;

            if (minutes == 0) return false;

            toasts.Success($"{Ui.Hours(minutes)} on #{workItemId} recorded here",
                "Taken as booked because you said the work item has it. Nothing was written to Azure DevOps; " +
                "undo the entry from the time view if it turns out it does not.");

            Changed?.Invoke();
            _ = RefreshWorkItemAsync(workItemId);
            return true;
        }
        finally
        {
            ReleaseBlock(allocationId);
        }
    }

    /// <summary>
    /// Takes a block's unsettled bookings as this organization's after all, when the user says
    /// so. The way out of the one refusal there is otherwise no way out of: an organization
    /// that was renamed or moved has no old address left to switch back to, so without this
    /// those hours could never be checked, answered for or undone again.
    ///
    /// It only re-stamps them, and only the ones that are actually refused: a block can hold a
    /// booking made before the switch and one made after, and the second was never in question.
    /// Nothing is written to Azure DevOps and nothing is settled - the check and the two
    /// answers simply become available again, and they still decide.
    /// </summary>
    public bool AdoptOrganizationForBlock(Guid allocationId)
    {
        if (TryClaimBlock(allocationId) is { } refused)
        {
            toasts.Error("Could not move that booking over", refused);
            return false;
        }

        try
        {
            var bookings = planner.UnconfirmedForBlock(allocationId);
            if (bookings.Count == 0) return false;

            var workItemId = bookings[0].Entry.WorkItemId;
            if (planner.AdoptOrganization(allocationId, CurrentOrganization) == 0) return false;

            toasts.Info($"That booking on #{workItemId} is this organization's now",
                "Nothing was written to Azure DevOps. Check it, or answer for it, as usual.");

            Changed?.Invoke();
            return true;
        }
        finally
        {
            ReleaseBlock(allocationId);
        }
    }

    /// <summary>
    /// The same for a filed entry, whose undo is what the organization stamp is refusing.
    /// Claimed the way an undo of it is, so it cannot run beside one.
    /// </summary>
    public bool AdoptOrganizationForEntry(Guid entryId)
    {
        if (TryBeginTimeWrite() is { } refused)
        {
            toasts.Error("Could not move that entry over", refused);
            return false;
        }

        lock (_undoingEntries)
        {
            if (!_undoingEntries.Add(entryId))
            {
                EndWrite();
                return false;
            }
        }

        try
        {
            if (planner.FindTimeEntry(entryId) is not { } entry) return false;
            if (!planner.AdoptOrganizationForEntry(entryId, CurrentOrganization)) return false;

            toasts.Info($"Those hours on #{entry.WorkItemId} are this organization's now",
                "Nothing was written to Azure DevOps. Undo works on them again.");

            Changed?.Invoke();
            return true;
        }
        finally
        {
            lock (_undoingEntries) _undoingEntries.Remove(entryId);
            EndWrite();
        }
    }

    /// <summary>
    /// Answers for an undo Azure DevOps never confirmed, when the user has the work item in
    /// front of them: it went through, so the entry goes, or it did not, so the entry stands
    /// and the pin is let go.
    ///
    /// Without this an undo stuck at "cannot tell" left its entry permanently un-undoable -
    /// every Undo of it stopped at the same unanswerable question. Nothing is written to Azure
    /// DevOps either way; this only writes down what the work item already says.
    ///
    /// Gated like the booking answers: only once no send of it could still be arriving, and
    /// never for another organization's hours, which are the ones the user cannot have looked
    /// at from here.
    /// </summary>
    public bool AnswerUnconfirmedUndo(Guid entryId, bool wentThrough)
    {
        if (TryBeginTimeWrite() is { } refused)
        {
            toasts.Error("Could not settle that undo", refused);
            return false;
        }

        lock (_undoingEntries)
        {
            if (!_undoingEntries.Add(entryId))
            {
                EndWrite();
                toasts.Error("Could not settle that undo", "That entry is already being undone.");
                return false;
            }
        }

        try
        {
            if (planner.FindTimeEntry(entryId) is not { UnconfirmedUndo: { } plan } entry) return false;

            if (WrongConnection(entry) is { } elsewhere)
            {
                toasts.Error("That undo is not this organization's to settle", elsewhere);
                return false;
            }

            if (plan.CouldStillLand)
            {
                toasts.Error("Too soon to settle that undo by hand",
                    "It was sent only moments ago and could still be arriving. Give it five minutes and undo " +
                    "again - Azure DevOps may yet answer for it.");
                return false;
            }

            if (!planner.SettleUnconfirmedUndo(entryId, plan, wentThrough)) return false;

            if (wentThrough)
            {
                toasts.Success($"The undo of {entry.Hours:0.##}h on #{entry.WorkItemId} is settled",
                    "Taken as gone through because you said the work item no longer has those hours. Nothing " +
                    "was written to Azure DevOps.");
                _ = RefreshWorkItemAsync(entry.WorkItemId);
            }
            else
            {
                toasts.Info($"The undo of {entry.Hours:0.##}h on #{entry.WorkItemId} never went through",
                    "The entry stands, and Undo works on it again.");
            }

            Changed?.Invoke();
            return true;
        }
        finally
        {
            lock (_undoingEntries) _undoingEntries.Remove(entryId);
            EndWrite();
        }
    }

    private bool _settledUnconfirmed;

    /// <summary>
    /// Does what <see cref="CheckUnconfirmedAsync"/> does for every unconfirmed booking, once a
    /// session, after the first list of work items that loads - so they are settled even when
    /// nobody presses Check, including ones from blocks deleted since, which no dialog can reach
    /// any more. Quiet unless one turns out to have landed, which is news worth a toast; one
    /// that still cannot be told is simply left for later.
    /// </summary>
    private async Task SettleUnconfirmedQuietlyAsync()
    {
        var organization = CurrentOrganization;
        var landedMinutes = 0;

        foreach (var group in planner.UnconfirmedBookings
                     .Where(b => b.Entry.BelongsTo(organization))
                     .GroupBy(b => b.Entry.AllocationId).ToList())
        {
            // A block being booked or checked right now is left to whatever is doing it.
            if (TryClaimBlock(group.Key) is not null) continue;

            try
            {
                landedMinutes += (await SettleBlockAsync([.. group])).LandedMinutes;
            }
            catch (Exception)
            {
                // Deliberately broad: this is housekeeping, and the next session tries again.
            }
            finally
            {
                ReleaseBlock(group.Key);
            }
        }

        if (landedMinutes > 0)
            toasts.Success($"{Ui.Hours(landedMinutes)} Azure DevOps had not confirmed did go on",
                "It is recorded here now as well.");

        // Undos the same way: one that did go through takes its entry with it, as it would
        // have at the time. Entries from another organization are left alone - their work
        // item numbers mean something else here.
        var undoneMinutes = 0;
        foreach (var entry in planner.TimeEntries
                     .Where(e => e.UnconfirmedUndo is not null && e.BelongsTo(organization)).ToList())
        {
            if (TryBeginTimeWrite() is not null) break;

            lock (_undoingEntries)
            {
                if (!_undoingEntries.Add(entry.Id))
                {
                    EndWrite();
                    continue;
                }
            }

            try
            {
                // Read again inside the loop, not from the list: settling an earlier undo lets
                // go of any pinned to the same revision, and one of those may be this.
                if (entry.UnconfirmedUndo is not { } plan) continue;

                var (landed, _) = await ado.CheckTimeWriteAsync(plan);
                if (landed is null) continue;

                // The pin is tested again, inside the same lock that acts on it. Reading it
                // before the await is not enough: a manual undo of another entry of this work
                // item can land while this check is out and sweep this pin away - which says
                // this undo did not land, whatever the check thought it saw. Two entries
                // pinned to one revision and describing the same change are ordinary, not
                // exotic, and dropping both would leave an hour on the work item with nothing
                // here pointing at it. Landed, the removal and the sweep go in one save.
                if (!planner.SettleUnconfirmedUndo(entry.Id, plan, landed.Value)) continue;

                if (landed.Value)
                {
                    undoneMinutes += entry.Minutes;
                    _ = RefreshWorkItemAsync(entry.WorkItemId);
                }
            }
            catch (Exception)
            {
                // Housekeeping again: the next Undo of it, or the next session, looks again.
            }
            finally
            {
                lock (_undoingEntries) _undoingEntries.Remove(entry.Id);
                EndWrite();
            }
        }

        if (undoneMinutes > 0)
            toasts.Success($"An undo of {Ui.Hours(undoneMinutes)} Azure DevOps had not confirmed did go through",
                "Its entry is gone from here now as well.");

        Changed?.Invoke();
    }

    /// <summary>
    /// What came of settling one block's unconfirmed bookings. <see cref="Elsewhere"/> is the
    /// reason the first booking from another organization could not be looked at, when there
    /// was one; those are counted in <see cref="Unsure"/> as well, because nothing about them
    /// has been settled either.
    /// </summary>
    private sealed record BlockSettlement(int LandedMinutes, int Unsure, string? Elsewhere, bool UnpostedNote);

    /// <summary>
    /// Settles what can be settled of one block's unconfirmed bookings. The caller claims the
    /// block first, so nothing else books or checks it meanwhile.
    /// </summary>
    private async Task<BlockSettlement> SettleBlockAsync(IReadOnlyList<UnconfirmedBooking> bookings)
    {
        var landedMinutes = 0;
        var unsure = 0;
        string? elsewhere = null;
        var unpostedNote = false;

        foreach (var booking in bookings)
        {
            // Settling an earlier one lets go of any pinned to the same revision: only one of
            // them can ever have landed.
            if (!planner.IsUnsettled(booking)) continue;

            // Somebody else's #7. Left standing rather than guessed at, and counted as
            // unsettled so nothing offers the block as free to book.
            if (WrongConnection(booking.Entry) is { } why)
            {
                elsewhere ??= why;
                unsure++;
                continue;
            }

            // Always written with a plan; one without could only come from a hand-edited file,
            // and there is nothing to look for.
            var landed = booking.Plan is { } plan ? (await ado.CheckTimeWriteAsync(plan)).Landed : false;

            if (landed is null)
            {
                unsure++;
                continue;
            }

            if (planner.SettleUnconfirmed(booking, landed.Value) && landed.Value)
            {
                landedMinutes += booking.Entry.Minutes;
                unpostedNote |= booking.Entry.Comment.Length > 0;
                _ = RefreshWorkItemAsync(booking.Entry.WorkItemId);
            }
        }

        Changed?.Invoke();
        return new BlockSettlement(landedMinutes, unsure, elsewhere, unpostedNote);
    }

    /// <summary>
    /// Adds the note that came with a time booking to the work item's discussion. Returns
    /// whether anything was posted: an empty note is the normal case, not a failure.
    ///
    /// If the work item on show is the one being booked against, the new comment is folded
    /// into the loaded discussion so the open modal does not have to be reopened to see it.
    /// </summary>
    private async Task<bool> PostTimeNoteAsync(Allocation allocation, string note, TextFormat format)
    {
        if (string.IsNullOrWhiteSpace(note)) return false;

        try
        {
            var comment = await ado.AddCommentAsync(
                allocation.WorkItemId, allocation.Project, Html.ToCommentHtml(note, format, Members));

            if (DetailWorkItemId == allocation.WorkItemId) Comments = [.. Comments, comment];
            return true;
        }
        catch (Exception ex)
        {
            toasts.Warning($"The time went on #{allocation.WorkItemId}, but the note did not", ex.Message);
            return false;
        }
    }

    /// <summary>Entries being taken back off at this moment, so a second Undo cannot take them off twice.</summary>
    private readonly HashSet<Guid> _undoingEntries = [];

    /// <summary>
    /// Takes a booking back off the work item in Azure DevOps and drops the entry. Only
    /// removes the entry locally once the write has actually succeeded.
    ///
    /// An undo Azure DevOps never confirmed is written onto the entry - before it is sent, so
    /// a copy that dies mid-write leaves it behind too - and the next Undo looks for it before
    /// doing anything: taking the hours off again when the first one had gone through would
    /// hand back work that was never there.
    /// </summary>
    public async Task<bool> UndoTimeEntryAsync(TimeEntry entry)
    {
        // Counted for the same reason as recording: cut off between the write and dropping
        // the entry, the entry would survive to be undone a second time. The plan being
        // unwritable is that same gap held open for the whole session.
        if (TryBeginTimeWrite() is { } refused)
        {
            toasts.Error("Could not undo that time", refused);
            return false;
        }

        lock (_undoingEntries)
        {
            if (!_undoingEntries.Add(entry.Id))
            {
                EndWrite();
                toasts.Error("Could not undo that time", "That entry is already being undone.");
                return false;
            }
        }

        try
        {
            // Taken from the plan rather than the caller: a page can still be holding an entry
            // that another Undo has already taken off and dropped.
            if (planner.FindTimeEntry(entry.Id) is not { } current)
            {
                toasts.Error("Could not undo that time", "That entry has already been undone.");
                return false;
            }

            // Only the organization these hours went to can take them off again: #7 elsewhere
            // is a different work item, and undoing against it would change somebody's work.
            if (WrongConnection(current) is { } elsewhere)
            {
                toasts.Error("Those hours are not this organization's to undo", elsewhere);
                return false;
            }

            if (current.UnconfirmedUndo is { } earlier)
            {
                var (landed, _) = await ado.CheckTimeWriteAsync(earlier);
                if (landed is null)
                {
                    toasts.Error($"Still cannot tell whether the undo on #{entry.WorkItemId} went through",
                        "Look at the work item itself, or try again in a few minutes.");
                    return false;
                }

                // Removed and swept in one save, against the very pin it was judged by: that
                // undo became the revision after the one it was pinned to, so nothing else
                // pinned there can have landed, and anything still waiting to be checked there
                // must not claim it. The pin is tested inside that same save because another
                // undo of this work item can have landed and swept it while this check was
                // out - and that says this one did not land, whatever the check saw.
                if (landed.Value && planner.SettleUnconfirmedUndo(entry.Id, earlier, landed: true))
                {
                    toasts.Success($"Undid {entry.Hours:0.##}h on #{entry.WorkItemId}",
                        "The earlier undo had gone through after all, so nothing more was taken off.");
                    Changed?.Invoke();
                    _ = RefreshWorkItemAsync(entry.WorkItemId);
                    return true;
                }

                planner.SettleUnconfirmedUndo(entry.Id, earlier, landed: false);
            }

            // Entries written before the applied amounts were recorded fall back to the
            // hours asked for, which is what those entries were undone by at the time.
            var completed = entry.AppliedCompleted != 0 ? entry.AppliedCompleted : entry.Hours;
            var remaining = entry.AppliedCompleted != 0
                ? entry.AppliedRemaining
                : entry.ReducedRemaining ? -entry.Hours : 0;

            TimeRecordResult result;
            try
            {
                // Written onto the entry before each send, the same way a booking is, so a
                // copy that dies with the PATCH on its way does not leave the entry looking
                // untouched and free to be undone all over again.
                result = await ado.UndoTimeAsync(entry.WorkItemId, completed, remaining,
                    plan => PinQuietly(() => planner.SetUnconfirmedUndo(entry.Id, plan), entry.WorkItemId));
            }
            catch (AzureDevOpsException ex) when (ex is not TimeWriteUnconfirmedException)
            {
                // Turned away, or never sent: nothing of it is on the work item, so the pin
                // would only make the next Undo go looking for a change that never existed.
                // An unconfirmed one is left pinned - that record is the whole point of it.
                PinQuietly(() => planner.SetUnconfirmedUndo(entry.Id, null), entry.WorkItemId);
                throw;
            }

            planner.RemoveTimeEntry(entry.Id, result.Plan);

            toasts.Success($"Undid {entry.Hours:0.##}h on #{entry.WorkItemId}",
                $"Completed Work is back to {result.CompletedWork:0.##}h, Remaining {result.RemainingWork:0.##}h.");

            Changed?.Invoke();
            _ = RefreshWorkItemAsync(entry.WorkItemId);
            return true;
        }
        catch (TimeWriteUnconfirmedException ex)
        {
            // Already pinned before the send in the ordinary case; this covers the one where
            // the plan file refused it then, and must never itself escape as a crash.
            PinQuietly(() => planner.SetUnconfirmedUndo(entry.Id, ex.Plan), entry.WorkItemId);
            toasts.Error($"The undo on #{entry.WorkItemId} may already have gone through",
                "Azure DevOps did not confirm it, and a look at the work item afterwards could not settle " +
                "whether it went through. Undo again looks for it first, so the hours are never taken off twice.");
            Changed?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            toasts.Error("Could not undo that time", ex.Message);
            return false;
        }
        finally
        {
            lock (_undoingEntries) _undoingEntries.Remove(entry.Id);
            EndWrite();
        }
    }

    /// <summary>Re-reads a single work item in place after it has been changed.</summary>
    public async Task RefreshWorkItemAsync(int workItemId)
    {
        try
        {
            // Match on id rather than taking the first row: a batch response is not
            // guaranteed to contain only what was asked for, and putting the wrong item
            // into the list produces duplicate keys that tear down the renderer.
            var refreshed = (await ado.GetWorkItemsByIdAsync([workItemId]))
                .FirstOrDefault(i => i.Id == workItemId);
            if (refreshed is null) return;

            var index = WorkItems.FindIndex(i => i.Id == workItemId);
            if (index >= 0) WorkItems[index] = refreshed;

            planner.RefreshSnapshots([refreshed]);
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // Deliberately broad: the write this follows has already succeeded, so a failed
            // cosmetic refresh must never turn a successful action into a reported error.
        }
    }

    // ---------------------------------------------------------------- spawning a task

    /// <summary>The block whose work item cannot carry time, awaiting the user's decision.</summary>
    public Allocation? SpawnFor { get; private set; }
    public WorkItem? SpawnParent { get; private set; }
    public bool IsSpawning { get; private set; }

    /// <summary>
    /// Called after a block is placed. If the work item type cannot carry time, offers to
    /// spawn a Task underneath it so the hours have somewhere to go.
    /// </summary>
    public void OfferTaskIfUntrackable(Allocation allocation)
    {
        // Spawning a task creates a work item, so Basic mode never offers it.
        if (!CanCreateWorkItems) return;
        if (!Settings.Planning.OfferTaskForUntrackable) return;

        var item = WorkItems.FirstOrDefault(i => i.Id == allocation.WorkItemId);
        if (item is null || item.TracksTime) return;

        SpawnFor = allocation;
        SpawnParent = item;
        Changed?.Invoke();
    }

    public void CancelSpawn()
    {
        if (SpawnFor is null) return;
        SpawnFor = null;
        SpawnParent = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Creates the Task in Azure DevOps and points the calendar block at it, so the block
    /// now stands for something time can actually be booked against.
    /// </summary>
    public async Task<bool> SpawnTaskAsync(string title, string description, string type, double? remainingHours)
    {
        if (SpawnFor is not { } allocation || SpawnParent is not { } parent || IsSpawning) return false;

        // Counted: a task created but never pointed at would be offered, and created, again.
        if (!TryBeginWrite())
        {
            toasts.Error("Could not create that task", RestartingForUpdate);
            return false;
        }

        IsSpawning = true;
        Changed?.Invoke();

        try
        {
            var created = await ado.CreateChildAsync(parent, title, description, type, remainingHours);

            // Make it visible in the sidebar straight away; the query behind the list may
            // not include it (it could be unassigned, or outside the current filter).
            if (WorkItems.All(i => i.Id != created.Id)) WorkItems.Add(created);

            planner.Repoint(allocation.Id, created);

            toasts.Success($"Created #{created.Id} under #{parent.Id}",
                "The calendar block now points at the new task, so time can be recorded against it.");

            SpawnFor = null;
            SpawnParent = null;
            return true;
        }
        catch (Exception ex)
        {
            toasts.Error("Could not create that task", ex.Message);
            return false;
        }
        finally
        {
            IsSpawning = false;
            EndWrite();
            Changed?.Invoke();
        }
    }

    // ---------------------------------------------------------------- background refresh

    private Timer? _poll;
    private Timer? _workItemPoll;

    /// <summary>
    /// Re-reads the calendar on a timer so changes made in Outlook show up without the user
    /// having to ask. Safe to call repeatedly; it reconfigures itself from settings.
    /// </summary>
    private bool _watchingPlan;

    public void ConfigurePolling()
    {
        // Stopped for a handover, and only started again if it is abandoned.
        if (IsHandingOver) return;

        // Every edit to the plan arms the debounce. Subscribed here because this is where the
        // rest of the timers are set up, and guarded so repeated calls do not stack handlers.
        if (!_watchingPlan)
        {
            planner.Changed += NudgeAutoSync;
            _watchingPlan = true;
        }

        _poll?.Dispose();
        _poll = null;

        var minutes = Settings.Planning.RefreshMinutes;
        if (minutes > 0 && Settings.IsCalendarConfigured)
        {
            var period = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 120));
            _poll = new Timer(_ => _ = LoadEventsAsync(), null, period, period);
        }

        _workItemPoll?.Dispose();
        _workItemPoll = null;

        var seconds = Settings.Planning.WorkItemRefreshSeconds;
        if (seconds > 0 && Settings.IsAdoConfigured)
        {
            var period = TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 3600));
            _workItemPoll = new Timer(_ => _ = RefreshWorkItemsQuietlyAsync(), null, period, period);
        }
    }

    // ---------------------------------------------------------------- handing over to an update

    /// <summary>Why something was refused while an update is taking over.</summary>
    private const string RestartingForUpdate = "Slate is restarting to install an update.";

    private volatile bool _handingOver;
    private int _writesInFlight;

    /// <summary>
    /// True from the moment an update starts putting a new copy in this one's place until
    /// this copy exits, or the update is abandoned. Nothing new goes to Azure DevOps, Outlook
    /// or the plan meanwhile: the new copy reads the plan as it starts, so a write made here
    /// after that is lost to it - and a booking or an event made remotely but not yet written
    /// down here is one the new copy would make a second time.
    /// </summary>
    public bool IsHandingOver => _handingOver;

    /// <summary>
    /// Counts in a write to Azure DevOps, Outlook or the plan, unless a handover has begun;
    /// every true must be paired with an <see cref="EndWrite"/>. Counting first and looking at
    /// the flag second - the mirror image of <see cref="PrepareForHandoverAsync"/> - means a
    /// write starting at the same instant as a handover is either refused here or waited for
    /// there, whichever threads the two are on.
    /// </summary>
    private bool TryBeginWrite()
    {
        Interlocked.Increment(ref _writesInFlight);
        if (!_handingOver) return true;

        Interlocked.Decrement(ref _writesInFlight);
        return false;
    }

    private void EndWrite() => Interlocked.Decrement(ref _writesInFlight);

    /// <summary>
    /// Brings this copy to a standstill before an update moves any files: the timers stop,
    /// nothing new may start, and what is already under way is waited for rather than cut
    /// off by the shutdown at the end of the handover. False when something is still going
    /// when <paramref name="timeout"/> is up, which abandons the update - an unfinished write
    /// is exactly what this is here to protect.
    /// </summary>
    public async Task<bool> PrepareForHandoverAsync(TimeSpan timeout)
    {
        // Seen by every thread before the count below is read; see TryBeginWrite.
        _handingOver = true;
        Interlocked.MemoryBarrier();

        _poll?.Dispose();
        _poll = null;
        _workItemPoll?.Dispose();
        _workItemPoll = null;
        _autoSync?.Dispose();
        _autoSync = null;
        Changed?.Invoke();

        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _writesInFlight) > 0)
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100);
        }

        return true;
    }

    /// <summary>
    /// Picks up where <see cref="PrepareForHandoverAsync"/> left off when the update did not
    /// go ahead, so this copy carries on exactly as before: the timers come back, and any
    /// edit that was waiting to reach Outlook is sent.
    /// </summary>
    public void ResumeAfterFailedHandover()
    {
        if (!_handingOver) return;

        _handingOver = false;
        ConfigurePolling();
        NudgeAutoSync();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- selection

    public Guid? SelectedAllocationId { get; private set; }

    public void SelectAllocation(Guid? id)
    {
        SelectedAllocationId = id;
        Changed?.Invoke();
    }

    public Allocation? SelectedAllocation =>
        SelectedAllocationId is Guid id ? planner.Find(id) : null;
}

internal static class CancellationExtensions
{
    /// <summary>Cancels and disposes a token source, tolerating one that is already disposed.</summary>
    public static async Task CancelAndDisposeAsync(this CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
        catch (ObjectDisposedException) { }
    }
}

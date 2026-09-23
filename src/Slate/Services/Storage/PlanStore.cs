using System.Text.Json;
using System.Text.Json.Serialization;
using Slate.Models;

namespace Slate.Services.Storage;

/// <summary>Persists the set of time allocations. Writes are debounced and atomic.</summary>
public sealed class PlanStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();
    private PlanFile? _cached;

    /// <summary>
    /// Why the plan on disk could not be read, when it could not, for the one notice that
    /// says so. Null on an ordinary start, including the first one of all.
    ///
    /// Reading it loads the plan if nothing has yet, so that asking the question at startup
    /// gets the answer rather than the moment before it.
    /// </summary>
    public string? LoadProblem
    {
        get
        {
            _ = Cached;
            return _loadProblem;
        }
    }

    private string? _loadProblem;

    /// <summary>
    /// True when the file that is there must not be written over: it exists, it could not be
    /// read, and it could not be put safely aside either. Everything in it - the blocks, the
    /// entries, and the record of which bookings and undos were never confirmed - is still in
    /// that file, and saving an empty plan on top would be the end of it.
    ///
    /// Set once by the load and never cleared: nothing re-reads the file for the rest of the
    /// session.
    /// </summary>
    private bool _refuseToSave;

    /// <summary>
    /// False for the whole session once the plan on disk has been ruled unwritable by
    /// <see cref="_refuseToSave"/>: <see cref="Save"/> then writes nothing at all, so nothing
    /// this copy does is recorded anywhere.
    ///
    /// Exposed because that is not a thing to find out about afterwards. Booking time in Azure
    /// DevOps writes hours onto a work item and nothing else; the record that says so - the
    /// pin written before the send, and the entry written after it - lives only in this file.
    /// A session that cannot write it would leave hours on work items with nothing pointing at
    /// them, and offer the same blocks again next time, so time writes are refused while this
    /// is false rather than allowed to go on unrecorded.
    ///
    /// Reading it loads the plan if nothing has yet, so that asking the question at startup
    /// gets the answer rather than the moment before it.
    /// </summary>
    public bool CanSave
    {
        get
        {
            _ = Cached;
            return !_refuseToSave;
        }
    }

    public List<Allocation> All => Cached.Allocations;

    public List<TimeEntry> TimeEntries => Cached.TimeEntries;

    public List<UnconfirmedBooking> UnconfirmedBookings => Cached.UnconfirmedBookings;

    public Dictionary<int, int> Priorities => Cached.Priorities;

    public List<string> PendingDeletes => Cached.PendingDeletes;

    /// <summary>Event ids the plan has deliberately let go of, so they are not picked up again.</summary>
    public List<string> Disowned => Cached.Disowned;

    /// <summary>
    /// Appends allocations by swapping in a new list rather than mutating the live one.
    ///
    /// Adoption is the one path that adds blocks off the UI thread - it runs from the
    /// calendar poll's timer callback - while the grid is enumerating the same list to
    /// render. Adding in place there throws "Collection was modified"; replacing the
    /// reference leaves any enumeration already under way looking at an intact list.
    /// </summary>
    public void Append(IReadOnlyList<Allocation> additions)
    {
        if (additions.Count == 0) return;

        lock (_gate)
        {
            var current = Cached;
            var grown = new List<Allocation>(current.Allocations.Count + additions.Count);
            grown.AddRange(current.Allocations);
            grown.AddRange(additions);
            current.Allocations = grown;
        }
    }

    /// <summary>
    /// Runs a change to the plan's own lists holding the lock <see cref="Save"/> holds while
    /// it copies them.
    ///
    /// Every list here is written from the UI thread and read from the two polling timers'
    /// thread-pool callbacks, or the other way round. Save copies each one to take its
    /// snapshot, and a copy reads the count and then takes the items: a list that grew in
    /// between no longer fits, and the save comes apart with it ("Destination array was not
    /// long enough"; "Collection was modified" for the dictionary, which enumerates). On the
    /// calendar path that exception is swallowed into the load's error message, so what was
    /// being saved - the record of a booking about to be sent, among it - is quietly lost.
    /// This is the same protection <see cref="Append"/> gives the blocks, for the rest of
    /// what the file holds.
    /// </summary>
    public T Edit<T>(Func<PlanFile, T> change)
    {
        lock (_gate)
        {
            return change(Cached);
        }
    }

    /// <summary>The same, for a change with nothing to report back.</summary>
    public void Edit(Action<PlanFile> change)
    {
        lock (_gate)
        {
            change(Cached);
        }
    }

    private PlanFile Cached
    {
        get
        {
            lock (_gate)
            {
                return _cached ??= Load();
            }
        }
    }

    private PlanFile Load()
    {
        var path = AppPaths.PlanFile;

        string text;
        try
        {
            if (!File.Exists(path)) return new PlanFile();
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // There is a plan there; this copy simply could not get at it. Carrying on with an
            // empty one is fine - saving over the real one with it is not.
            _refuseToSave = true;
            _loadProblem = $"Your plan could not be read ({ex.Message}). Nothing will be saved over it, and " +
                           "nothing you do now will be written down either - so recording time is refused for " +
                           "this session. Close Slate, make sure nothing else is holding the file, and start " +
                           "it again.";
            CrashLog.WriteLine($"Could not read the plan at {path}: {ex}");
            return new PlanFile();
        }

        PlanFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PlanFile>(text, Json);
        }
        catch (JsonException ex)
        {
            return Unreadable(path, ex);
        }

        if (file is null) return Unreadable(path, null);

        Migrate(file);
        return file;
    }

    /// <summary>
    /// A plan file that will not parse. It is put aside under its own name before anything is
    /// written where it was: it holds every block and every time entry, and - since bookings
    /// and undos Azure DevOps never confirmed live in it too - the only record of which hours
    /// may already be on a work item. One bad value is no reason to lose all of that, and an
    /// empty plan saved on top is exactly how it would be lost.
    /// </summary>
    private PlanFile Unreadable(string path, JsonException? ex)
    {
        var moved = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unreadable";

        try
        {
            File.Move(path, moved, overwrite: true);
            _loadProblem = "Your plan could not be read, so Slate has started a new one. The old file is kept " +
                           $"as {Path.GetFileName(moved)} in the Slate data folder - nothing in it has been lost.";
        }
        catch (Exception move) when (move is IOException or UnauthorizedAccessException)
        {
            _refuseToSave = true;
            _loadProblem = "Your plan could not be read, and it could not be put aside either. Nothing will be " +
                           "saved over it, and nothing you do now will be written down either - so recording " +
                           "time is refused for this session. Close Slate and take a copy of plan.json from the " +
                           "Slate data folder before starting it again.";
            CrashLog.WriteLine($"Could not set the unreadable plan at {path} aside: {move}");
        }

        CrashLog.WriteLine($"The plan at {path} could not be read: {ex?.Message ?? "it held nothing at all"}.");
        return new PlanFile();
    }

    /// <summary>
    /// Plans written before time entries existed carried a running total on the block. Turn
    /// each one into a single entry so nothing already recorded disappears from the new view.
    ///
    /// Also squares up anything a hand-edited or older file left null where a list belongs.
    /// </summary>
    private static void Migrate(PlanFile file)
    {
        // An explicit null in the file comes straight back as null, whatever the property's
        // own default was - and every reader below would then throw on a plan that merely
        // says "UnconfirmedBookings": null.
        file.Allocations ??= [];
        file.TimeEntries ??= [];
        file.UnconfirmedBookings ??= [];
        file.Priorities ??= [];
        file.PendingDeletes ??= [];
        file.Disowned ??= [];
        file.Extra ??= [];

        foreach (var allocation in file.Allocations)
        {
            if (allocation.RecordedMinutes <= 0) continue;
            if (file.TimeEntries.Any(e => e.AllocationId == allocation.Id)) continue;

            file.TimeEntries.Add(new TimeEntry
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
                Hours = Math.Round(allocation.RecordedMinutes / 60.0, 2),
                ReducedRemaining = true,
                RecordedAt = allocation.LastRecordedAt ?? DateTimeOffset.Now,
            });

            allocation.RecordedMinutes = 0;
            allocation.LastRecordedAt = null;
        }
    }

    public void Save()
    {
        AppPaths.EnsureCreated();

        lock (_gate)
        {
            // Read first: the flag below is set by the load, and this can be what triggers it.
            var current = Cached;

            // The one thing worse than not saving this change: saving it over a plan that is
            // still sitting there with everything else in it.
            if (_refuseToSave) return;

            // Version and Extra come from what was read rather than from this copy's own
            // defaults: a plan a newer Slate wrote must not come back from here looking older
            // than it is, nor lose the members that version added.
            var snapshot = new PlanFile
            {
                Version = current.Version,
                Allocations = [.. current.Allocations],
                TimeEntries = [.. current.TimeEntries],
                UnconfirmedBookings = [.. current.UnconfirmedBookings],
                Priorities = new Dictionary<int, int>(current.Priorities),
                PendingDeletes = [.. current.PendingDeletes],
                Disowned = [.. current.Disowned],
                Extra = current.Extra,
            };

            // Serializing inside the lock as well, so two threads cannot both be writing the
            // one temp path and promote each other's half-finished file over the plan.
            var temp = AppPaths.PlanFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Json));
            File.Move(temp, AppPaths.PlanFile, overwrite: true);
        }
    }
}

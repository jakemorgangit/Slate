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

    public List<Allocation> All => Cached.Allocations;

    public List<TimeEntry> TimeEntries => Cached.TimeEntries;

    public Dictionary<int, int> Priorities => Cached.Priorities;

    public List<string> PendingDeletes => Cached.PendingDeletes;

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

    private static PlanFile Load()
    {
        PlanFile file;
        try
        {
            file = File.Exists(AppPaths.PlanFile)
                ? JsonSerializer.Deserialize<PlanFile>(File.ReadAllText(AppPaths.PlanFile), Json) ?? new PlanFile()
                : new PlanFile();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new PlanFile();
        }

        Migrate(file);
        return file;
    }

    /// <summary>
    /// Plans written before time entries existed carried a running total on the block. Turn
    /// each one into a single entry so nothing already recorded disappears from the new view.
    /// </summary>
    private static void Migrate(PlanFile file)
    {
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
            var current = Cached;
            var snapshot = new PlanFile
            {
                Allocations = [.. current.Allocations],
                TimeEntries = [.. current.TimeEntries],
                Priorities = new Dictionary<int, int>(current.Priorities),
                PendingDeletes = [.. current.PendingDeletes],
            };

            // Serializing inside the lock as well, so two threads cannot both be writing the
            // one temp path and promote each other's half-finished file over the plan.
            var temp = AppPaths.PlanFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Json));
            File.Move(temp, AppPaths.PlanFile, overwrite: true);
        }
    }
}

namespace Slate.Models;

/// <summary>On-disk shape of the plan. Versioned so the format can move without losing data.</summary>
public sealed class PlanFile
{
    public int Version { get; set; } = 2;
    public List<Allocation> Allocations { get; set; } = [];
    public List<TimeEntry> TimeEntries { get; set; } = [];

    /// <summary>Work item id to locally-assigned priority. Never written back to Azure DevOps.</summary>
    public Dictionary<int, int> Priorities { get; set; } = [];

    /// <summary>
    /// Outlook event ids whose block has been deleted but whose event still needs removing.
    /// Written down rather than held in memory: closing the app before the next send would
    /// otherwise strand those events on the calendar with nothing left pointing at them.
    /// </summary>
    public List<string> PendingDeletes { get; set; } = [];
}

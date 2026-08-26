namespace Slate.Models;

/// <summary>
/// One booking of time against a work item, made from a calendar block. Entries are kept
/// individually rather than as a running total so each one can be shown, totalled and undone.
/// </summary>
public sealed class TimeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The calendar block this came from. May no longer exist.</summary>
    public Guid AllocationId { get; set; }

    public int WorkItemId { get; set; }
    public string WorkItemTitle { get; set; } = "";
    public string WorkItemType { get; set; } = "";
    public string WorkItemUrl { get; set; } = "";
    public string Project { get; set; } = "";

    /// <summary>The day the time is booked against - snapshotted, so moving the block later does not move the entry.</summary>
    public DateTime Date { get; set; }

    /// <summary>The block's span when the entry was made, for the ghost in the time view.</summary>
    public DateTime Start { get; set; }
    public int BlockMinutes { get; set; }

    /// <summary>Hours actually written to Azure DevOps.</summary>
    public double Hours { get; set; }

    /// <summary>Whether Remaining Work was taken down too, so an undo can put it back.</summary>
    public bool ReducedRemaining { get; set; }

    /// <summary>
    /// The signed change actually made to Completed and Remaining Work. Both fields clamp
    /// at zero, so booking 3h against a work item with 1h remaining only takes 1h off it -
    /// and an undo that added 3h back would leave behind work that never existed.
    /// </summary>
    public double AppliedCompleted { get; set; }
    public double AppliedRemaining { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
    public string Notes { get; set; } = "";

    public DateTime End => Start.AddMinutes(BlockMinutes);

    public int Minutes => (int)Math.Round(Hours * 60);
}

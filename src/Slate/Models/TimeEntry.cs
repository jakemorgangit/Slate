using System.Text.Json.Serialization;

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

    /// <summary>
    /// The note posted to the work item's discussion when this time was booked, as it was
    /// typed. Kept so the entry can say what the hours went on. Undoing the entry does not
    /// retract the comment - a discussion is a record of what was said at the time, and
    /// quietly deleting from it would lose somebody else's reply along with it.
    /// </summary>
    public string Comment { get; set; } = "";

    /// <summary>
    /// An undo of this entry that Azure DevOps never confirmed, kept with what was sent so the
    /// next Undo looks for it first rather than taking the hours off a second time.
    /// </summary>
    public TimeWritePlan? UnconfirmedUndo { get; set; }

    public DateTime End => Start.AddMinutes(BlockMinutes);

    public int Minutes => (int)Math.Round(Hours * 60);
}

/// <summary>How a time write ended, as far as it can be told.</summary>
public enum TimeWriteOutcome
{
    /// <summary>On the work item, and kept here as an entry.</summary>
    Recorded,

    /// <summary>Did not happen, so trying again is safe.</summary>
    Failed,

    /// <summary>
    /// Went out, and neither the answer nor a look at the work item afterwards said whether it
    /// landed. Not to be sent again blindly: the hours may already be there.
    /// </summary>
    Unconfirmed,
}

/// <summary>
/// One change to a work item's time, as it was sent: the revision it was pinned to, and
/// Completed and Remaining Work as they stood then and as it leaves them. The pin is what
/// makes a lost answer recoverable - a pinned write can only ever become the very next
/// revision, so that one revision says whether it landed.
/// </summary>
public sealed record TimeWritePlan(
    int WorkItemId,
    int Rev,
    double CompletedBefore,
    double CompletedAfter,
    double RemainingBefore,
    double RemainingAfter,
    bool SetsRemaining,
    DateTimeOffset SentAt)
{
    [JsonIgnore]
    public double AppliedCompleted => CompletedAfter - CompletedBefore;

    [JsonIgnore]
    public double AppliedRemaining => SetsRemaining ? RemainingAfter - RemainingBefore : 0;
}

/// <summary>
/// A booking that went to Azure DevOps without an answer, where checking the work item
/// afterwards could not settle whether it landed. Written down so the block is not offered
/// again as if nothing had happened - on the next opening, or after a restart - and so a
/// later check can still settle it, filing the entry if the time did go on.
/// </summary>
public sealed class UnconfirmedBooking
{
    /// <summary>The entry the booking would have made, ready to file as it is if it did land.</summary>
    public TimeEntry Entry { get; set; } = new();

    /// <summary>What was sent, which is what a later check looks for on the work item.</summary>
    public TimeWritePlan? Plan { get; set; }
}

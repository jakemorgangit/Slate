using System.Text.Json;
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

    /// <summary>
    /// The Azure DevOps organization these hours were booked to. Work item numbers are only
    /// unique within an organization, so without this an Undo made after switching to another
    /// one would take hours off whatever #7 happens to be over there. The project is not part
    /// of it: ids do not repeat across the projects of one organization, and the time endpoints
    /// are organization-scoped, so narrowing it further would only refuse honest undos.
    ///
    /// Empty on entries written before this was kept, and on ones whose organization could not
    /// be worked out; those are acted on as before rather than being stranded.
    /// </summary>
    public string Organization { get; set; } = "";

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

    /// <summary>
    /// Anything in the saved entry this copy does not know about, kept so it survives a save.
    /// A newer Slate can add fields, and an older one that reads and writes the plan after a
    /// rollback would otherwise quietly drop them - including the ones that say a booking or
    /// an undo was never confirmed, which is exactly how the same hours get booked twice.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];

    public DateTime End => Start.AddMinutes(BlockMinutes);

    public int Minutes => (int)Math.Round(Hours * 60);

    /// <summary>
    /// Whether these hours belong to the Azure DevOps organization given - the one an undo,
    /// a check or a settle is about to act through.
    ///
    /// The stamp answers it outright. Entries written before there was one are judged on the
    /// work item link they were kept with, which begins with the organization they were read
    /// from, so switching organization does not strand them either. Only an entry that says
    /// nothing at all is taken on trust: refusing those would make old entries impossible to
    /// undo for no better reason than that they are old.
    /// </summary>
    public bool BelongsTo(string organization)
    {
        var wanted = NormaliseOrganization(organization);
        if (wanted.Length == 0) return true;

        if (Organization.Length > 0)
            return string.Equals(NormaliseOrganization(Organization), wanted, StringComparison.OrdinalIgnoreCase);

        return WorkItemUrl.Length == 0
               || WorkItemUrl.StartsWith(wanted + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One spelling of an organization URL, so two ways of writing the same one match.</summary>
    public static string NormaliseOrganization(string organizationUrl) =>
        organizationUrl.Trim().TrimEnd('/');
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
    /// <summary>
    /// How long a change that has not shown up on the work item is still given to arrive
    /// before it is called lost - well past the client's own timeout, and past anything a
    /// gateway would sit on a request for.
    /// </summary>
    public static readonly TimeSpan LandingWindow = TimeSpan.FromMinutes(5);

    [JsonIgnore]
    public double AppliedCompleted => CompletedAfter - CompletedBefore;

    [JsonIgnore]
    public double AppliedRemaining => SetsRemaining ? RemainingAfter - RemainingBefore : 0;

    /// <summary>
    /// True while a send of this could still be on its way, so nothing may decide on its
    /// behalf that it never went on.
    /// </summary>
    [JsonIgnore]
    public bool CouldStillLand => DateTimeOffset.Now - SentAt < LandingWindow;
}

/// <summary>
/// A booking that went to Azure DevOps without an answer, where checking the work item
/// afterwards could not settle whether it landed. Written down so the block is not offered
/// again as if nothing had happened - on the next opening, or after a restart - and so a
/// later check can still settle it, filing the entry if the time did go on.
///
/// It is written down before the change is sent rather than after it comes back, so a copy
/// that dies between the two still leaves the next one something to settle.
/// </summary>
public sealed class UnconfirmedBooking
{
    /// <summary>The entry the booking would have made, ready to file as it is if it did land.</summary>
    public TimeEntry Entry { get; set; } = new();

    /// <summary>What was sent, which is what a later check looks for on the work item.</summary>
    public TimeWritePlan? Plan { get; set; }

    /// <summary>
    /// True while the write this was written down for is still going in this copy. Until it
    /// comes back there is nothing to tell anyone and nothing to check - this is not an
    /// unconfirmed booking yet, only one in flight. Never saved: a copy that finds one of
    /// these in the plan file is the next one along, and to it the write really is unsettled.
    /// </summary>
    [JsonIgnore]
    public bool InFlight { get; set; }

    /// <summary>
    /// True once no send of it can still be arriving, so letting it go cannot be overtaken by
    /// it landing straight afterwards.
    /// </summary>
    [JsonIgnore]
    public bool CanBeLetGo => !InFlight && (Plan is not { } plan || !plan.CouldStillLand);
}

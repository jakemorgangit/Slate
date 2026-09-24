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

    /// <summary>
    /// The id Azure DevOps gives that organization - connectionData's instanceId. Kept beside
    /// the URL because the URL is not an identity: a rename, Microsoft's own move from
    /// {org}.visualstudio.com to dev.azure.com, or a new host name in front of an Azure DevOps
    /// Server collection all change it while the organization stays the one these hours are on.
    /// The id does not move, so it is preferred whenever both sides have one.
    ///
    /// Empty on entries written before this was kept, and whenever the id could not be read;
    /// the URL then decides, as it did before.
    /// </summary>
    public string OrganizationId { get; set; } = "";

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
    /// typed. Kept so the entry can say what the hours went on.
    /// </summary>
    public string Comment { get; set; } = "";

    /// <summary>
    /// The id Azure DevOps gave that note in the work item's discussion, so undoing these
    /// hours can offer to take the comment off with them: hours reversed while the note that
    /// went with them still stands reads as work that was done.
    ///
    /// One comment can be several entries' note. A day booked in one pass posts a work item's
    /// note once however many of its blocks are booked, and from this version on every entry it
    /// covers is given the same id - so the note comes off with the last of the hours it speaks
    /// for rather than with whichever happened to post it. The undo that finds others still
    /// carrying the id says what the comment also stands for before offering to delete it.
    ///
    /// Zero whenever there is nothing here to remove with, which is not the same as there being
    /// no note: an entry written before this was kept has the text and no id, so does one
    /// written while only the posting entry was given it, so does one whose note never reached
    /// the discussion, and so does one whose comment has already gone with an earlier undo of
    /// another block the same note covered.
    /// </summary>
    public int CommentId { get; set; }

    /// <summary>
    /// The project the comment was posted under. The comments API is project-scoped and the
    /// project used is not always the one on the block - an empty one falls back to whatever
    /// project is selected - so it is kept as it was used rather than worked out again at the
    /// undo, by which time the selection may have moved on and the address would name a
    /// project the comment was never in.
    /// </summary>
    public string CommentProject { get; set; } = "";

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
    /// The ids settle it outright when both sides carry one, because they are the only part
    /// of an organization that does not move. Failing that the stamped URL is compared, in
    /// the one spelling <see cref="NormaliseOrganization"/> reduces every address to. Entries
    /// written before there was a stamp are judged on the work item link they were kept with,
    /// which was built from the organization they were read from. Only an entry that says
    /// nothing at all is taken on trust: refusing those would make old entries impossible to
    /// undo for no better reason than that they are old.
    /// </summary>
    public bool BelongsTo(OrganizationRef organization)
    {
        if (OrganizationId.Length > 0 && organization.Id.Length > 0)
            return string.Equals(OrganizationId, organization.Id, StringComparison.OrdinalIgnoreCase);

        var wanted = organization.Url;
        if (wanted.Length == 0) return true;

        if (Organization.Length > 0) return NormaliseOrganization(Organization) == wanted;

        if (WorkItemUrl.Length == 0) return true;

        var from = NormaliseOrganization(WorkItemUrl);
        return from == wanted || from.StartsWith(wanted + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// One spelling of an organization URL, so the same organization reached by another
    /// address still matches. The scheme, any credential in front of the host and the case
    /// are dropped, as is everything past the organization itself - a project left in the URL,
    /// most often - and Azure DevOps' two names for one hosted organization,
    /// {org}.visualstudio.com and dev.azure.com/{org}, come out the same. That last pair is
    /// Microsoft's own migration, which moved every existing entry's address underneath it.
    ///
    /// A host it does not know keeps its path intact. On Azure DevOps Server the collection
    /// can sit at any depth - /tfs/DefaultCollection as often as /DefaultCollection - and each
    /// collection on a server has its own #7, so guessing where to cut is the one mistake here
    /// worth avoiding. A server that changed address is what <see cref="OrganizationId"/> is
    /// for.
    /// </summary>
    public static string NormaliseOrganization(string organizationUrl)
    {
        var text = organizationUrl.Trim().TrimEnd('/');
        if (text.Length == 0) return "";

        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];

        text = text.ToLowerInvariant();

        var slash = text.IndexOf('/');
        var host = slash < 0 ? text : text[..slash];
        var path = slash < 0 ? "" : text[(slash + 1)..];

        // A personal access token pasted into the address lives in front of the host.
        var at = host.LastIndexOf('@');
        if (at >= 0) host = host[(at + 1)..];

        const string legacyHost = ".visualstudio.com";
        if (host.EndsWith(legacyHost, StringComparison.Ordinal))
            return HostedOrganization(host[..^legacyHost.Length]);

        if (host == "dev.azure.com")
        {
            var end = path.IndexOf('/');
            return HostedOrganization(end < 0 ? path : path[..end]);
        }

        return path.Length == 0 ? host : host + "/" + path;
    }

    /// <summary>
    /// The one name a hosted organization is known by here. Spelled as a dev.azure.com
    /// address rather than the bare name so that it cannot collide with a single-label
    /// server of the same name on someone's network.
    /// </summary>
    private static string HostedOrganization(string name) =>
        name.Length == 0 ? "dev.azure.com" : "dev.azure.com/" + name;
}

/// <summary>
/// An Azure DevOps organization as far as anything here needs to know it: the address it is
/// reached at, in the one spelling <see cref="TimeEntry.NormaliseOrganization"/> gives, and
/// the id the service itself gives it. Either may be empty - the id until connectionData has
/// been read, the URL until one is configured - and what is known is what gets compared.
/// </summary>
public sealed record OrganizationRef(string Url, string Id)
{
    /// <summary>Nothing known, which every entry is taken to belong to rather than stranded.</summary>
    public static readonly OrganizationRef None = new("", "");

    public static OrganizationRef For(string organizationUrl, string id) =>
        new(TimeEntry.NormaliseOrganization(organizationUrl), id.Trim());
}

/// <summary>
/// The comment a booking's note was posted as, and the project it went under - between them,
/// everything it takes to address it again.
///
/// Handed from the booking that posted a note to the rest of a batch that shares it, so every
/// entry the one comment covers is written down carrying it. Never a default value: a note
/// posted to no project could not be addressed at all, so "nothing posted" is a null of this
/// rather than an instance saying nothing.
/// </summary>
public readonly record struct TimeNote(int CommentId, string Project);

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
    DateTimeOffset SentAt,
    DateTimeOffset FirstSentAt = default)
{
    /// <summary>
    /// Anything in the saved plan this copy does not know about, kept so it survives a save -
    /// see <see cref="PlanFile.Extra"/>. This is the record that says what went out, so a
    /// member of it dropped on the way through an older copy is exactly how a lost answer
    /// stops being settleable.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];

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
    /// behalf that it never went on. Counted from the last send: a later one extends the wait.
    /// </summary>
    [JsonIgnore]
    public bool CouldStillLand => DateTimeOffset.Now - SentAt < LandingWindow;

    /// <summary>
    /// The earliest moment a revision could be this change. Nothing made before the first send
    /// can be it, whatever it moved - the same hours booked from another machine, most likely.
    /// Falls back to the last send for plans written before this was kept.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset SendWindowStart => FirstSentAt == default ? SentAt : FirstSentAt;
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
    /// Anything in the saved plan this copy does not know about, kept so it survives a save -
    /// see <see cref="PlanFile.Extra"/>. Without it, an older copy putting the plan back after
    /// a failed handover would drop whatever a newer one had added to the very record that
    /// says these hours may already be on the work item.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];

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

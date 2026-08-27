using System.Globalization;
using System.Text.Json.Serialization;

namespace Slate.Models;

public enum SyncState { Draft, Synced, Modified, Failed, Missing }

/// <summary>A block of time the user has set aside for a work item.</summary>
public sealed class Allocation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int WorkItemId { get; set; }

    // Snapshotted so the board still renders before (or without) an Azure DevOps fetch.
    public string WorkItemTitle { get; set; } = "";
    public string WorkItemType { get; set; } = "";
    public string WorkItemState { get; set; } = "";
    public string WorkItemUrl { get; set; } = "";
    public string Project { get; set; } = "";

    public DateTime Start { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public string Notes { get; set; } = "";

    // Outlook linkage
    public string? OutlookEventId { get; set; }
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>Hash of the fields we push to Graph, captured at the last successful sync.</summary>
    public string? SyncedFingerprint { get; set; }

    public string? LastError { get; set; }

    /// <summary>Set by two-way sync when the linked Outlook event has been deleted there.</summary>
    public bool MissingInOutlook { get; set; }

    /// <summary>
    /// Legacy running total from before time entries were kept individually. Only read once,
    /// to migrate old plan files; <see cref="TimeEntry"/> is the source of truth now.
    /// </summary>
    public int RecordedMinutes { get; set; }

    /// <summary>
    /// True when this block was rebuilt from an event whose payload had to clip the title or
    /// notes. The full text is still on the event, so this copy must never write its
    /// shortened version back over it.
    /// </summary>
    public bool TextIsPartial { get; set; }

    /// <summary>
    /// Time already booked against this block somewhere else, carried on the event. Recording
    /// lives in the plan file, which does not travel, so without this a second machine shows
    /// nothing recorded and invites the same hours being logged to Azure DevOps twice.
    /// </summary>
    public int RecordedElsewhereMinutes { get; set; }
    public DateTimeOffset? LastRecordedAt { get; set; }

    [JsonIgnore]
    public DateTime End => Start.AddMinutes(DurationMinutes);

    [JsonIgnore]
    public SyncState State =>
        LastError is not null ? SyncState.Failed
        : MissingInOutlook ? SyncState.Missing
        : OutlookEventId is null ? SyncState.Draft
        : SyncedFingerprint == Fingerprint() ? SyncState.Synced
        : SyncState.Modified;



    /// <summary>
    /// Cheap change-detector over the allocation fields that end up on the calendar event -
    /// including the ones the subject template and the event body draw on, so a work item
    /// changing state or type marks its block for re-sending rather than going unnoticed.
    ///
    /// The time is written as invariant wall clock rather than round-trip "O": the latter
    /// carries an offset only for a Local kind, so the same block would hash differently
    /// depending on where its time came from, and again either side of a clock change.
    /// </summary>
    public string Fingerprint() =>
        string.Join('|',
            // Bumped when the shape of what gets written to the event changes, so blocks
            // that are already Synced re-send once and pick the new shape up. Without it a
            // plan made before portable blocks existed would never carry the payload that
            // makes them portable, and nothing in the app could make it.
            EventShape,
            WorkItemId,
            WorkItemTitle,
            WorkItemType,
            WorkItemState,
            Project,
            WorkItemUrl,
            Start.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            DurationMinutes,
            Notes);

    /// <summary>Version of the event payload this app writes. See <see cref="Fingerprint"/>.</summary>
    private const string EventShape = "v2";

    public bool Overlaps(DateTime start, DateTime end) => Start < end && start < End;

    public Allocation Clone() => (Allocation)MemberwiseClone();
}

/// <summary>An event already on the user's Outlook calendar, shown as background context.</summary>
public sealed record ExistingEvent(
    string Id,
    string Subject,
    DateTime Start,
    DateTime End,
    string ShowAs,
    bool IsAllDay,
    bool IsFromThisApp,
    Guid? AllocationId,
    string? Payload,
    DateTimeOffset? LastModified)
{
    /// <summary>True when this event should block planning on top of it.</summary>
    public bool BlocksTime => !IsFromThisApp && !IsAllDay && ShowAs is not "free";
}

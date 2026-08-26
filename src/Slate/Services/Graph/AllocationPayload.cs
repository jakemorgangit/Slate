using System.Text.Json;
using System.Text.Json.Serialization;
using Slate.Models;

namespace Slate.Services.Graph;

/// <summary>
/// The block, small enough to ride on a calendar event as an extended property.
///
/// This is what makes a plan portable. The plan file lives on one machine; the calendar is
/// the thing both machines can see, so a block carries enough of itself in its own event for
/// any copy of the app to rebuild it - and therefore to move, resize or delete it - without
/// ever having seen the file it was created in.
///
/// Property names are single letters and the text is capped, because Outlook is being asked
/// to store this on every event and there is no reason to spend more of it than necessary.
/// </summary>
public sealed class AllocationPayload
{
    private const int MaxTitle = 160;
    private const int MaxNotes = 240;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Allocation id.</summary>
    [JsonPropertyName("a")] public string? A { get; set; }

    /// <summary>Work item id.</summary>
    [JsonPropertyName("w")] public int W { get; set; }

    [JsonPropertyName("t")] public string? T { get; set; }
    [JsonPropertyName("y")] public string? Y { get; set; }
    [JsonPropertyName("s")] public string? S { get; set; }
    [JsonPropertyName("p")] public string? P { get; set; }
    [JsonPropertyName("u")] public string? U { get; set; }
    [JsonPropertyName("n")] public string? N { get; set; }

    public static string Write(Allocation allocation) =>
        JsonSerializer.Serialize(new AllocationPayload
        {
            A = allocation.Id.ToString(),
            W = allocation.WorkItemId,
            T = Clip(allocation.WorkItemTitle, MaxTitle),
            Y = Empty(allocation.WorkItemType),
            S = Empty(allocation.WorkItemState),
            P = Empty(allocation.Project),
            U = Empty(allocation.WorkItemUrl),
            N = Clip(allocation.Notes, MaxNotes),
        }, Json);

    /// <summary>
    /// Rebuilds a block from an event. The event's own start and length win: somebody may
    /// have dragged it in Outlook since, and what the calendar says now is the truth.
    /// </summary>
    public static Allocation? Read(string? payload, string eventId, DateTime start, int minutes)
    {
        if (string.IsNullOrWhiteSpace(payload) || minutes <= 0) return null;

        AllocationPayload? read;
        try
        {
            read = JsonSerializer.Deserialize<AllocationPayload>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (read is null || read.W <= 0) return null;

        var allocation = new Allocation
        {
            Id = Guid.TryParse(read.A, out var id) ? id : Guid.NewGuid(),
            WorkItemId = read.W,
            WorkItemTitle = read.T ?? "",
            WorkItemType = read.Y ?? "",
            WorkItemState = read.S ?? "",
            Project = read.P ?? "",
            WorkItemUrl = read.U ?? "",
            Notes = read.N ?? "",
            Start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
            DurationMinutes = minutes,
            OutlookEventId = eventId,
            SyncedAt = DateTimeOffset.Now,
        };

        // Adopted already matching what is in the calendar, so it reads as Synced rather
        // than immediately queueing a pointless write back to the event it came from.
        allocation.SyncedFingerprint = allocation.Fingerprint();
        return allocation;
    }

    private static string? Empty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= max ? value
        : value[..max];
}

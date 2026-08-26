using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Slate.Models;
using Slate.Services.Auth;
using Slate.Services.Storage;

namespace Slate.Services.Graph;

public sealed class GraphException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// The HTTP status behind this failure, when there was a response to read one from.
    /// Callers branch on this rather than on the wording of <see cref="Exception.Message"/>.
    /// </summary>
    public HttpStatusCode? Status { get; init; }
}

public sealed record GraphCalendar(string Id, string Name, bool IsDefault, bool CanEdit);

public sealed record GraphProfile(string DisplayName, string Mail);

/// <summary>
/// Minimal Microsoft Graph client covering just the calendar surface this app needs.
/// Events created here carry an extended property so the app can recognise its own bookings.
/// </summary>
public sealed class GraphCalendarClient(SettingsStore settings, MsalAuthService auth)
{
    private const string BaseUrl = "https://graph.microsoft.com/v1.0";

    /// <summary>
    /// Named extended property stamped on every event this app creates, and what ties an
    /// event back to the block that made it. Still carries the app's old name on purpose:
    /// it is already written into every calendar event, and changing it would orphan all of
    /// them from their allocations.
    /// </summary>
    private const string AllocationPropertyId =
        "String {9b3c1f2e-6d4a-4c1b-8f77-2a5d1e0c7b41} Name WorkItemPlannerAllocationId";

    /// <summary>
    /// Everything needed to rebuild the block from the event alone, so a machine that has
    /// never seen the plan file can still show it, move it and delete it. The id above says
    /// which block an event belongs to; this says what that block actually was.
    /// </summary>
    private const string PayloadPropertyId =
        "String {9b3c1f2e-6d4a-4c1b-8f77-2a5d1e0c7b41} Name SlateAllocationPayload";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Graph accepts Windows time zone ids, which is exactly what TimeZoneInfo gives us here.</summary>
    private static string LocalTimeZoneId => TimeZoneInfo.Local.Id;

    // ---------------------------------------------------------------- transport

    private async Task<JsonDocument?> SendAsync(
        HttpMethod method, string url, object? body, CancellationToken ct, bool preferTimeZone = false)
    {
        const int maxAttempts = 4;

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetGraphTokenAsync(ct));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (preferTimeZone)
                request.Headers.TryAddWithoutValidation("Prefer", $"outlook.timezone=\"{LocalTimeZoneId}\"");

            if (body is not null)
                request.Content = JsonContent.Create(body, options: Json);

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new GraphException($"Could not reach Microsoft Graph. {ex.Message}", ex);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                    && attempt < maxAttempts)
                {
                    var wait = response.Headers.RetryAfter?.Delta
                               ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(wait, ct);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NoContent) return null;

                var payload = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                    throw new GraphException(DescribeFailure(response, payload)) { Status = response.StatusCode };

                if (string.IsNullOrWhiteSpace(payload)) return null;

                try
                {
                    return JsonDocument.Parse(payload);
                }
                catch (JsonException ex)
                {
                    throw new GraphException(
                        "Microsoft Graph returned a response this app could not read. " + ex.Message, ex);
                }
            }
        }
    }

    private static string DescribeFailure(HttpResponseMessage response, string payload)
    {
        var detail = payload;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
                detail = message.GetString() ?? payload;
        }
        catch (JsonException)
        {
            // Keep the raw body.
        }

        if (detail.Length > 400) detail = detail[..400] + "...";

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Microsoft rejected the sign-in (401). Sign out and back in from Settings.",
            HttpStatusCode.Forbidden => $"Graph denied the request (403). The app needs the Calendars.ReadWrite permission. {detail}",
            HttpStatusCode.NotFound => $"Not found (404). The calendar or event may have been deleted in Outlook. {detail}",
            _ => $"Microsoft Graph returned {(int)response.StatusCode}. {detail}",
        };
    }

    // ---------------------------------------------------------------- public API

    public async Task<GraphProfile> GetProfileAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{BaseUrl}/me?$select=displayName,mail,userPrincipalName", null, ct)
                        ?? throw new GraphException("Graph returned an empty profile.");

        var root = doc.RootElement;
        return new GraphProfile(
            root.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "",
            root.TryGetProperty("mail", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()!
                : root.TryGetProperty("userPrincipalName", out var u) ? u.GetString() ?? "" : "");
    }

    public async Task<List<GraphCalendar>> GetCalendarsAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"{BaseUrl}/me/calendars?$select=id,name,isDefaultCalendar,canEdit&$top=100", null, ct)
            ?? throw new GraphException("Graph returned no calendars.");

        return [.. doc.RootElement.GetProperty("value").EnumerateArray()
            .Select(c => new GraphCalendar(
                c.GetProperty("id").GetString()!,
                c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                c.TryGetProperty("isDefaultCalendar", out var d) && d.GetBoolean(),
                !c.TryGetProperty("canEdit", out var e) || e.GetBoolean()))
            .Where(c => c.CanEdit)];
    }

    /// <summary>Everything already on the calendar between two local times, for the busy overlay.</summary>
    public async Task<List<ExistingEvent>> GetEventsAsync(DateTime localStart, DateTime localEnd, CancellationToken ct = default)
    {
        var calendar = settings.Current.Calendar.CalendarId;
        var scope = string.IsNullOrWhiteSpace(calendar)
            ? "/me/calendarView"
            : $"/me/calendars/{Uri.EscapeDataString(calendar)}/calendarView";

        // The window is sent as an explicit UTC instant. Graph reads these two parameters
        // from the offset in the value and assumes UTC when there is none, so a naive local
        // wall-clock string would slide the whole week by the machine's offset.
        var url = $"{BaseUrl}{scope}" +
                  $"?startDateTime={Instant(localStart)}" +
                  $"&endDateTime={Instant(localEnd)}" +
                  "&$select=id,subject,start,end,showAs,isAllDay,lastModifiedDateTime" +
                  "&$orderby=start/dateTime&$top=250" +
                  $"&$expand=singleValueExtendedProperties($filter=id eq '{Uri.EscapeDataString(AllocationPropertyId)}'" +
                  $" or id eq '{Uri.EscapeDataString(PayloadPropertyId)}')";

        var events = new List<ExistingEvent>();

        // Paging is followed rather than truncated: an event missing from a short page reads
        // to the reconciler as one deleted in Outlook, which would flag good blocks as lost.
        for (var page = 0; url.Length > 0 && page < MaxEventPages; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get, url, null, ct, preferTimeZone: true)
                            ?? throw new GraphException("Graph returned no calendar view.");

            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (!TryReadDateTime(e, "start", out var start) || !TryReadDateTime(e, "end", out var end))
                    continue;

                var allocationId = ReadAllocationId(e);
                var payload = ReadNamedProperty(e, PayloadPropertyId);

                events.Add(new ExistingEvent(
                    e.GetProperty("id").GetString()!,
                    e.TryGetProperty("subject", out var s) ? s.GetString() ?? "(no subject)" : "(no subject)",
                    start,
                    end,
                    e.TryGetProperty("showAs", out var sa) ? sa.GetString() ?? "busy" : "busy",
                    e.TryGetProperty("isAllDay", out var ad) && ad.GetBoolean(),
                    allocationId is not null || payload is not null,
                    allocationId,
                    payload,
                    e.TryGetProperty("lastModifiedDateTime", out var lm) &&
                    DateTimeOffset.TryParse(lm.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var modified) ? modified : null));
            }

            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next)
                  && next.ValueKind == JsonValueKind.String
                ? next.GetString() ?? ""
                : "";
        }

        return events;
    }

    /// <summary>Enough pages to cover any realistic week; a guard against a paging loop.</summary>
    private const int MaxEventPages = 20;

    /// <summary>A local wall-clock time as the unambiguous UTC instant Graph reads.</summary>
    private static string Instant(DateTime local) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local))
            .ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A wall-clock time in the shape Graph pairs with an explicit time zone. Invariant, because
    /// a culture whose time separator is not a colon would otherwise write "09.00.00".
    /// </summary>
    private static string WallClock(DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The allocation this event was created from, or null if the app did not create it.</summary>
    private static Guid? ReadAllocationId(JsonElement e) =>
        Guid.TryParse(ReadNamedProperty(e, AllocationPropertyId), out var id) ? id : null;

    /// <summary>One named extended property off an event, matched by id.</summary>
    private static string? ReadNamedProperty(JsonElement e, string propertyId)
    {
        if (!e.TryGetProperty("singleValueExtendedProperties", out var props) ||
            props.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var property in props.EnumerateArray())
        {
            if (property.TryGetProperty("id", out var id) &&
                string.Equals(id.GetString(), propertyId, StringComparison.OrdinalIgnoreCase) &&
                property.TryGetProperty("value", out var value))
                return value.GetString();
        }

        return null;
    }

    private static bool TryReadDateTime(JsonElement e, string property, out DateTime value)
    {
        value = default;
        return e.TryGetProperty(property, out var node) &&
               node.TryGetProperty("dateTime", out var dt) &&
               DateTime.TryParse(dt.GetString(), CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
                   out var utc) &&
               (value = utc.ToLocalTime()) != default;
    }

    /// <summary>Creates the calendar event for an allocation and returns the new event id.</summary>
    public async Task<string> CreateEventAsync(Allocation allocation, CancellationToken ct = default)
    {
        var calendar = settings.Current.Calendar.CalendarId;
        var scope = string.IsNullOrWhiteSpace(calendar)
            ? "/me/events"
            : $"/me/calendars/{Uri.EscapeDataString(calendar)}/events";

        using var doc = await SendAsync(HttpMethod.Post, BaseUrl + scope, BuildEventBody(allocation, includeIdentity: true), ct)
                        ?? throw new GraphException("Outlook did not return the created event.");

        return doc.RootElement.GetProperty("id").GetString()
               ?? throw new GraphException("Outlook returned an event without an id.");
    }

    public async Task UpdateEventAsync(Allocation allocation, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(allocation.OutlookEventId))
            throw new GraphException("This allocation has no linked Outlook event.");

        var url = $"{BaseUrl}/me/events/{Uri.EscapeDataString(allocation.OutlookEventId)}";
        (await SendAsync(HttpMethod.Patch, url, BuildEventBody(allocation, includeIdentity: false), ct))?.Dispose();
    }

    /// <summary>Deletes the linked event. A 404 is treated as success - it is already gone.</summary>
    public async Task DeleteEventAsync(string eventId, CancellationToken ct = default)
    {
        try
        {
            (await SendAsync(HttpMethod.Delete, $"{BaseUrl}/me/events/{Uri.EscapeDataString(eventId)}", null, ct))?.Dispose();
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // Already removed in Outlook - nothing to do.
        }
    }

    /// <summary>True when the linked event still exists in Outlook.</summary>
    public async Task<bool> EventExistsAsync(string eventId, CancellationToken ct = default)
    {
        try
        {
            (await SendAsync(HttpMethod.Get,
                $"{BaseUrl}/me/events/{Uri.EscapeDataString(eventId)}?$select=id", null, ct))?.Dispose();
            return true;
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private object BuildEventBody(Allocation allocation, bool includeIdentity)
    {
        var cal = settings.Current.Calendar;
        var subject = RenderSubject(cal.SubjectTemplate, allocation, cal.Marker);

        var body = new Dictionary<string, object?>
        {
            ["subject"] = subject,
            ["start"] = new { dateTime = WallClock(allocation.Start), timeZone = LocalTimeZoneId },
            ["end"] = new { dateTime = WallClock(allocation.End), timeZone = LocalTimeZoneId },
            ["showAs"] = cal.ShowAs,
            ["isReminderOn"] = cal.ReminderEnabled,
            ["reminderMinutesBeforeStart"] = Math.Max(0, cal.ReminderMinutes),
            ["sensitivity"] = cal.IsPrivate ? "private" : "normal",
            ["body"] = new { contentType = "html", content = BuildBodyHtml(allocation) },
        };

        if (!string.IsNullOrWhiteSpace(cal.Category))
            body["categories"] = new[] { cal.Category };

        // The payload rides along on every write, not just creation: a block edited here
        // has to leave the calendar telling the same story to whichever machine reads it next.
        body["singleValueExtendedProperties"] = includeIdentity
            ? new[]
            {
                new { id = AllocationPropertyId, value = allocation.Id.ToString() },
                new { id = PayloadPropertyId, value = AllocationPayload.Write(allocation) },
            }
            : [new { id = PayloadPropertyId, value = AllocationPayload.Write(allocation) }];

        return body;
    }

    public static string RenderSubject(string template, Allocation allocation, string marker = "")
    {
        var rendered = (string.IsNullOrWhiteSpace(template) ? "#{id} {title}" : template)
            .Replace("{id}", allocation.WorkItemId.ToString())
            .Replace("{title}", allocation.WorkItemTitle)
            .Replace("{type}", allocation.WorkItemType)
            .Replace("{state}", allocation.WorkItemState)
            .Replace("{project}", allocation.Project)
            .Trim();

        // Outlook truncates very long subjects awkwardly; keep them sane. The marker is
        // trimmed for rather than trimmed off - it is the part another machine looks for.
        var tag = marker.Trim();
        if (tag.Length > 0 && !rendered.Contains(tag, StringComparison.OrdinalIgnoreCase))
        {
            var room = 250 - tag.Length - 1;
            if (rendered.Length > room) rendered = rendered[..Math.Max(0, room - 3)] + "...";
            rendered = $"{rendered} {tag}".Trim();
        }

        return rendered.Length > 250 ? rendered[..247] + "..." : rendered;
    }

    private string BuildBodyHtml(Allocation allocation)
    {
        var cal = settings.Current.Calendar;
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(allocation.Notes))
            lines.Add($"<p>{Escape(allocation.Notes).Replace("\n", "<br/>")}</p>");

        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(allocation.WorkItemType)) facts.Add($"<b>Type:</b> {Escape(allocation.WorkItemType)}");
        if (!string.IsNullOrWhiteSpace(allocation.WorkItemState)) facts.Add($"<b>State:</b> {Escape(allocation.WorkItemState)}");
        if (!string.IsNullOrWhiteSpace(allocation.Project)) facts.Add($"<b>Project:</b> {Escape(allocation.Project)}");
        if (facts.Count > 0) lines.Add($"<p>{string.Join(" &nbsp;|&nbsp; ", facts)}</p>");

        if (cal.IncludeWorkItemLink && !string.IsNullOrWhiteSpace(allocation.WorkItemUrl))
            lines.Add($"<p><a href=\"{Escape(allocation.WorkItemUrl)}\">Open work item #{allocation.WorkItemId} in Azure DevOps</a></p>");

        lines.Add("<p style=\"color:#888;font-size:11px\">Scheduled with Slate</p>");
        return string.Join("\n", lines);

        static string Escape(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}

using System.Text;
using System.Text.RegularExpressions;
using Slate.Models;

namespace Slate.Services.Graph;

/// <summary>
/// What an event's subject alone says about the block behind it.
///
/// The stamp on the event is the fuller record, but it is invisible - nothing in Outlook shows
/// it, and an event can reach a machine without it. The marker is the part anyone can see, so
/// a subject carrying it and naming a work item is taken as Slate's own, whichever machine
/// wrote it, and enough to rebuild a block that can be moved, resized or deleted from here.
/// </summary>
public sealed record MarkedSubject(int WorkItemId, string Title)
{
    /// <summary>The marker every copy of the app has written unless told otherwise.</summary>
    public const string DefaultMarker = "-Slate-";

    private static readonly Regex AnyHashId = new(@"#(\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"(\{(?:id|title|type|state|project)\})", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads a subject written by <see cref="GraphCalendarClient.RenderSubject"/>. Null unless it
    /// carries a marker - this machine's, or the default, since the other machine may never
    /// have changed it - and a work item id can be found in what is left.
    /// </summary>
    public static MarkedSubject? Read(string? subject, string? template, string? marker)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;

        var markers = new[] { marker?.Trim(), DefaultMarker }
            .Where(m => !string.IsNullOrEmpty(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var found = markers.FirstOrDefault(m => subject.Contains(m!, StringComparison.OrdinalIgnoreCase));
        if (found is null) return null;

        var rest = subject.Replace(found, " ", StringComparison.OrdinalIgnoreCase);
        rest = Regex.Replace(rest, @"\s+", " ").Trim();

        // The template this machine writes with first, since it knows where the title ends.
        // The other machine may use a different one, so a bare "#123" anywhere will also do.
        var shaped = TemplatePattern(template).Match(rest);
        if (shaped.Success && int.TryParse(shaped.Groups["id"].Value, out var id) && id > 0)
        {
            var title = shaped.Groups["title"].Success ? shaped.Groups["title"].Value.Trim() : rest;
            return new MarkedSubject(id, title.Length > 0 ? title : rest);
        }

        var loose = AnyHashId.Match(rest);
        return loose.Success && int.TryParse(loose.Groups[1].Value, out var looseId) && looseId > 0
            ? new MarkedSubject(looseId, rest)
            : null;
    }

    /// <summary>
    /// A block to stand for the event, marked as holding only part of its text: the notes and
    /// the full title are still on the event, and this copy must never write over them.
    /// </summary>
    public Allocation ToAllocation(string eventId, DateTime start, int minutes)
    {
        var allocation = new Allocation
        {
            WorkItemId = WorkItemId,
            WorkItemTitle = Title,
            TextIsPartial = true,
            Start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
            DurationMinutes = minutes,
            OutlookEventId = eventId,
            SyncedAt = DateTimeOffset.Now,
        };

        // Matches what is in the calendar already, so it reads as Synced rather than queueing
        // a write back to the event it was just read from.
        allocation.SyncedFingerprint = allocation.Fingerprint();
        return allocation;
    }

    /// <summary>The subject template turned into a pattern that captures {id} and {title}.</summary>
    private static Regex TemplatePattern(string? template)
    {
        var source = string.IsNullOrWhiteSpace(template) ? "#{id} {title}" : template.Trim();
        var pattern = new StringBuilder("^");
        bool haveId = false, haveTitle = false;

        foreach (var part in Placeholder.Split(source))
        {
            switch (part)
            {
                case "":
                    break;
                case "{id}" when !haveId:
                    pattern.Append(@"(?<id>\d+)");
                    haveId = true;
                    break;
                case "{id}":
                    pattern.Append(@"\d+");
                    break;
                case "{title}" when !haveTitle:
                    pattern.Append("(?<title>.*?)");
                    haveTitle = true;
                    break;
                case "{title}" or "{type}" or "{state}" or "{project}":
                    pattern.Append(".*?");
                    break;
                default:
                    pattern.Append(Regex.Replace(Regex.Escape(part), @"(\\ |\s)+", @"\s*"));
                    break;
            }
        }

        return new Regex(pattern.Append('$').ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Slate.Models;

namespace Slate.Services.AzureDevOps;

/// <summary>
/// Moving a work item to another project or area: reading one in full, raising the copy, and
/// carrying the links, the discussion and the attachments across - which is the order
/// <see cref="Planning.WorkItemMigrator"/> calls them in, and the order it explains.
///
/// Everything here reads and writes raw field values rather than the display shapes the details
/// window uses. <see cref="GetWorkItemDetailAsync"/> sanitises markup and rewrites attached
/// images as data URIs so a WebView can render them, both of which are exactly wrong for a copy:
/// the new work item would carry Slate's rendering of the description instead of the description.
/// </summary>
public sealed partial class AzureDevOpsClient
{
    /// <summary>
    /// The phrase the note on a migrated work item leads with. Reading it back is a warning, not
    /// a record - see <see cref="MigrationSource.LooksMigratedTo"/>.
    /// </summary>
    public const string MigratedMarker = "Migrated to #";

    /// <summary>
    /// What a migration writes on the Related link between the copy and the original. Read back
    /// alongside <see cref="MigratedMarker"/> because the link goes on at the second step and the
    /// note only at the sixth: a run stopped in between - or a window closed while a large
    /// attachment was still going over - leaves this and nothing else, and it is the only thing
    /// that can warn the next attempt.
    /// </summary>
    public const string MigratedFromMarker = "Migrated from #";

    /// <summary>
    /// Azure DevOps' own cap on an attachment is 60 MB. Anything larger cannot have come from
    /// this API, but the number is checked anyway rather than trusted: it decides how much of
    /// somebody else's file this app is willing to hold in memory at once.
    /// </summary>
    private const long MaxAttachmentBytes = 60L * 1024 * 1024;

    /// <summary>
    /// A client of its own for attachment bodies. The shared one's sixty seconds is right for
    /// an API call and far too short for a 60 MB file over a VPN, and raising it there would
    /// leave every ordinary read hanging for minutes before it gave up. The five minutes here
    /// is a backstop only: a migration passes its own cancellation token down, so the user's
    /// Cancel is what normally ends a transfer early.
    /// </summary>
    private static readonly HttpClient Files = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = PreferIPv4.ConnectAsync,
    })
    { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// Fields that never travel, with the reason each one stays behind. Shown in the wizard
    /// verbatim, so the wording is the explanation the user reads rather than a code comment.
    /// </summary>
    private static readonly (string Field, string Why)[] NeverCopied =
    [
        ("System.State", "the new work item starts at the first state of its type in the target process; a state named by another process may not exist there"),
        ("System.Reason", "Azure DevOps sets the reason itself from the state"),
        ("System.History", "revision history is built from the changes made to a work item and cannot be written"),
        ("System.Parent", "a parent link cannot cross a project, so the parent is referenced as Related instead"),
        ("System.CreatedBy", "the service records whoever raises the copy, which is you"),
        ("System.CreatedDate", "the service stamps the moment the copy is raised and cannot be back-dated"),
        ("System.ChangedBy", "the service records whoever last changed the copy"),
        ("System.ChangedDate", "the service stamps this itself"),
        ("Microsoft.VSTS.Common.StateChangeDate", "the service stamps this from the state"),
        ("Microsoft.VSTS.Common.ActivatedBy", "the service stamps this from the state"),
        ("Microsoft.VSTS.Common.ActivatedDate", "the service stamps this from the state"),
        ("Microsoft.VSTS.Common.ResolvedBy", "the service stamps this from the state"),
        ("Microsoft.VSTS.Common.ResolvedDate", "the service stamps this from the state"),
        ("Microsoft.VSTS.Common.ClosedBy", "the service stamps this from the state"),
        ("Microsoft.VSTS.Common.ClosedDate", "the service stamps this from the state"),
        ("System.BoardColumn", "board position belongs to the target team's own board"),
        ("System.BoardColumnDone", "board position belongs to the target team's own board"),
        ("System.BoardLane", "board position belongs to the target team's own board"),
    ];

    /// <summary>
    /// Fields Azure DevOps owns outright, left out of the wizard's list as well as out of the
    /// copy: an id, a revision number or a link count is not something a user chose, so naming
    /// it as "not copied" would be noise.
    /// </summary>
    private static readonly HashSet<string> ServiceOwnedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.Rev", "System.Watermark", "System.AreaId", "System.IterationId",
        "System.NodeName", "System.TeamProject", "System.CommentCount", "System.IsDeleted",
        "System.PersonId", "System.AuthorizedAs", "System.AuthorizedDate", "System.RevisedDate",
        "System.AttachedFileCount", "System.ExternalLinkCount", "System.HyperLinkCount",
        "System.RelatedLinkCount", "System.RemoteLinkCount",
    };

    /// <summary>
    /// Fields the wizard sets itself. They do go onto the copy - the type, the area and the
    /// iteration are the whole point of a migration - but their values are the ones chosen on
    /// the first page, so they are shown there rather than in the list of what is carried over.
    /// </summary>
    private static readonly HashSet<string> ChosenInTheWizard = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.WorkItemType", "System.AreaPath", "System.IterationPath",
    };

    /// <summary>
    /// The fields kept when everything else has been refused. Enough for the copy to still be
    /// worth having - it is the text somebody wrote - and plain enough that no process template
    /// can turn them down.
    /// </summary>
    private static readonly HashSet<string> LastResortFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Title", "System.Description", "System.Tags", "System.AreaPath", "System.IterationPath",
    };

    /// <summary>
    /// Reads the work item to be migrated, in full: every field worth copying, its parent and
    /// children, its attachments, and its discussion with the markup untouched.
    /// </summary>
    public async Task<MigrationSource> GetMigrationSourceAsync(int id, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}/_apis/wit/workitems/{id}?$expand=all&api-version={ApiVersion}", null, ct);

        var root = doc.RootElement;
        var fields = root.GetProperty("fields");
        var project = Str(fields, "System.TeamProject");

        var copyable = new List<MigrationFieldValue>();
        var skipped = new List<MigrationSkip>();
        var reasons = NeverCopied.ToDictionary(n => n.Field, n => n.Why, StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields.EnumerateObject())
        {
            if (ServiceOwnedFields.Contains(field.Name) || ChosenInTheWizard.Contains(field.Name)) continue;

            // Per-board fields carry a board's own guid in their name and mean nothing on
            // another team's board, so they are dropped without being listed either.
            if (field.Name.StartsWith("WEF_", StringComparison.OrdinalIgnoreCase)) continue;

            var patchable = PatchValueOf(field.Value);

            // An empty field is not a loss, so it is neither copied nor listed as left behind.
            if (patchable is string text && text.Length == 0) continue;
            if (patchable is null && field.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            // Judged on the value, never on how it reads: a description that is one pasted
            // screenshot flattens to no text at all, and dropping it on that basis would lose
            // the very thing somebody wanted carried over.
            var display = DisplayOf(field.Value);
            if (display.Length == 0) display = "markup only - no text";

            if (reasons.TryGetValue(field.Name, out var why))
            {
                skipped.Add(new MigrationSkip($"{Prettify(field.Name)} ({display})", why));
                continue;
            }

            if (patchable is null)
            {
                skipped.Add(new MigrationSkip(Prettify(field.Name),
                    "Slate could not read that value in a form Azure DevOps would accept back"));
                continue;
            }

            copyable.Add(new MigrationFieldValue(
                field.Name, Prettify(field.Name), patchable, display, IsIdentity(field.Value)));
        }

        // Description first, then the other rich text, then everything else: the same order the
        // details window uses, because it is the order somebody reads a work item in.
        copyable = [.. copyable.OrderBy(MigrationFieldRank).ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase)];

        var discussion = await ReadRawCommentsAsync(id, project, ct);

        if (OtherWorkItemLinks(root) is { } otherLinks) skipped.Add(otherLinks);
        if (discussion.OwnNotes > 0) skipped.Add(OwnNotesSkip(discussion.OwnNotes));
        if (copyable.Any(f => HasPastedPicture(f.Value)) || discussion.Comments.Any(c => HasPastedPicture(c.RawHtml)))
            skipped.Add(PastedPicturesSkip(id, project));

        var parents = LinkedIds(root, "System.LinkTypes.Hierarchy-Reverse");

        return new MigrationSource(
            id,
            Str(fields, "System.Title"),
            Str(fields, "System.WorkItemType"),
            Str(fields, "System.State"),
            project,
            Str(fields, "System.AreaPath"),
            Str(fields, "System.IterationPath"),
            Identity(fields, "System.AssignedTo"),
            BuildWebUrl(project, id),
            copyable,
            skipped,
            parents.Count > 0 ? parents[0] : null,
            LinkedIds(root, "System.LinkTypes.Hierarchy-Forward"),
            ReadAttachments(root),
            discussion.Comments,
            discussion.MigratedTo ?? LinkedCopyId(root),
            discussion.Error,
            discussion.NotCarried);
    }

    /// <summary>
    /// The work item links that are neither the parent nor a child, as one line for the list of
    /// what the copy will not have: Related, Duplicate, Predecessor and Successor, Tests and
    /// Tested By. Named rather than carried over because each of them says something about the
    /// original's place among the work around it, and which of those the copy is still meant to
    /// have is not Slate's judgement to make - the originals keep every one of them, and the
    /// copy's own Related link back to the source is the way to find them again.
    ///
    /// Null when there are none, so no line is shown for a work item that has none.
    /// </summary>
    private static MigrationSkip? OtherWorkItemLinks(JsonElement root)
    {
        if (!root.TryGetProperty("relations", out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        var named = new List<string>();

        foreach (var relation in array.EnumerateArray())
        {
            var rel = relation.TryGetProperty("rel", out var kind) ? kind.GetString() ?? "" : "";
            if (!rel.StartsWith(LinkTypePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var type = rel[LinkTypePrefix.Length..];
            if (type.StartsWith("Hierarchy", StringComparison.OrdinalIgnoreCase)) continue;

            var url = relation.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (TryReadWorkItemId(url, out var other)) named.Add($"{type} #{other}");
        }

        return named.Count == 0
            ? null
            : new MigrationSkip("Its other work item links — " + string.Join(", ", named),
                "only the parent and the children are referenced from the copy. What a Related, Duplicate, "
                + "Predecessor, Successor or Tested By link means is about where this work item sits, so they "
                + "stay on it rather than being guessed at on the copy — which does carry a Related link back "
                + "here, so they are one hop away");
    }

    private const string LinkTypePrefix = "System.LinkTypes.";

    /// <summary>
    /// A note a previous migration of this work item left. Never copied onto the new one: the
    /// copy would then carry a note saying it had been migrated, which reads as it having been
    /// migrated to itself - and the wizard, opened on the copy later, would warn about exactly
    /// that. Listed so the count on the review page and the discussion actually copied agree.
    /// </summary>
    private static MigrationSkip OwnNotesSkip(int count) =>
        new(count == 1 ? "Slate's own migration note" : $"Slate's own {count} migration notes",
            "a note saying this work item was migrated belongs to this work item. Copied over, it would have "
            + "the new one claiming it had been migrated somewhere itself");

    /// <summary>
    /// Pictures pasted into the description or a comment. The markup goes over exactly as it
    /// stands, so each one still points at the file attached to the source - which is why this
    /// is a line in what is not copied rather than a silent success.
    /// </summary>
    private static MigrationSkip PastedPicturesSkip(int id, string project) =>
        new("Pictures pasted into the text",
            $"the markup is copied exactly, so each picture still points at the file on #{id}. Anybody who can "
            + $"see {project} sees them on the copy as well; anybody who can only see the new project sees a "
            + "broken picture and has to be sent the attachment. The files themselves are copied — it is only "
            + "the addresses inside the text that still lead here");

    /// <summary>
    /// True when a value carries an image tag, which for a work item means a pasted screenshot
    /// whose address is an attachment of the work item it was pasted into.
    /// </summary>
    private static bool HasPastedPicture(object? value) =>
        value is string text && text.Contains("<img", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The copy a previous migration raised, from the Related link it puts on rather than from
    /// the note it leaves afterwards. Evidence from the second step of a run instead of the
    /// sixth, which is what makes the warning exist for a run that stopped in the middle - and
    /// the only evidence at all when the discussion could not be read.
    /// </summary>
    private static int? LinkedCopyId(JsonElement root)
    {
        if (!root.TryGetProperty("relations", out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        int? found = null;

        foreach (var relation in array.EnumerateArray())
        {
            if (!relation.TryGetProperty("rel", out var kind)
                || !string.Equals(kind.GetString(), "System.LinkTypes.Related", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!relation.TryGetProperty("attributes", out var attributes)
                || !attributes.TryGetProperty("comment", out var comment)
                || comment.GetString() is not { } text
                || !text.StartsWith(MigratedFromMarker, StringComparison.OrdinalIgnoreCase))
                continue;

            var url = relation.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

            // The last one wins, as with the note: a work item migrated twice went wherever it
            // went last, and Azure DevOps keeps relations in the order they were added.
            if (TryReadWorkItemId(url, out var id)) found = id;
        }

        return found;
    }

    private static int MigrationFieldRank(MigrationFieldValue field) => field.Name switch
    {
        "System.Description" => 0,
        "Microsoft.VSTS.TCM.ReproSteps" => 1,
        "Microsoft.VSTS.Common.AcceptanceCriteria" => 2,
        _ => 3,
    };

    /// <summary>
    /// The work item ids on one kind of link. Ordered as Azure DevOps returned them, which for
    /// children is the order the original was built in.
    /// </summary>
    private static List<int> LinkedIds(JsonElement root, string rel)
    {
        var ids = new List<int>();
        if (!root.TryGetProperty("relations", out var array) || array.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (var relation in array.EnumerateArray())
        {
            if (!relation.TryGetProperty("rel", out var kind)
                || !string.Equals(kind.GetString(), rel, StringComparison.OrdinalIgnoreCase))
                continue;

            var url = relation.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (TryReadWorkItemId(url, out var id) && !ids.Contains(id)) ids.Add(id);
        }

        return ids;
    }

    private static List<MigrationAttachment> ReadAttachments(JsonElement root)
    {
        var files = new List<MigrationAttachment>();
        if (!root.TryGetProperty("relations", out var array) || array.ValueKind != JsonValueKind.Array)
            return files;

        foreach (var relation in array.EnumerateArray())
        {
            if (!relation.TryGetProperty("rel", out var kind)
                || !string.Equals(kind.GetString(), "AttachedFile", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = relation.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (url.Length == 0) continue;

            var name = "attachment";
            var comment = "";
            long size = 0;

            if (relation.TryGetProperty("attributes", out var attributes))
            {
                if (attributes.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } given)
                    name = given;
                if (attributes.TryGetProperty("comment", out var c)) comment = c.GetString() ?? "";
                if (attributes.TryGetProperty("resourceSize", out var s) && s.ValueKind == JsonValueKind.Number)
                    size = s.GetInt64();
            }

            files.Add(new MigrationAttachment(name, url, size, comment));
        }

        return files;
    }

    /// <summary>
    /// What reading a work item's discussion produced: the comments to copy, how many had to be
    /// left out, how many of them were Slate's own migration notes, the copy the last of those
    /// notes named, and - the point of having a shape at all - why there are none, when that is
    /// the answer.
    /// </summary>
    private sealed record RawDiscussion(
        List<MigrationComment> Comments, string? Error, int NotCarried, int OwnNotes, int? MigratedTo);

    /// <summary>How many comments are asked for in one go, which is also Azure DevOps' own cap.</summary>
    private const int CommentPageSize = 200;

    /// <summary>
    /// How many pages of discussion are followed. Two hundred comments at a time, so this is
    /// four thousand: past that the wizard says how many cannot be carried rather than holding
    /// an unbounded amount of somebody else's discussion in memory to copy one comment at a time.
    /// </summary>
    private const int CommentPages = 20;

    /// <summary>
    /// The discussion with its markup exactly as stored - see <see cref="MigrationComment"/> for
    /// why the sanitised, image-inlined form <see cref="GetCommentsAsync"/> returns will not do.
    ///
    /// A discussion that cannot be read comes back with the reason rather than as an empty one.
    /// The two are not the same: an empty discussion means the copy loses nothing, while a read
    /// that failed - throttling, a server error, a dropped connection, or an on-prem server
    /// without the comments endpoint - means the copy may be missing comments nobody has seen,
    /// and the original must not be closed on the strength of it.
    /// </summary>
    private async Task<RawDiscussion> ReadRawCommentsAsync(
        int id, string project, CancellationToken ct)
    {
        var scope = CommentScope(project);
        if (string.IsNullOrWhiteSpace(scope))
            return Settle([], $"Slate has no project to read the discussion of #{id} through.", 0);

        var all = new List<MigrationComment>();
        var total = 0;
        var token = "";

        for (var page = 0; page < CommentPages; page++)
        {
            var url = $"{OrgUrl}{scope}/_apis/wit/workItems/{id}/comments"
                      + $"?$top={CommentPageSize}&api-version={CommentsApiVersion}"
                      + (token.Length == 0 ? "" : "&continuationToken=" + Uri.EscapeDataString(token));

            JsonDocument doc;
            try
            {
                doc = await SendAsync(HttpMethod.Get, url, null, ct);
            }
            catch (AzureDevOpsException ex)
            {
                // Whatever came back before this page stands; the reason goes with it, so the
                // wizard can say the discussion is only part of one.
                return Settle(all, ex.Message, 0);
            }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("totalCount", out var count)
                    && count.ValueKind == JsonValueKind.Number)
                    total = count.GetInt32();

                if (!doc.RootElement.TryGetProperty("comments", out var array)
                    || array.ValueKind != JsonValueKind.Array)
                    break;

                all.AddRange(array.EnumerateArray()
                    .Select(c => new MigrationComment(
                        c.TryGetProperty("id", out var cid) ? cid.GetInt32() : 0,
                        Identity(c, "createdBy"),
                        ReadDate(c, "createdDate"),
                        c.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "")));

                token = doc.RootElement.TryGetProperty("continuationToken", out var next)
                        && next.GetString() is { Length: > 0 } more
                    ? more
                    : "";
            }

            if (token.Length == 0) break;
        }

        // What the service itself said was there, against what arrived. Normally the two agree
        // and this is nothing; a discussion longer than every page followed is what it is for,
        // and the review page then names the number rather than claiming all of them are copied.
        var missing = total > all.Count ? total - all.Count : 0;

        return Settle(all, null, missing);
    }

    /// <summary>
    /// Makes the answer out of the comments as they arrived: the empty ones dropped, and Slate's
    /// own migration notes with them - see <see cref="OwnNotesSkip"/> for why. The notes are read
    /// before they are dropped, because the copy one of them names is the warning a second
    /// migration of the same work item is shown.
    /// </summary>
    private static RawDiscussion Settle(List<MigrationComment> comments, string? error, int notCarried)
    {
        var mine = comments
            .Where(c => c.RawHtml.Contains(MigratedMarker, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new RawDiscussion(
            [
                .. comments
                    .Where(c => c.RawHtml.Length > 0
                                && !c.RawHtml.Contains(MigratedMarker, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.CreatedDate),
            ],
            error,
            notCarried,
            mine.Count,
            LooksMigratedTo(mine));
    }

    /// <summary>
    /// The work item a previous migration said this one went to, out of the notes one leaves.
    /// The last such note wins: a work item migrated twice was migrated to wherever it went last.
    /// </summary>
    private static int? LooksMigratedTo(IReadOnlyList<MigrationComment> comments)
    {
        for (var i = comments.Count - 1; i >= 0; i--)
        {
            var at = comments[i].RawHtml.IndexOf(MigratedMarker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            var digits = comments[i].RawHtml[(at + MigratedMarker.Length)..]
                .TakeWhile(char.IsAsciiDigit)
                .ToArray();

            if (digits.Length > 0 && int.TryParse(digits, out var id)) return id;
        }

        return null;
    }

    /// <summary>True when a field value is a person rather than text or a number.</summary>
    private static bool IsIdentity(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && (value.TryGetProperty("uniqueName", out _) || value.TryGetProperty("displayName", out _));

    /// <summary>
    /// A field value in the form it can be patched back in. An identity goes back as its unique
    /// name where there is one, because a display name is ambiguous and a stale one resolves to
    /// nobody. Null for anything this app has no honest way to send, which is listed in the
    /// wizard rather than guessed at.
    /// </summary>
    private static object? PatchValueOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object when value.TryGetProperty("uniqueName", out var u)
                                  && u.GetString() is { Length: > 0 } unique => unique,
        JsonValueKind.Object when value.TryGetProperty("displayName", out var d)
                                  && d.GetString() is { Length: > 0 } name => name,
        _ => null,
    };

    /// <summary>
    /// How a field value reads in the wizard's list of what is coming over. Markup is flattened
    /// to text for the list only: what actually goes over is the markup itself.
    /// </summary>
    private static string DisplayOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Html.ToPlainText(value.GetString(), 160),
        _ => Describe(value),
    };

    // ---------------------------------------------------------------- the target

    /// <summary>
    /// The fields a work item type actually has in a given project, so the wizard can say which
    /// of the source's fields have nowhere to go before a single write is attempted. An empty
    /// set means the question could not be asked, which the caller treats as "send them all and
    /// let Azure DevOps decide" rather than as "this type has no fields".
    /// </summary>
    public async Task<HashSet<string>> GetWorkItemTypeFieldsAsync(
        string project, string workItemType, CancellationToken ct = default)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(workItemType)) return found;

        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}/{Uri.EscapeDataString(project)}/_apis/wit/workitemtypes/" +
            $"{Uri.EscapeDataString(workItemType)}/fields?api-version={ApiVersion}", null, ct);

        foreach (var field in ReadValueArray(doc.RootElement))
        {
            if (field.TryGetProperty("referenceName", out var name) && name.GetString() is { Length: > 0 } reference)
                found.Add(reference);
        }

        return found;
    }

    /// <summary>
    /// A project's iteration tree, read the same way as its areas and for the same reason: an
    /// iteration path has to exist in the project it is written to, and a migration that carried
    /// one over blindly would be refused outright. Null when it cannot be read, which leaves the
    /// new work item at the project's default iteration.
    ///
    /// Falls back to the first configured board's project when none is named, the same way its
    /// sibling <see cref="GetAreaTreeAsync"/> does - the wizard always names one, so the fallback
    /// is only there to keep the two reading alike.
    /// </summary>
    public async Task<AreaNode?> GetIterationTreeAsync(string project, CancellationToken ct = default)
    {
        var scope = string.IsNullOrWhiteSpace(project) ? settings.Current.Ado.PrimaryProject : project;
        if (string.IsNullOrWhiteSpace(scope)) return null;

        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}/{Uri.EscapeDataString(scope)}/_apis/wit/classificationnodes/iterations" +
            $"?$depth={AreaTreeDepth}&api-version={ApiVersion}", null, ct);

        return ReadAreaNode(doc.RootElement, "");
    }

    // ---------------------------------------------------------------- raising the copy

    /// <summary>
    /// Raises the copy. Every value is sent as it stands on the source; only a rejection - a 400,
    /// which means nothing was created - narrows the patch and tries again, first without the
    /// people fields and then with the plain text alone. A timeout or a dropped connection is
    /// never retried here: the item may well exist, and a second attempt would make two.
    /// </summary>
    public async Task<MigrationCreated> CreateMigratedAsync(
        string project, string workItemType,
        IReadOnlyList<MigrationFieldValue> fields, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project))
            throw new AzureDevOpsException("Choose a project to migrate the work item into.");
        if (string.IsNullOrWhiteSpace(workItemType))
            throw new AzureDevOpsException("Choose the work item type to raise in the target project.");
        if (!fields.Any(f => f.Name == "System.Title"))
            throw new AzureDevOpsException("The copy needs a title.");

        var url = $"{OrgUrl}/{Uri.EscapeDataString(project)}/_apis/wit/workitems/" +
                  $"${Uri.EscapeDataString(workItemType)}?api-version={ApiVersion}";

        var attempts = new List<IReadOnlyList<MigrationFieldValue>> { fields };

        if (fields.Any(f => f.IsIdentity))
            attempts.Add([.. fields.Where(f => !f.IsIdentity)]);

        if (attempts[^1].Any(f => !LastResortFields.Contains(f.Name)))
            attempts.Add([.. fields.Where(f => LastResortFields.Contains(f.Name))]);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var doc = await SendAsync(HttpMethod.Post, url, PatchFor(attempts[attempt]), ct,
                    "application/json-patch+json");

                // Past the send: Azure DevOps has said yes, so the copy exists. Anything that
                // goes wrong from here is Slate failing to read the answer, and saying "nothing
                // was created" about it would send somebody looking for a work item that is
                // there - or have them run the migration again and raise a second.
                try
                {
                    var dropped = fields
                        .Where(f => attempts[attempt].All(kept => kept.Name != f.Name))
                        .Select(f => f.Label)
                        .ToArray();

                    return new MigrationCreated(Map(doc.RootElement), dropped);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new AzureDevOpsException(
                        "Azure DevOps raised the copy but Slate could not read what it sent back. " + ex.Message, ex)
                    { Unanswered = true };
                }
            }
            catch (AzureDevOpsException ex)
                when (ex.Status == HttpStatusCode.BadRequest && attempt + 1 < attempts.Count)
            {
                // Turned down, so nothing was created and narrowing the patch is safe.
            }
        }
    }

    private static List<object> PatchFor(IReadOnlyList<MigrationFieldValue> fields) =>
    [
        .. fields.Select(f => (object)new
        {
            op = "add",
            path = "/fields/" + f.Name,
            value = f.Value ?? "",
        }),
    ];

    // ---------------------------------------------------------------- links

    /// <summary>
    /// Puts a Related link between two work items. Azure DevOps keeps this kind of link on both
    /// ends, so one call is what gives each of them a reference to the other - and is also why
    /// referencing a parent or a child shows up on the parent or child as well.
    /// </summary>
    public async Task AddRelatedLinkAsync(int id, int otherId, string comment, CancellationToken ct = default)
    {
        if (id == otherId) throw new AzureDevOpsException("A work item cannot be related to itself.");

        await AddRelationAsync(id, "System.LinkTypes.Related",
            $"{OrgUrl}/_apis/wit/workItems/{otherId}", comment, ct);
    }

    /// <summary>
    /// The one 400 a caller adding a link may treat as having got what it asked for: the link is
    /// already there, which is how a second run over the same pair ends. Every other 400 on a
    /// relations patch means the link was not made - a target that has been deleted or never
    /// existed, a process rule or a permission refusing it, a malformed relation - and counting
    /// those as done would report links that are not there and stop them ever being retried.
    ///
    /// Only a 400. Anything else is either a refusal that says nothing about the link or an
    /// answer that never came, and both of those are safe to try again - the link is the same
    /// link however many times it is asked for.
    ///
    /// Azure DevOps names this one in the typeKey. The wording is looked at as well, because the
    /// same refusal from an on-prem server old enough not to send a typeKey still carries
    /// TF201036, and being told a link exists twice is cheaper than reporting one that does not.
    /// </summary>
    public static bool IsLinkAlreadyThere(AzureDevOpsException ex) =>
        ex.Status == HttpStatusCode.BadRequest
        && ((ex.ErrorKey ?? "").Contains("AlreadyExists", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("TF201036", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase));

    private async Task AddRelationAsync(
        int id, string rel, string targetUrl, string comment, CancellationToken ct)
    {
        List<object> patch =
        [
            new
            {
                op = "add",
                path = "/relations/-",
                value = new { rel, url = targetUrl, attributes = new { comment } },
            },
        ];

        using var doc = await SendAsync(HttpMethod.Patch,
            $"{OrgUrl}/_apis/wit/workitems/{id}?api-version={ApiVersion}",
            patch, ct, "application/json-patch+json");
    }

    // ---------------------------------------------------------------- attachments

    /// <summary>
    /// Fetches an attachment's bytes. Its own client and its own timeout - see
    /// <see cref="Files"/> - because a large file over a slow link is a normal thing for a
    /// migration to be doing and an abnormal thing for an API call.
    /// </summary>
    public async Task<byte[]> DownloadAttachmentAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new AzureDevOpsException("That attachment has no address.");

        var canRenew = settings.Current.Ado.AuthMode == AdoAuthMode.Entra;

        for (var renewed = false; ; renewed = true)
        {
            using var request = await BuildRequestAsync(HttpMethod.Get, url, ct, freshToken: renewed);
            request.Headers.Accept.Clear();

            HttpResponseMessage response;
            try
            {
                response = await Files.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new AzureDevOpsException($"Could not reach {OrgUrl}. {ex.Message}", ex) { IsTransient = true };
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new AzureDevOpsException($"Timed out fetching that attachment from {OrgUrl}.", ex)
                { IsTransient = true };
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && canRenew && !renewed) continue;

                if (!response.IsSuccessStatusCode)
                    throw new AzureDevOpsException(
                        $"Azure DevOps returned {(int)response.StatusCode} for that attachment.")
                    {
                        Status = response.StatusCode,
                        IsTransient = BusyStatuses.Contains(response.StatusCode),
                    };

                // The credential being stale is answered with the sign-in page and a success
                // status, which every other call here is saved from by the 2xx-and-HTML test
                // inside the shared send. This path reads the body itself, so it has to make
                // the same test - otherwise that page is uploaded under the original file's
                // name, recorded as copied, and the original is then cancelled on the strength
                // of it. The content type is what is judged rather than the bytes: the
                // attachments endpoint serves every real file as octet-stream, while an
                // attachment may itself perfectly well be an HTML or SVG file.
                var served = response.Content.Headers.ContentType?.MediaType ?? "";
                if (served.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                    || served.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                    throw new AzureDevOpsException(SignInPageMessage);

                if (response.Content.Headers.ContentLength is { } declared && declared > MaxAttachmentBytes)
                    throw new AzureDevOpsException(
                        $"That attachment is {declared / (1024 * 1024)} MB, past the {MaxAttachmentBytes / (1024 * 1024)} MB Azure DevOps accepts.");

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);

                // A length the headers did not declare is only known once it is read.
                if (bytes.Length > MaxAttachmentBytes)
                    throw new AzureDevOpsException(
                        $"That attachment is past the {MaxAttachmentBytes / (1024 * 1024)} MB Azure DevOps accepts.");

                return bytes;
            }
        }
    }

    /// <summary>
    /// Uploads a file to a project's attachment store and returns the address it now lives at,
    /// for attaching to a work item there. Uploading is not attaching: an upload nobody links
    /// to is collected by Azure DevOps on its own after a while, which is what makes it safe
    /// for the caller to give up between the two.
    /// </summary>
    public async Task<string> UploadAttachmentAsync(
        string project, string fileName, byte[] content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project))
            throw new AzureDevOpsException("A project is needed to upload an attachment.");

        var name = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName.Trim();

        var payload = await SendBytesAsync(HttpMethod.Post,
            $"{OrgUrl}/{Uri.EscapeDataString(project)}/_apis/wit/attachments" +
            $"?fileName={Uri.EscapeDataString(name)}&api-version={ApiVersion}",
            content, ct);

        using var doc = ReadAnswer(payload);
        return doc.RootElement.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } address
            ? address
            : throw new AzureDevOpsException("Azure DevOps accepted the upload but did not say where it put it.")
            { Unanswered = true };
    }

    /// <summary>Attaches an already uploaded file to a work item.</summary>
    public async Task AttachFileAsync(
        int id, string attachmentUrl, string comment, CancellationToken ct = default) =>
        await AddRelationAsync(id, "AttachedFile", attachmentUrl, comment, ct);

    /// <summary>
    /// The same send as everywhere else here - one silent token renewal, the same classification
    /// of what a failure means for a write - with a body of bytes rather than JSON. Written out
    /// again rather than folded into <see cref="SendForPayloadAsync"/> because that one builds
    /// its content from an object, and a 60 MB upload has no business being serialised.
    /// </summary>
    private async Task<string> SendBytesAsync(
        HttpMethod method, string url, byte[] content, CancellationToken ct)
    {
        var canRenew = settings.Current.Ado.AuthMode == AdoAuthMode.Entra;

        for (var renewed = false; ; renewed = true)
        {
            using var request = await BuildRequestAsync(method, url, ct, freshToken: renewed);

            // Fresh each time round: content already sent cannot be sent again.
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            HttpResponseMessage response;
            try
            {
                response = await Files.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new AzureDevOpsException($"Could not reach {OrgUrl}. {ex.Message}", ex)
                { IsTransient = true, Unanswered = !NeverSent(ex) };
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new AzureDevOpsException($"Timed out uploading to {OrgUrl}.", ex)
                { IsTransient = true, Unanswered = true };
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && canRenew && !renewed)
            {
                response.Dispose();
                continue;
            }

            return await ReadPayloadAsync(response, ct);
        }
    }
}

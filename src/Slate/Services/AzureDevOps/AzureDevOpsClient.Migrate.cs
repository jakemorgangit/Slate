using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Slate.Models;

namespace Slate.Services.AzureDevOps;

/// <summary>
/// Moving a work item to another project or area: reading one in full, raising the copy, and
/// carrying the discussion, the attachments and the links across.
///
/// Everything here reads and writes raw field values rather than the display shapes the details
/// window uses. <see cref="GetWorkItemDetailAsync"/> sanitises markup and rewrites attached
/// images as data URIs so a WebView can render them, both of which are exactly wrong for a copy:
/// the new work item would carry Slate's rendering of the description instead of the description.
/// </summary>
public sealed partial class AzureDevOpsClient
{
    /// <summary>
    /// The phrase the note on a migrated work item leads with, and the one thing a later
    /// migration of the same item has to go on. Reading it back is a warning, not a record -
    /// see <see cref="MigrationSource.LooksMigratedTo"/>.
    /// </summary>
    public const string MigratedMarker = "Migrated to #";

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

        var comments = await ReadRawCommentsAsync(id, project, ct);
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
            comments,
            LooksMigratedTo(comments));
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
    /// The discussion with its markup exactly as stored - see <see cref="MigrationComment"/> for
    /// why the sanitised, image-inlined form <see cref="GetCommentsAsync"/> returns will not do.
    /// A discussion that cannot be read is not a reason to give up on the migration, so it comes
    /// back empty and the wizard says the copy will carry no comments.
    /// </summary>
    private async Task<List<MigrationComment>> ReadRawCommentsAsync(
        int id, string project, CancellationToken ct)
    {
        var scope = CommentScope(project);
        if (string.IsNullOrWhiteSpace(scope)) return [];

        JsonDocument doc;
        try
        {
            doc = await SendAsync(HttpMethod.Get,
                $"{OrgUrl}{scope}/_apis/wit/workItems/{id}/comments?$top=200&api-version={CommentsApiVersion}",
                null, ct);
        }
        catch (AzureDevOpsException)
        {
            return [];
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("comments", out var array) || array.ValueKind != JsonValueKind.Array)
                return [];

            return
            [
                .. array.EnumerateArray()
                    .Select(c => new MigrationComment(
                        c.TryGetProperty("id", out var cid) ? cid.GetInt32() : 0,
                        Identity(c, "createdBy"),
                        ReadDate(c, "createdDate"),
                        c.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""))
                    .Where(c => c.RawHtml.Length > 0)
                    .OrderBy(c => c.CreatedDate),
            ];
        }
    }

    /// <summary>
    /// The work item a previous migration said this one went to, if the discussion carries the
    /// note one leaves. The last such note wins: a work item migrated twice was migrated to
    /// wherever it went last.
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
    /// </summary>
    public async Task<AreaNode?> GetIterationTreeAsync(string project, CancellationToken ct = default)
    {
        var scope = string.IsNullOrWhiteSpace(project) ? settings.Current.Ado.Project : project;
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

                var dropped = fields
                    .Where(f => attempts[attempt].All(kept => kept.Name != f.Name))
                    .Select(f => f.Label)
                    .ToArray();

                return new MigrationCreated(Map(doc.RootElement), dropped);
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

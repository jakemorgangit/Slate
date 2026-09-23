using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Slate.Models;
using Slate.Services.Auth;
using Slate.Services.Storage;

namespace Slate.Services.AzureDevOps;

public class AzureDevOpsException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// The HTTP status behind this failure, when there was a response to read one from.
    /// Callers branch on this rather than on the wording of the message.
    /// </summary>
    public HttpStatusCode? Status { get; init; }

    /// <summary>
    /// The request never got a real answer - the network, TLS, a proxy or VPN not up yet, a
    /// timeout, or the service saying it is busy - so the same call may well work shortly.
    /// Nothing is wrong with the credential or the configuration.
    /// </summary>
    public bool IsTransient { get; init; }

    /// <summary>
    /// The request may have reached the service, and no answer came back that says whether
    /// it was carried out: the connection dropped or timed out after it was sent, the service
    /// failed while handling it, or it said yes in a form that could not be read. A write
    /// that ends like this may or may not have happened. False when it was turned down, or
    /// never left this machine - both of which mean nothing was done.
    /// </summary>
    public bool Unanswered { get; init; }

    /// <summary>Azure DevOps' own name for the failure (its typeKey), when it gave one.</summary>
    public string? ErrorKey { get; init; }
}

/// <summary>
/// A change to a work item's time that went out without an answer, where looking at the work
/// item afterwards could not settle whether it landed either. Distinct from a failure, which
/// is safe to try again: this one may already be on the work item.
/// </summary>
public sealed class TimeWriteUnconfirmedException(string message, TimeWritePlan plan, Exception? inner = null)
    : AzureDevOpsException(message, inner)
{
    /// <summary>What was sent, so a later check can still look for it.</summary>
    public TimeWritePlan Plan { get; } = plan;
}

/// <summary>
/// Thin REST client over the Azure DevOps Work Item Tracking API. Deliberately hand-rolled
/// rather than using the client SDK, which would add tens of megabytes to a single-file publish.
/// </summary>
public sealed partial class AzureDevOpsClient(SettingsStore settings, MsalAuthService auth)
{
    private const string ApiVersion = "7.1";

    /// <summary>The comments endpoint has not left preview.</summary>
    private const string CommentsApiVersion = "7.1-preview.4";
    private const int BatchSize = 200;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = PreferIPv4.ConnectAsync,
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Fields we would like. Some process templates lack some of these, hence <see cref="CoreFields"/>.</summary>
    private static readonly string[] PreferredFields =
    [
        "System.Id", "System.Title", "System.WorkItemType", "System.State", "System.AssignedTo",
        "System.AreaPath", "System.IterationPath", "System.TeamProject", "System.Tags",
        "System.ChangedDate", "System.CreatedDate", "System.Parent",
        "Microsoft.VSTS.Common.Priority",
        "Microsoft.VSTS.Scheduling.RemainingWork",
        "Microsoft.VSTS.Scheduling.CompletedWork",
        "Microsoft.VSTS.Scheduling.OriginalEstimate",
        "Microsoft.VSTS.Scheduling.StoryPoints",
    ];

    private static readonly string[] CoreFields =
    [
        "System.Id", "System.Title", "System.WorkItemType", "System.State", "System.AssignedTo",
        "System.AreaPath", "System.IterationPath", "System.TeamProject", "System.Tags", "System.ChangedDate",
        "System.CreatedDate",
    ];

    private string OrgUrl => settings.Current.Ado.OrganizationUrl.TrimEnd('/');

    // ---------------------------------------------------------------- transport

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method, string url, CancellationToken ct, bool freshToken = false)
    {
        var request = new HttpRequestMessage(method, url);
        var ado = settings.Current.Ado;

        if (ado.AuthMode == AdoAuthMode.Entra)
        {
            string token;
            try
            {
                token = await auth.GetAdoTokenAsync(ct, forceRefresh: freshToken);
            }
            catch (Exception ex) when (MsalAuthService.IsNetworkFailure(ex))
            {
                // Renewing the token needs the network too; not reaching it says nothing
                // about whether the saved sign-in is still good.
                request.Dispose();
                throw new AzureDevOpsException($"Could not reach Microsoft to renew the sign-in. {ex.Message}", ex)
                { IsTransient = true };
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(ado.PersonalAccessToken))
                throw new AzureDevOpsException("No personal access token is configured.");

            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + ado.PersonalAccessToken));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string url, object? body, CancellationToken ct, string? contentType = null) =>
        ReadAnswer(await SendForPayloadAsync(method, url, body, ct, contentType));

    /// <summary>
    /// The same send with nothing read back, for a request whose answer is the status alone -
    /// a delete, which Azure DevOps replies to with an empty body. Parsing that as JSON would
    /// report a delete that plainly worked as an answer this app could not read.
    /// </summary>
    private async Task SendNoAnswerAsync(HttpMethod method, string url, CancellationToken ct) =>
        await SendForPayloadAsync(method, url, null, ct, null);

    /// <summary>
    /// The request itself, up to and including how a failure is classified: the silent token
    /// renewal, the transport failures that leave a write in doubt, and the service's own
    /// refusals. What comes back is the body as it arrived, for the caller to make what it
    /// can of.
    /// </summary>
    private async Task<string> SendForPayloadAsync(
        HttpMethod method, string url, object? body, CancellationToken ct, string? contentType)
    {
        // A 401 on a signed-in account usually means the cached token went stale while the
        // machine slept. One silent renewal is tried before calling the credential bad.
        var canRenew = settings.Current.Ado.AuthMode == AdoAuthMode.Entra;

        for (var renewed = false; ; renewed = true)
        {
            using var request = await BuildRequestAsync(method, url, ct, freshToken: renewed);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
                if (contentType is not null)
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType) { CharSet = "utf-8" };
            }

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new AzureDevOpsException($"Could not reach {OrgUrl}. {ex.Message}", ex)
                { IsTransient = true, Unanswered = !NeverSent(ex) };
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient's own timeout, not the caller cancelling. The request may be
                // sitting at the service still, and may yet be carried out.
                throw new AzureDevOpsException($"Timed out reaching {OrgUrl}.", ex)
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

    /// <summary>
    /// Failures that happen before the request is on its way: nothing reached the service, so
    /// nothing can have been done there.
    /// </summary>
    private static bool NeverSent(HttpRequestException ex) => ex.HttpRequestError is
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
        or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;

    private static readonly HashSet<HttpStatusCode> BusyStatuses =
    [
        HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout,
    ];

    /// <summary>
    /// Statuses that leave a write in doubt: the service failed while handling the request, or
    /// a gateway gave up while it carried on, so it may yet be carried out.
    ///
    /// "Service unavailable" is not one of them, and neither is throttling: those are the front
    /// door turning the request away before it reaches anything that could act on it. Counting
    /// them as doubtful would turn an ordinary outage - where every call fails and nothing at
    /// all happens - into hours that may already be booked and a block nobody can record until
    /// the work item can be read again.
    /// </summary>
    private static bool LeavesWritesInDoubt(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
        || ((int)status >= 500 && status != HttpStatusCode.ServiceUnavailable);

    private static async Task<string> ReadPayloadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new AzureDevOpsException(DescribeFailure(response, payload))
                {
                    Status = response.StatusCode,
                    IsTransient = BusyStatuses.Contains(response.StatusCode),
                    Unanswered = LeavesWritesInDoubt(response.StatusCode),
                    ErrorKey = ReadErrorKey(payload),
                };

            return payload;
        }
    }

    /// <summary>Makes a document of an answer that was asked for as data.</summary>
    private static JsonDocument ReadAnswer(string payload)
    {
        // A sign-in redirect comes back as 200 plus HTML, which means the credential was rejected.
        if (payload.StartsWith('<'))
            throw new AzureDevOpsException(
                "Azure DevOps returned a sign-in page instead of data. The token is likely expired or lacks the Work Items (Read) scope.");

        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            // The service said yes, so a write did happen - it is only the account of it
            // that is missing.
            throw new AzureDevOpsException(
                "Azure DevOps returned a response this app could not read. " + ex.Message, ex)
            { Unanswered = true };
        }
    }

    private static string? ReadErrorKey(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("typeKey", out var key) && key.ValueKind == JsonValueKind.String
                ? key.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeFailure(HttpResponseMessage response, string payload)
    {
        var detail = payload;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("message", out var message))
                detail = message.GetString() ?? payload;
        }
        catch (JsonException)
        {
            // Keep the raw body.
        }

        if (detail.Length > 400) detail = detail[..400] + "...";

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Azure DevOps rejected the credential (401). Check the PAT, or sign in again.",
            HttpStatusCode.Forbidden => $"Access denied (403). The credential needs the Work Items (Read) scope. {detail}",
            HttpStatusCode.NotFound => $"Not found (404). Check the organization URL and project name. {detail}",
            HttpStatusCode.TooManyRequests => "Azure DevOps is rate-limiting this account. Try again shortly.",
            _ => $"Azure DevOps returned {(int)response.StatusCode}. {detail}",
        };
    }

    // ---------------------------------------------------------------- public API

    /// <summary>Verifies credentials and returns the signed-in identity's display name.</summary>
    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}/_apis/connectionData?api-version={ApiVersion}-preview", null, ct);

        return doc.RootElement.TryGetProperty("authenticatedUser", out var user) &&
               user.TryGetProperty("providerDisplayName", out var name)
            ? name.GetString() ?? "unknown"
            : "unknown";
    }

    public async Task<List<AdoProject>> GetProjectsAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}/_apis/projects?$top=500&api-version={ApiVersion}", null, ct);

        return [.. ReadValueArray(doc.RootElement)
            .Select(p => new AdoProject(p.GetProperty("id").GetString()!, p.GetProperty("name").GetString()!))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Flattened list of the shared and personal saved queries in the selected project.</summary>
    public async Task<List<AdoQuery>> GetSavedQueriesAsync(CancellationToken ct = default)
    {
        var project = settings.Current.Ado.Project;
        if (string.IsNullOrWhiteSpace(project))
            throw new AzureDevOpsException("Pick a project before loading saved queries.");

        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}/{Uri.EscapeDataString(project)}/_apis/wit/queries?$depth=2&api-version={ApiVersion}", null, ct);

        var results = new List<AdoQuery>();
        foreach (var root in ReadValueArray(doc.RootElement))
            Walk(root, results);

        return [.. results.Where(q => !q.IsFolder).OrderBy(q => q.Path, StringComparer.OrdinalIgnoreCase)];

        static void Walk(JsonElement node, List<AdoQuery> into)
        {
            var isFolder = node.TryGetProperty("isFolder", out var f) && f.GetBoolean();
            into.Add(new AdoQuery(
                node.GetProperty("id").GetString()!,
                node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                node.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                isFolder));

            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray()) Walk(child, into);
        }
    }

    /// <summary>Runs whatever query the settings currently describe and hydrates the results.</summary>
    public async Task<List<WorkItem>> GetWorkItemsAsync(CancellationToken ct = default)
    {
        var ado = settings.Current.Ado;
        var ids = ado.Scope switch
        {
            WorkItemScope.SavedQuery => await RunSavedQueryAsync(ado.SavedQueryId, ct),
            WorkItemScope.CustomWiql => await RunWiqlAsync(ado.CustomWiql, ct),
            _ => await RunWiqlAsync(BuildScopeWiql(ado), ct),
        };

        return ids.Count == 0 ? [] : await GetWorkItemsByIdAsync(ids, ct);
    }

    /// <summary>
    /// The query Slate builds for itself, from two independent choices: which area the work
    /// lives in, and whether to narrow it to your own. An area takes everything beneath it,
    /// so a top-level pick includes its sub-areas; no area at all means the whole project.
    ///
    /// The saved query and custom WIQL scopes never come through here - those belong to
    /// whoever wrote them, and Slate does not rewrite them.
    /// </summary>
    public static string BuildScopeWiql(AdoSettings ado)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(ado.AreaPath))
            clauses.Add($"[System.AreaPath] UNDER {Quote(ado.AreaPath)}");

        if (ado.OnlyMine)
            clauses.Add("[System.AssignedTo] = @Me");

        if (ado.ExcludedStates.Count > 0)
            clauses.Add($"[System.State] NOT IN ({string.Join(", ", ado.ExcludedStates.Select(Quote))})");

        // An area with nothing else set would otherwise produce a bare WHERE.
        var where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);

        return "SELECT [System.Id] FROM WorkItems" + where + " ORDER BY [System.ChangedDate] DESC";
    }

    /// <summary>A WIQL string literal, with embedded quotes doubled as that dialect expects.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";


    private async Task<List<int>> RunWiqlAsync(string wiql, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(wiql))
            throw new AzureDevOpsException("The WIQL query is empty.");

        var url = $"{OrgUrl}{ProjectSegment()}/_apis/wit/wiql?$top=500&api-version={ApiVersion}";
        using var doc = await SendAsync(HttpMethod.Post, url, new { query = wiql }, ct);
        return ExtractIds(doc.RootElement);
    }

    private async Task<List<int>> RunSavedQueryAsync(string queryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queryId))
            throw new AzureDevOpsException("No saved query is selected.");

        var url = $"{OrgUrl}{ProjectSegment()}/_apis/wit/wiql/{Uri.EscapeDataString(queryId)}?$top=500&api-version={ApiVersion}";
        using var doc = await SendAsync(HttpMethod.Get, url, null, ct);
        return ExtractIds(doc.RootElement);
    }

    private string ProjectSegment()
    {
        var project = settings.Current.Ado.Project;
        return string.IsNullOrWhiteSpace(project) ? "" : "/" + Uri.EscapeDataString(project);
    }

    /// <summary>Handles both flat queries (workItems) and tree/one-hop queries (workItemRelations).</summary>
    private static List<int> ExtractIds(JsonElement root)
    {
        var ids = new List<int>();

        if (root.TryGetProperty("workItems", out var flat) && flat.ValueKind == JsonValueKind.Array)
        {
            ids.AddRange(flat.EnumerateArray().Select(w => w.GetProperty("id").GetInt32()));
        }
        else if (root.TryGetProperty("workItemRelations", out var tree) && tree.ValueKind == JsonValueKind.Array)
        {
            foreach (var relation in tree.EnumerateArray())
                if (relation.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object)
                    ids.Add(target.GetProperty("id").GetInt32());
        }

        return [.. ids.Distinct()];
    }

    public async Task<List<WorkItem>> GetWorkItemsByIdAsync(IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        var items = new List<WorkItem>(ids.Count);

        for (var offset = 0; offset < ids.Count; offset += BatchSize)
        {
            var chunk = ids.Skip(offset).Take(BatchSize).ToArray();
            items.AddRange(await GetBatchAsync(chunk, PreferredFields, ct));
        }

        await AttachParentTitlesAsync(items, ct);
        await AttachLinksAsync(items, ct);
        return items;
    }

    /// <summary>
    /// Adds the links between work items. Relations need their own batch call because the
    /// API will not return $expand and an explicit field list together. Links are a
    /// convenience, so a failure here leaves the items as they are.
    /// </summary>
    private async Task AttachLinksAsync(List<WorkItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return;

        Dictionary<int, List<(string Kind, int Id)>> relations;
        try
        {
            relations = new Dictionary<int, List<(string Kind, int Id)>>();

            // Batched like the field reads above. A single Take(BatchSize) here left every
            // item past the first page with no links at all and nothing said so.
            for (var offset = 0; offset < items.Count; offset += BatchSize)
            {
                var chunk = items.Skip(offset).Take(BatchSize).Select(i => i.Id).ToArray();
                foreach (var (id, links) in await FetchRelationsAsync(chunk, ct))
                    relations[id] = links;
            }
        }
        catch (AzureDevOpsException)
        {
            return;
        }

        if (relations.Count == 0) return;

        // Resolve titles: reuse what is already loaded, then fetch whatever is left over.
        var known = items.ToDictionary(i => i.Id, i => (i.Title, i.WorkItemType));

        var missing = relations.Values.SelectMany(v => v.Select(r => r.Id))
            .Distinct()
            .Where(id => !known.ContainsKey(id))
            .Take(BatchSize)
            .ToArray();

        if (missing.Length > 0)
        {
            try
            {
                foreach (var linked in await GetBatchAsync(missing, CoreFields, ct))
                    known[linked.Id] = (linked.Title, linked.WorkItemType);
            }
            catch (AzureDevOpsException)
            {
                // Fall through: links still render, just with the id as the label.
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (!relations.TryGetValue(items[i].Id, out var links) || links.Count == 0) continue;

            items[i] = items[i] with
            {
                Links = [.. links
                    .Select(l => new WorkItemLink(
                        l.Kind,
                        l.Id,
                        known.TryGetValue(l.Id, out var info) ? info.Title : $"#{l.Id}",
                        known.TryGetValue(l.Id, out var t) ? t.WorkItemType : ""))
                    .OrderBy(l => l.SortOrder)
                    .ThenBy(l => l.WorkItemId)],
            };
        }
    }

    private async Task<Dictionary<int, List<(string Kind, int Id)>>> FetchRelationsAsync(
        int[] ids, CancellationToken ct)
    {
        var url = $"{OrgUrl}/_apis/wit/workitemsbatch?api-version={ApiVersion}";
        var body = new Dictionary<string, object> { ["ids"] = ids, ["$expand"] = "Relations" };

        using var doc = await SendAsync(HttpMethod.Post, url, body, ct);
        var map = new Dictionary<int, List<(string, int)>>();

        foreach (var element in ReadValueArray(doc.RootElement))
        {
            if (!element.TryGetProperty("id", out var idElement)) continue;
            if (!element.TryGetProperty("relations", out var array) || array.ValueKind != JsonValueKind.Array) continue;

            var links = new List<(string, int)>();
            foreach (var relation in array.EnumerateArray())
            {
                var rel = relation.TryGetProperty("rel", out var r) ? r.GetString() ?? "" : "";
                var target = relation.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

                if (TryReadWorkItemId(target, out var linkedId))
                    links.Add((DescribeRelation(rel), linkedId));
            }

            if (links.Count > 0) map[idElement.GetInt32()] = links;
        }

        return map;
    }

    /// <summary>True when the url points at another work item rather than a file or hyperlink.</summary>
    private static bool TryReadWorkItemId(string url, out int id)
    {
        id = 0;
        if (!url.Contains("/workItems/", StringComparison.OrdinalIgnoreCase)) return false;

        var slash = url.LastIndexOf('/');
        return slash >= 0 && int.TryParse(url[(slash + 1)..], out id);
    }

    /// <summary>Turns an Azure DevOps link type into something worth showing a person.</summary>
    public static string DescribeRelation(string rel) => rel switch
    {
        "System.LinkTypes.Hierarchy-Reverse" => "Parent",
        "System.LinkTypes.Hierarchy-Forward" => "Child",
        "System.LinkTypes.Related" => "Related",
        "System.LinkTypes.Dependency-Forward" => "Successor",
        "System.LinkTypes.Dependency-Reverse" => "Predecessor",
        "System.LinkTypes.Duplicate-Forward" => "Duplicate",
        "System.LinkTypes.Duplicate-Reverse" => "Duplicate of",
        "Microsoft.VSTS.Common.TestedBy-Forward" => "Tested by",
        "Microsoft.VSTS.Common.TestedBy-Reverse" => "Tests",
        "Microsoft.VSTS.Common.Affects-Forward" => "Affects",
        "Microsoft.VSTS.Common.Affects-Reverse" => "Affected by",
        "ArtifactLink" => "Artifact",
        "AttachedFile" => "Attachment",
        "Hyperlink" => "Hyperlink",
        _ => Prettify(rel),
    };

    private async Task<List<WorkItem>> GetBatchAsync(int[] ids, string[] fields, CancellationToken ct)
    {
        var url = $"{OrgUrl}/_apis/wit/workitemsbatch?api-version={ApiVersion}";
        try
        {
            using var doc = await SendAsync(HttpMethod.Post, url, new { ids, fields }, ct);
            return [.. ReadValueArray(doc.RootElement).Select(Map)];
        }
        catch (AzureDevOpsException) when (!ReferenceEquals(fields, CoreFields))
        {
            // Not every process template defines every scheduling field; retry with the
            // fields that exist everywhere rather than failing the whole load.
            return await GetBatchAsync(ids, CoreFields, ct);
        }
    }

    /// <summary>Resolves parent titles so the board can group items under their parent.</summary>
    private async Task AttachParentTitlesAsync(List<WorkItem> items, CancellationToken ct)
    {
        var titles = items.ToDictionary(i => i.Id, i => i.Title);

        var missing = items
            .Where(i => i.ParentId is > 0)
            .Select(i => i.ParentId!.Value)
            .Distinct()
            .Where(id => !titles.ContainsKey(id))
            .Take(BatchSize)
            .ToArray();

        if (missing.Length > 0)
        {
            try
            {
                foreach (var parent in await GetBatchAsync(missing, CoreFields, ct))
                    titles[parent.Id] = parent.Title;
            }
            catch (AzureDevOpsException)
            {
                // Parent titles are cosmetic - never fail the whole load over them.
                return;
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].ParentId is int pid && titles.TryGetValue(pid, out var title))
                items[i] = items[i] with { ParentTitle = title };
        }
    }

    // ---------------------------------------------------------------- detail + writes

    /// <summary>Everything about one work item, for the details modal.</summary>
    public async Task<WorkItemDetail> GetWorkItemDetailAsync(int id, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}/_apis/wit/workitems/{id}?$expand=all&api-version={ApiVersion}", null, ct);

        var root = doc.RootElement;
        var fields = root.GetProperty("fields");
        var project = Str(fields, "System.TeamProject");

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Id", "System.Title", "System.WorkItemType", "System.State", "System.Reason",
            "System.AssignedTo", "System.CreatedBy", "System.TeamProject", "System.AreaPath",
            "System.IterationPath", "System.Tags", "System.Description", "System.CreatedDate",
            "System.ChangedDate",
        };

        var extra = new List<WorkItemFieldValue>();
        var rich = new List<WorkItemRichField>();

        foreach (var field in fields.EnumerateObject())
        {
            if (known.Contains(field.Name)) continue;

            var value = Describe(field.Value);
            if (string.IsNullOrWhiteSpace(value)) continue;

            // Anything that carries markup gets its own section rather than a metadata cell,
            // which picks up acceptance criteria, repro steps, system info and custom fields alike.
            if (LooksLikeHtml(value))
                rich.Add(new WorkItemRichField(
                    field.Name, Prettify(field.Name), await InlineImagesAsync(Html.Sanitize(value), ct)));
            else
                extra.Add(new WorkItemFieldValue(field.Name, Prettify(field.Name), value));
        }

        // Repro steps and acceptance criteria first; they are what people actually read.
        rich = [.. rich.OrderBy(f => RichFieldRank(f.Name)).ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)];

        return new WorkItemDetail(
            id,
            root.TryGetProperty("rev", out var rev) ? rev.GetInt32() : 0,
            Str(fields, "System.Title"),
            Str(fields, "System.WorkItemType"),
            Str(fields, "System.State"),
            Str(fields, "System.Reason"),
            Identity(fields, "System.AssignedTo"),
            Identity(fields, "System.CreatedBy"),
            project,
            Str(fields, "System.AreaPath"),
            Str(fields, "System.IterationPath"),
            SplitTags(Str(fields, "System.Tags")),
            BuildWebUrl(project, id),
            await InlineImagesAsync(Html.Sanitize(Str(fields, "System.Description")), ct),
            rich,
            Date(fields, "System.CreatedDate"),
            Date(fields, "System.ChangedDate"),
            [.. extra.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)],
            await NameRelationsAsync(ReadRelations(root), ct));
    }

    /// <summary>Puts real titles on linked work items so the modal is readable.</summary>
    private async Task<List<WorkItemRelation>> NameRelationsAsync(
        List<WorkItemRelation> relations, CancellationToken ct)
    {
        var ids = relations.Where(r => r.WorkItemId is int)
            .Select(r => r.WorkItemId!.Value)
            .Distinct()
            .Take(BatchSize)
            .ToArray();

        if (ids.Length == 0) return relations;

        Dictionary<int, string> titles;
        try
        {
            titles = (await GetBatchAsync(ids, CoreFields, ct))
                .ToDictionary(i => i.Id, i => $"#{i.Id} {i.Title}");
        }
        catch (AzureDevOpsException)
        {
            return relations;
        }

        return [.. relations.Select(r =>
            r.WorkItemId is int id && titles.TryGetValue(id, out var title)
                ? r with { Title = title }
                : r)];
    }

    private static List<WorkItemRelation> ReadRelations(JsonElement root)
    {
        var relations = new List<WorkItemRelation>();
        if (!root.TryGetProperty("relations", out var array) || array.ValueKind != JsonValueKind.Array)
            return relations;

        foreach (var relation in array.EnumerateArray())
        {
            var url = relation.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var rel = relation.TryGetProperty("rel", out var r) ? r.GetString() ?? "" : "";

            var name = DescribeRelation(rel);
            if (relation.TryGetProperty("attributes", out var attributes) &&
                attributes.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                name = n.GetString()!;

            // Work item links end in /workItems/{id}; anything else is an attachment or hyperlink.
            int? linkedId = null;
            var slash = url.LastIndexOf('/');
            if (slash >= 0 && int.TryParse(url[(slash + 1)..], out var parsed) &&
                url.Contains("/workItems/", StringComparison.OrdinalIgnoreCase))
                linkedId = parsed;

            var title = relation.TryGetProperty("attributes", out var a2) &&
                        a2.TryGetProperty("comment", out var c) && !string.IsNullOrWhiteSpace(c.GetString())
                ? c.GetString()!
                : linkedId is int wid ? $"#{wid}" : url;

            relations.Add(new WorkItemRelation(name, title, url, linkedId));
        }

        return relations;
    }

    // ---------------------------------------------------------------- creating work

    /// <summary>
    /// Creates a work item underneath another one. Used to spawn a Task from a type that
    /// cannot carry time, so there is somewhere to book hours against.
    /// Area, iteration and assignee are inherited from the parent.
    /// </summary>
    public async Task<WorkItem> CreateChildAsync(
        WorkItem parent,
        string title,
        string description,
        string workItemType,
        double? remainingHours,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new AzureDevOpsException("Give the new work item a title.");

        var project = string.IsNullOrWhiteSpace(parent.Project) ? settings.Current.Ado.Project : parent.Project;
        if (string.IsNullOrWhiteSpace(project))
            throw new AzureDevOpsException("A project is needed to create a work item.");

        var type = string.IsNullOrWhiteSpace(workItemType) ? "Task" : workItemType.Trim();
        var url = $"{OrgUrl}/{Uri.EscapeDataString(project)}/_apis/wit/workitems/${Uri.EscapeDataString(type)}?api-version={ApiVersion}";

        // Try with the parent's assignee; an identity that will not resolve is a common
        // failure, so fall back to leaving it unassigned rather than losing the whole create.
        try
        {
            return await PostCreateAsync(url, parent, title, description, remainingHours, parent.AssignedTo, ct);
        }
        catch (AzureDevOpsException ex) when (ex.Status == HttpStatusCode.BadRequest
                                              && !string.IsNullOrWhiteSpace(parent.AssignedTo))
        {
            // Only on a rejection. A timeout or a dropped connection may well have created
            // the item already, and retrying that would raise a second one.
            return await PostCreateAsync(url, parent, title, description, remainingHours, null, ct);
        }
    }

    private async Task<WorkItem> PostCreateAsync(
        string url, WorkItem parent, string title, string description,
        double? remainingHours, string? assignedTo, CancellationToken ct)
    {
        var patch = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = title.Trim() },
        };

        if (!string.IsNullOrWhiteSpace(description))
            patch.Add(new { op = "add", path = "/fields/System.Description", value = Html.ToBasicHtml(description) });

        if (!string.IsNullOrWhiteSpace(parent.AreaPath))
            patch.Add(new { op = "add", path = "/fields/System.AreaPath", value = parent.AreaPath });

        if (!string.IsNullOrWhiteSpace(parent.IterationPath))
            patch.Add(new { op = "add", path = "/fields/System.IterationPath", value = parent.IterationPath });

        if (!string.IsNullOrWhiteSpace(assignedTo))
            patch.Add(new { op = "add", path = "/fields/System.AssignedTo", value = assignedTo });

        if (remainingHours is > 0)
        {
            var hours = Math.Round(remainingHours.Value, 2);
            patch.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.RemainingWork", value = hours });
            patch.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Scheduling.OriginalEstimate", value = hours });
        }

        patch.Add(new
        {
            op = "add",
            path = "/relations/-",
            value = new
            {
                rel = "System.LinkTypes.Hierarchy-Reverse",
                url = $"{OrgUrl}/_apis/wit/workItems/{parent.Id}",
            },
        });

        using var doc = await SendAsync(HttpMethod.Post, url, patch, ct, "application/json-patch+json");
        return Map(doc.RootElement);
    }

    /// <summary>Rough but reliable: a value carrying tags is rich text, not a metadata value.</summary>
    private static bool LooksLikeHtml(string value) =>
        value.Contains('<') && value.Contains('>') && value.Length > 12;

    private static int RichFieldRank(string name) => name switch
    {
        "Microsoft.VSTS.TCM.ReproSteps" => 0,
        "Microsoft.VSTS.Common.AcceptanceCriteria" => 1,
        "Microsoft.VSTS.TCM.SystemInfo" => 2,
        _ => 3,
    };

    /// <summary>
    /// Replaces a work item's description. The revision test means a change made elsewhere
    /// since the modal opened fails loudly instead of being overwritten.
    /// </summary>
    public async Task<WorkItemDetail> UpdateDescriptionAsync(
        int id, int rev, string html, CancellationToken ct = default)
    {
        return await UpdateFieldsAsync(
            id, rev, new Dictionary<string, object?> { ["System.Description"] = html ?? "" }, ct);
    }

    /// <summary>
    /// Who the current credential belongs to, used to decide what is editable here. Read
    /// through the same connectionData call the time writes use, so the one thing that says
    /// which organization this is and whose hand a revision was is read once and agreed on.
    /// Empty when it could not be read, which is how it has always been treated here.
    /// </summary>
    public async Task<string> GetAuthenticatedUserAsync(CancellationToken ct = default) =>
        (await ReadConnectionAsync(refresh: false, ct))?.DisplayName ?? "";

    // ---------------------------------------------------------------- discussion

    /// <summary>
    /// The work item's discussion. The comments API is project-scoped and still preview-only,
    /// hence the separate api-version.
    /// </summary>
    public async Task<List<WorkItemComment>> GetCommentsAsync(
        int id, string project, CancellationToken ct = default)
    {
        var scope = CommentScope(project);
        if (string.IsNullOrWhiteSpace(scope))
            throw new AzureDevOpsException("A project is needed to read the discussion.");

        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}{scope}/_apis/wit/workItems/{id}/comments?$top=200&api-version={CommentsApiVersion}",
            null, ct);

        if (!doc.RootElement.TryGetProperty("comments", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var comments = array.EnumerateArray().Select(ReadComment).OrderBy(c => c.CreatedDate).ToList();

        // Pictures pasted into a discussion are attachments too, so they need the same
        // treatment as the ones in the description.
        for (var i = 0; i < comments.Count; i++)
            comments[i] = comments[i] with { Html = await InlineImagesAsync(comments[i].Html, ct) };

        return comments;
    }

    /// <summary>Adds a comment. Needs the Work Items (Read &amp; Write) scope.</summary>
    public async Task<WorkItemComment> AddCommentAsync(
        int id, string project, string html, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(html))
            throw new AzureDevOpsException("Write something first.");

        var scope = CommentScope(project);
        if (string.IsNullOrWhiteSpace(scope))
            throw new AzureDevOpsException("A project is needed to add to the discussion.");

        using var doc = await SendAsync(HttpMethod.Post,
            $"{OrgUrl}{scope}/_apis/wit/workItems/{id}/comments?api-version={CommentsApiVersion}",
            new { text = html }, ct);

        return ReadComment(doc.RootElement);
    }

    /// <summary>
    /// Removes one comment from a work item's discussion, for taking back the note a booking
    /// left when those hours are undone. Needs the Work Items (Read &amp; Write) scope, like
    /// adding one.
    ///
    /// A comment that is not there is what this asks for, so a 404 is left alone rather than
    /// reported: it is how Azure DevOps answers for one already deleted - in the browser, by a
    /// colleague, or by an earlier undo whose answer never came back. The same status also
    /// covers a work item or a project the address cannot find, which is why the project the
    /// comment was posted under is kept beside its id rather than worked out again here.
    /// </summary>
    public async Task DeleteCommentAsync(
        int id, string project, int commentId, CancellationToken ct = default)
    {
        if (commentId <= 0) throw new AzureDevOpsException("That note has no comment to remove.");

        var scope = CommentScope(project);
        if (string.IsNullOrWhiteSpace(scope))
            throw new AzureDevOpsException("A project is needed to remove a comment from the discussion.");

        try
        {
            await SendNoAnswerAsync(HttpMethod.Delete,
                $"{OrgUrl}{scope}/_apis/wit/workItems/{id}/comments/{commentId}?api-version={CommentsApiVersion}",
                ct);
        }
        catch (AzureDevOpsException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // Already gone, which is the state this was asked to reach.
        }
    }

    /// <summary>
    /// The project segment the comments endpoints are reached through: the one asked for, or
    /// the selected project when nothing was. Empty when neither says anything, which every
    /// caller refuses rather than sending an address with a hole in it.
    /// </summary>
    private string CommentScope(string project) =>
        string.IsNullOrWhiteSpace(project) ? ProjectSegment() : "/" + Uri.EscapeDataString(project);

    private static WorkItemComment ReadComment(JsonElement element) => new(
        element.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
        Identity(element, "createdBy"),
        Html.Sanitize(element.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""),
        ReadDate(element, "createdDate"),
        ReadDate(element, "modifiedDate"));

    private static DateTimeOffset? ReadDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), out var parsed) ? parsed : null;

    /// <summary>Renders any field value as display text.</summary>
    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetDouble().ToString("0.##"),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Object => value.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
        _ => "",
    };

    /// <summary>Turns "Microsoft.VSTS.Scheduling.RemainingWork" into "Remaining Work".</summary>
    private static string Prettify(string referenceName)
    {
        var leaf = referenceName[(referenceName.LastIndexOf('.') + 1)..];
        var text = new System.Text.StringBuilder(leaf.Length + 8);

        for (var i = 0; i < leaf.Length; i++)
        {
            if (i > 0 && char.IsUpper(leaf[i]) && !char.IsUpper(leaf[i - 1])) text.Append(' ');
            text.Append(leaf[i]);
        }

        return text.ToString();
    }

    private WorkItem Map(JsonElement element)
    {
        var fields = element.GetProperty("fields");
        var id = element.GetProperty("id").GetInt32();
        var project = Str(fields, "System.TeamProject");

        return new WorkItem
        {
            Id = id,
            Title = Str(fields, "System.Title"),
            WorkItemType = Str(fields, "System.WorkItemType"),
            State = Str(fields, "System.State"),
            AssignedTo = Identity(fields, "System.AssignedTo"),
            AreaPath = Str(fields, "System.AreaPath"),
            IterationPath = Str(fields, "System.IterationPath"),
            Project = project,
            // Zero rather than a made-up default: a work item with no priority set
            // should not be shown wearing a P4 pill it never had.
            Priority = (int)(Num(fields, PriorityField) ?? 0),
            Tags = SplitTags(Str(fields, "System.Tags")),
            ChangedDate = Date(fields, "System.ChangedDate"),
            CreatedDate = Date(fields, "System.CreatedDate"),
            RemainingWork = Num(fields, "Microsoft.VSTS.Scheduling.RemainingWork"),
            CompletedWork = Num(fields, "Microsoft.VSTS.Scheduling.CompletedWork"),
            OriginalEstimate = Num(fields, "Microsoft.VSTS.Scheduling.OriginalEstimate"),
            StoryPoints = Num(fields, "Microsoft.VSTS.Scheduling.StoryPoints"),
            ParentId = Num(fields, "System.Parent") is double p ? (int)p : null,
            TracksTime = TracksTime(fields, Str(fields, "System.WorkItemType")),
            Url = BuildWebUrl(project, id),
        };
    }

    /// <summary>Types that carry Remaining/Completed Work in every stock process template.</summary>
    private static readonly HashSet<string> TimeTrackingTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Task", "Bug" };

    private static bool TracksTime(JsonElement fields, string workItemType) =>
        TimeTrackingTypes.Contains(workItemType)
        || fields.TryGetProperty("Microsoft.VSTS.Scheduling.RemainingWork", out _)
        || fields.TryGetProperty("Microsoft.VSTS.Scheduling.CompletedWork", out _)
        || fields.TryGetProperty("Microsoft.VSTS.Scheduling.OriginalEstimate", out _);

    public string BuildWebUrl(string project, int id) =>
        string.IsNullOrWhiteSpace(project)
            ? $"{OrgUrl}/_workitems/edit/{id}"
            : $"{OrgUrl}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}";

    /// <summary>
    /// Reads the "value" collection from a response. A malformed or single-item payload that
    /// is not an array yields nothing rather than an exception, so one odd response cannot
    /// fail an operation that has already succeeded.
    /// </summary>
    private static IEnumerable<JsonElement> ReadValueArray(JsonElement root)
    {
        if (!root.TryGetProperty("value", out var value)) return [];

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray(),
            JsonValueKind.Object => [value],
            _ => [],
        };
    }

    private static string[] SplitTags(string tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Str(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTimeOffset? Date(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), out var parsed) ? parsed : null;

    private static string Identity(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Object => v.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
            _ => "",
        };
    }
}

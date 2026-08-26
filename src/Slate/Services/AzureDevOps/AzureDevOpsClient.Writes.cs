using System.Net;
using System.Net.Http;
using System.Text.Json;
using Slate.Models;

namespace Slate.Services.AzureDevOps;

/// <summary>Writing work items back: editing fields, setting priority, and raising new ones.</summary>
public sealed partial class AzureDevOpsClient
{
    /// <summary>
    /// Patches a set of fields on a work item. A revision above zero adds a concurrency test,
    /// so an edit made elsewhere since the modal opened fails loudly instead of being
    /// overwritten; pass zero when there is no revision to hand, as the priority pills do.
    /// </summary>
    public async Task<WorkItemDetail> UpdateFieldsAsync(
        int id, int rev, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default)
    {
        if (fields.Count == 0) throw new AzureDevOpsException("Nothing to change.");

        var patch = new List<object>();
        if (rev > 0) patch.Add(new { op = "test", path = "/rev", value = (object)rev });

        foreach (var (name, value) in fields)
            patch.Add(new { op = "add", path = "/fields/" + name, value = value ?? "" });

        using var doc = await SendAsync(HttpMethod.Patch,
            $"{OrgUrl}/_apis/wit/workitems/{id}?api-version={ApiVersion}",
            patch, ct, "application/json-patch+json");

        // Re-read so the caller gets everything, including the bumped revision.
        return await GetWorkItemDetailAsync(id, ct);
    }

    /// <summary>
    /// Sets Azure DevOps' own priority field, or removes it when the level is zero. Returns
    /// the item as it now stands so the list can be updated without a second round trip.
    /// </summary>
    public async Task<WorkItem?> SetAdoPriorityAsync(int id, int priority, CancellationToken ct = default)
    {
        if (priority is < 0 or > 4)
            throw new AzureDevOpsException("Priority runs from 1 to 4, or zero to clear it.");

        List<object> patch = priority == 0
            ? [new { op = "remove", path = "/fields/" + PriorityField }]
            : [new { op = "add", path = "/fields/" + PriorityField, value = (object)priority }];

        using var doc = await SendAsync(HttpMethod.Patch,
            $"{OrgUrl}/_apis/wit/workitems/{id}?api-version={ApiVersion}",
            patch, ct, "application/json-patch+json");

        return Map(doc.RootElement);
    }

    public const string PriorityField = "Microsoft.VSTS.Common.Priority";

    // ---------------------------------------------------------------- raising new work

    /// <summary>The work item types a project offers, so the new-item form is not guesswork.</summary>
    public async Task<List<string>> GetWorkItemTypesAsync(string project, CancellationToken ct = default)
    {
        var scope = string.IsNullOrWhiteSpace(project) ? settings.Current.Ado.Project : project;
        if (string.IsNullOrWhiteSpace(scope)) return [];

        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}/{Uri.EscapeDataString(scope)}/_apis/wit/workitemtypes?api-version={ApiVersion}",
            null, ct);

        return
        [
            .. ReadValueArray(doc.RootElement)
                .Select(type => type.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// A project's area tree, deep enough to cover any area path a real process is likely to
    /// carry - the picker offers a level for each one it finds. Returns null when the tree
    /// cannot be read, so the caller can fall back to a plain text box rather than leaving
    /// the user with nothing.
    /// </summary>
    public async Task<AreaNode?> GetAreaTreeAsync(string project, CancellationToken ct = default)
    {
        var scope = string.IsNullOrWhiteSpace(project) ? settings.Current.Ado.Project : project;
        if (string.IsNullOrWhiteSpace(scope)) return null;

        using var doc = await SendAsync(HttpMethod.Get,
            $"{OrgUrl}/{Uri.EscapeDataString(scope)}/_apis/wit/classificationnodes/areas" +
            $"?$depth={AreaTreeDepth}&api-version={ApiVersion}", null, ct);

        return ReadAreaNode(doc.RootElement, "");
    }

    private const int AreaTreeDepth = 8;

    /// <summary>
    /// Builds the node and its subtree. The path is accumulated from names on the way down:
    /// the API's own "path" property includes an "\Area" segment that System.AreaPath does
    /// not want, so using it directly would produce a value Azure DevOps rejects.
    /// </summary>
    private static AreaNode? ReadAreaNode(JsonElement element, string parentPath)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("name", out var nameProperty)) return null;

        var name = nameProperty.GetString() ?? "";
        if (name.Length == 0) return null;

        var path = parentPath.Length == 0 ? name : parentPath + "\\" + name;

        var children = new List<AreaNode>();
        if (element.TryGetProperty("children", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in array.EnumerateArray())
            {
                if (ReadAreaNode(child, path) is { } node) children.Add(node);
            }
        }

        return new AreaNode(name, path,
            [.. children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// Raises a brand new work item. Everything except the project, type and title is
    /// optional, and an assignee that will not resolve is dropped rather than being allowed
    /// to fail the whole thing - losing a filled-in form to a stale display name is a poor
    /// trade for an item that would have been created unassigned anyway.
    /// </summary>
    public async Task<WorkItem> CreateWorkItemAsync(NewWorkItem request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new AzureDevOpsException("Give the new work item a title.");

        var project = string.IsNullOrWhiteSpace(request.Project) ? settings.Current.Ado.Project : request.Project;
        if (string.IsNullOrWhiteSpace(project))
            throw new AzureDevOpsException("Choose a project to create the work item in.");

        var type = string.IsNullOrWhiteSpace(request.WorkItemType) ? "Task" : request.WorkItemType.Trim();
        var url = $"{OrgUrl}/{Uri.EscapeDataString(project)}/_apis/wit/workitems/" +
                  $"${Uri.EscapeDataString(type)}?api-version={ApiVersion}";

        try
        {
            return await PostNewAsync(url, request, request.AssignedTo, ct);
        }
        catch (AzureDevOpsException ex) when (ex.Status == HttpStatusCode.BadRequest
                                              && !string.IsNullOrWhiteSpace(request.AssignedTo))
        {
            // Only on a rejection. A timeout or a dropped connection may well have created
            // the item already, and retrying that would raise a second one.
            return await PostNewAsync(url, request, null, ct);
        }
    }

    private async Task<WorkItem> PostNewAsync(
        string url, NewWorkItem request, string? assignedTo, CancellationToken ct)
    {
        var patch = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = request.Title.Trim() },
        };

        void Add(string field, object value) =>
            patch.Add(new { op = "add", path = "/fields/" + field, value });

        if (!string.IsNullOrWhiteSpace(request.DescriptionHtml))
            Add("System.Description", request.DescriptionHtml);

        if (!string.IsNullOrWhiteSpace(request.AreaPath)) Add("System.AreaPath", request.AreaPath);
        if (!string.IsNullOrWhiteSpace(request.IterationPath)) Add("System.IterationPath", request.IterationPath);
        if (!string.IsNullOrWhiteSpace(assignedTo)) Add("System.AssignedTo", assignedTo);
        if (!string.IsNullOrWhiteSpace(request.Tags)) Add("System.Tags", request.Tags);
        if (request.Priority is >= 1 and <= 4) Add(PriorityField, request.Priority);

        if (request.EstimateHours is > 0)
        {
            var hours = Math.Round(request.EstimateHours.Value, 2);
            Add("Microsoft.VSTS.Scheduling.RemainingWork", hours);
            Add("Microsoft.VSTS.Scheduling.OriginalEstimate", hours);
        }

        if (request.ParentId is int parent)
        {
            patch.Add(new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "System.LinkTypes.Hierarchy-Reverse",
                    url = $"{OrgUrl}/_apis/wit/workItems/{parent}",
                },
            });
        }

        using var doc = await SendAsync(HttpMethod.Post, url, patch, ct, "application/json-patch+json");
        return Map(doc.RootElement);
    }
}

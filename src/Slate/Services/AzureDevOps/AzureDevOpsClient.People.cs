using System.Net.Http;
using Slate.Models;

namespace Slate.Services.AzureDevOps;

/// <summary>
/// Who can be @-mentioned in a discussion.
///
/// Team membership is asked for first because it is the only source that hands back the
/// identity GUID a live mention needs. The organization graph is the fallback for setups
/// where teams are not readable, and whatever names are already on screen fill in behind
/// both, so the picker is never empty even on a token with the narrowest scope.
/// </summary>
public sealed partial class AzureDevOpsClient
{
    private const int MaxTeamsRead = 10;
    private const int MaxMembers = 500;

    private IReadOnlyList<OrgMember>? _members;
    private DateTimeOffset _membersReadAt;

    /// <summary>
    /// Drops the roster. Called when the connection changes: these people belong to the
    /// organization they were read from, and offering them anywhere else links mentions
    /// to identities that do not exist there.
    /// </summary>
    public void ForgetPeople()
    {
        _members = null;
        _membersReadAt = default;
    }

    public async Task<IReadOnlyList<OrgMember>> GetOrgMembersAsync(
        IEnumerable<string>? knownNames = null, CancellationToken ct = default)
    {
        var extra = (knownNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();

        if (_members is { Count: > 0 } cached && DateTimeOffset.Now - _membersReadAt < TimeSpan.FromMinutes(30))
            return extra.Length == 0 ? cached : Merge(cached, extra);

        var found = new Dictionary<string, OrgMember>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in await TryTeamMembersAsync(ct)) Remember(found, member);
        if (found.Count == 0)
            foreach (var member in await TryGraphUsersAsync(ct)) Remember(found, member);

        _members = [.. found.Values.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)];
        _membersReadAt = DateTimeOffset.Now;

        return extra.Length == 0 ? _members : Merge(_members, extra);
    }

    /// <summary>
    /// A name already on a work item is better than no name, but it never displaces a real
    /// identity, which would cost that mention its link.
    /// </summary>
    private static IReadOnlyList<OrgMember> Merge(IReadOnlyList<OrgMember> known, IEnumerable<string> names)
    {
        var found = new Dictionary<string, OrgMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in known) Remember(found, member);
        foreach (var name in names) Remember(found, new OrgMember(name.Trim(), "", ""));

        return [.. found.Values.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    private static void Remember(Dictionary<string, OrgMember> found, OrgMember member)
    {
        if (string.IsNullOrWhiteSpace(member.DisplayName)) return;
        if (LooksLikeService(member)) return;

        if (found.TryGetValue(member.DisplayName, out var existing) && existing.CanLink) return;
        found[member.DisplayName] = member;
    }

    /// <summary>Build agents and the like are team members but are never worth mentioning.</summary>
    private static bool LooksLikeService(OrgMember member) =>
        member.DisplayName.Contains("Build Service", StringComparison.OrdinalIgnoreCase)
        || member.DisplayName.Contains("Service Account", StringComparison.OrdinalIgnoreCase)
        || member.DisplayName.StartsWith("Project Collection ", StringComparison.OrdinalIgnoreCase)
        || member.UniqueName.StartsWith("vstfs:", StringComparison.OrdinalIgnoreCase);

    private async Task<List<OrgMember>> TryTeamMembersAsync(CancellationToken ct)
    {
        var members = new List<OrgMember>();

        try
        {
            var configured = settings.Current.Ado.Project;
            List<string> projects = string.IsNullOrWhiteSpace(configured)
                ? [.. (await GetProjectsAsync(ct)).Take(3).Select(p => p.Id)]
                : [configured];

            var teamsRead = 0;

            foreach (var project in projects)
            {
                var scope = Uri.EscapeDataString(project);

                using var teams = await SendAsync(HttpMethod.Get,
                    $"{OrgUrl}/_apis/projects/{scope}/teams?api-version={ApiVersion}", null, ct);

                foreach (var team in ReadValueArray(teams.RootElement))
                {
                    if (teamsRead++ >= MaxTeamsRead || members.Count >= MaxMembers) return members;

                    var teamId = team.TryGetProperty("id", out var t) ? t.GetString() ?? "" : "";
                    if (teamId.Length == 0) continue;

                    using var roster = await SendAsync(HttpMethod.Get,
                        $"{OrgUrl}/_apis/projects/{scope}/teams/{teamId}/members?api-version={ApiVersion}",
                        null, ct);

                    foreach (var entry in ReadValueArray(roster.RootElement))
                    {
                        if (!entry.TryGetProperty("identity", out var identity)) continue;

                        members.Add(new OrgMember(
                            identity.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
                            identity.TryGetProperty("uniqueName", out var u) ? u.GetString() ?? "" : "",
                            identity.TryGetProperty("id", out var i) ? i.GetString() ?? "" : ""));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is AzureDevOpsException or OperationCanceledException)
        {
            // Not every credential can read teams; the caller falls back.
        }

        return members;
    }

    private async Task<List<OrgMember>> TryGraphUsersAsync(CancellationToken ct)
    {
        var members = new List<OrgMember>();
        if (AccountName() is not { Length: > 0 } account) return members;

        try
        {
            using var doc = await SendAsync(HttpMethod.Get,
                $"https://vssps.dev.azure.com/{account}/_apis/graph/users?api-version=7.1-preview.1",
                null, ct);

            foreach (var user in ReadValueArray(doc.RootElement))
            {
                if (user.TryGetProperty("subjectKind", out var kind) &&
                    !string.Equals(kind.GetString(), "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                members.Add(new OrgMember(
                    user.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
                    user.TryGetProperty("mailAddress", out var m) ? m.GetString() ?? "" : "",
                    // The graph hands back a descriptor rather than the identity GUID a
                    // mention needs, so these people are offered without a link.
                    ""));
            }
        }
        catch (Exception ex) when (ex is AzureDevOpsException or OperationCanceledException)
        {
            // The graph endpoint needs a scope a work-item token does not have to carry.
        }

        return members;
    }

    /// <summary>The organization name out of the configured URL, in either of its two shapes.</summary>
    private string AccountName()
    {
        if (!Uri.TryCreate(OrgUrl, UriKind.Absolute, out var uri)) return "";

        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            return uri.Host[..uri.Host.IndexOf('.')];

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : "";
    }
}

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Slate.Services;

/// <summary>A release on GitHub that is newer than the one running.</summary>
public sealed record ReleaseInfo(string Version, string Url);

/// <summary>
/// Asks GitHub once per launch whether there is a newer release, so the app can say so
/// instead of leaving people on an old build indefinitely.
///
/// Deliberately best-effort and quiet: no network, a rate limit, a rewritten API or a
/// missing release all mean "nothing to say" rather than an error in the user's face. The
/// app is perfectly usable without ever reaching GitHub, so a failure here is not news.
/// </summary>
public sealed class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/jakemorgangit/Slate/releases/latest";

    /// <summary>Where to send someone who wants the download, if the API gave us nothing better.</summary>
    private const string ReleasesPage = "https://github.com/jakemorgangit/Slate/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The newer release, once one has been found and not yet dismissed.</summary>
    public ReleaseInfo? Available { get; private set; }

    public event Action? Changed;

    private bool _checked;

    /// <summary>
    /// Runs at most once per launch. Checking again on every navigation would spend the
    /// unauthenticated rate limit on a question whose answer cannot change while the app
    /// is open.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (_checked) return;
        _checked = true;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // GitHub rejects anonymous calls that do not identify themselves.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Slate", AppInfo.Version));

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            // A draft or prerelease is not something to push people onto.
            if (Flag(root, "draft") || Flag(root, "prerelease")) return;

            var tag = Text(root, "tag_name");
            if (!IsNewer(tag, AppInfo.Version)) return;

            var url = Text(root, "html_url");
            Available = new ReleaseInfo(
                tag.TrimStart('v', 'V'),
                string.IsNullOrWhiteSpace(url) ? ReleasesPage : url);

            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or InvalidOperationException)
        {
            // Offline, blocked, rate limited or an unexpected payload. Say nothing.
        }
    }

    /// <summary>Hides the notice for the rest of this run.</summary>
    public void Dismiss()
    {
        if (Available is null) return;
        Available = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// True when the tag names a version above the one running. Anything unparseable counts
    /// as "not newer": a tag we cannot read is no reason to tell somebody to upgrade.
    /// </summary>
    internal static bool IsNewer(string? tag, string current)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;

        return Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var latest)
               && Version.TryParse(current, out var running)
               && latest > running;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

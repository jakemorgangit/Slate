using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Slate.Services;

/// <summary>A release on GitHub that is newer than the one running.</summary>
public sealed record ReleaseInfo(string Version, string Url, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The file that is this same build at the new version, when the release has it.</summary>
    public ReleaseAsset? AssetFor(string? name) =>
        name is null ? null : Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One file attached to a release. <paramref name="Sha256"/> is the lower-case hex digest
/// GitHub computed on upload, or empty when it gave none - in which case the file is never
/// installed, because there would be nothing to check the download against.
/// </summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string Sha256);

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

    /// <summary>
    /// Test hook: SLATE_UPDATE_FEED points the check at a stand-in for the latest-release
    /// API (a local listener serving the same JSON), so installing an update can be tried
    /// end to end without publishing anything. It exists only in Debug builds. A Release
    /// build compiles it out, so nothing in the environment can steer a shipped copy to
    /// download and run an executable from anywhere but this repository's releases.
    /// </summary>
    internal static Uri? TestFeed
    {
        get
        {
#if DEBUG
            return Uri.TryCreate(Environment.GetEnvironmentVariable("SLATE_UPDATE_FEED"), UriKind.Absolute, out var feed)
                   && (feed.Scheme == Uri.UriSchemeHttp || feed.Scheme == Uri.UriSchemeHttps)
                ? feed
                : null;
#else
            return null;
#endif
        }
    }

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
            using var request = new HttpRequestMessage(HttpMethod.Get, TestFeed?.AbsoluteUri ?? LatestReleaseApi);
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
                tag.Trim().TrimStart('v', 'V'),
                string.IsNullOrWhiteSpace(url) ? ReleasesPage : url,
                Assets(root));

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

    /// <summary>
    /// The files on the release, keeping only what could be installed: a name, an address
    /// and a SHA-256 digest in the "sha256:hex" form the API reports.
    /// </summary>
    private static List<ReleaseAsset> Assets(JsonElement release)
    {
        var assets = new List<ReleaseAsset>();
        if (!release.TryGetProperty("assets", out var list) || list.ValueKind != JsonValueKind.Array) return assets;

        foreach (var asset in list.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;

            var name = Text(asset, "name");
            var url = Text(asset, "browser_download_url");
            if (name.Length == 0 || url.Length == 0) continue;

            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                       && s.TryGetInt64(out var n) && n > 0
                ? n
                : 0;
            assets.Add(new ReleaseAsset(name, url, size, Sha256(Text(asset, "digest"))));
        }

        return assets;
    }

    /// <summary>The hex part of "sha256:...", lower-cased; empty for anything else.</summary>
    internal static string Sha256(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";

        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : "";
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

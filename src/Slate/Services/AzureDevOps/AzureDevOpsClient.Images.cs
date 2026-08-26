using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Slate.Services.AzureDevOps;

/// <summary>
/// Pulling attachment images into the HTML that gets rendered.
///
/// Images on a work item sit behind the same credential as the API, so the WebView cannot
/// fetch them itself - it would ask anonymously and be handed a sign-in page. They are
/// downloaded here and rewritten as data URIs instead.
/// </summary>
public sealed partial class AzureDevOpsClient
{
    private const int MaxInlineImageBytes = 6 * 1024 * 1024;
    private const int MaxCachedImages = 250;

    [GeneratedRegex("""(<img\b[^>]*?\bsrc\s*=\s*)("([^"]*)"|'([^']*)')""",
        RegexOptions.IgnoreCase)]
    private static partial Regex ImageSource();

    private readonly ConcurrentDictionary<string, string> _inlinedImages = new();

    /// <summary>Rewrites every image this organization owns as a data URI, in one pass.</summary>
    private async Task<string> InlineImagesAsync(string html, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains("<img", StringComparison.OrdinalIgnoreCase))
            return html;

        var matches = ImageSource().Matches(html);
        if (matches.Count == 0) return html;

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in matches)
        {
            var raw = SourceOf(match);
            if (raw.Length == 0 || replacements.ContainsKey(raw)) continue;
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            if (!IsOurs(WebUtility.HtmlDecode(raw), out var absolute)) continue;
            if (await FetchAsDataUriAsync(absolute, ct) is { } data) replacements[raw] = data;
        }

        if (replacements.Count == 0) return html;

        return ImageSource().Replace(html, match =>
            replacements.TryGetValue(SourceOf(match), out var data)
                ? match.Groups[1].Value + "\"" + data + "\""
                : match.Value);
    }

    private static string SourceOf(Match match) =>
        match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;

    /// <summary>
    /// True when the image lives in our own organization, which is the only place the
    /// credential may be sent. Anything hosted elsewhere is left for the WebView to fetch.
    /// </summary>
    private bool IsOurs(string url, out string absolute)
    {
        absolute = "";
        if (string.IsNullOrWhiteSpace(url)) return false;

        var isAbsolute = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!isAbsolute)
        {
            // Relative sources are resolved against the organization, which is where the
            // work item they came from lives.
            absolute = OrgUrl + "/" + url.TrimStart(Slash);
            return true;
        }

        if (!IsSameOrigin(url)) return false;

        absolute = url;
        return true;
    }

    /// <summary>
    /// Same scheme, host, port and organization path - compared as a URL, not as a string.
    /// "https://contoso.visualstudio.com.example.net/x.png" has the organization URL as a
    /// prefix, and a prefix test would send the credential straight to it.
    /// </summary>
    private bool IsSameOrigin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate)) return false;
        if (!Uri.TryCreate(OrgUrl, UriKind.Absolute, out var org)) return false;

        if (!string.Equals(candidate.Scheme, org.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Host, org.Host, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != org.Port)
            return false;

        // dev.azure.com carries every organization, so the first path segment counts too.
        var orgPath = org.AbsolutePath.TrimEnd('/');
        return orgPath.Length == 0
               || candidate.AbsolutePath.Equals(orgPath, StringComparison.OrdinalIgnoreCase)
               || candidate.AbsolutePath.StartsWith(orgPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private const char Slash = '/';

    private async Task<string?> FetchAsDataUriAsync(string url, CancellationToken ct)
    {
        if (_inlinedImages.TryGetValue(url, out var cached)) return cached;

        try
        {
            using var request = await BuildRequestAsync(HttpMethod.Get, url, ct);
            request.Headers.Accept.Clear();

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > MaxInlineImageBytes) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length is 0 or > MaxInlineImageBytes) return null;

            // The attachments endpoint serves everything as octet-stream, so the bytes
            // themselves have to say what they are.
            if (SniffImageType(bytes) is not { } type) return null;

            var data = "data:" + type + ";base64," + Convert.ToBase64String(bytes);
            if (_inlinedImages.Count < MaxCachedImages) _inlinedImages[url] = data;
            return data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or InvalidOperationException or AzureDevOpsException)
        {
            // A picture that will not load must never stop the work item from opening.
            return null;
        }
    }

    private static string? SniffImageType(byte[] bytes)
    {
        if (bytes.Length < 12) return null;

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) return "image/bmp";

        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";

        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(256, bytes.Length)).TrimStart();
        var isSvg = head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
                    || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                        && head.Contains("<svg", StringComparison.OrdinalIgnoreCase));

        return isSvg ? "image/svg+xml" : null;
    }
}

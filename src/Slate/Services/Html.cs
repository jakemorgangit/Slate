using System.Text.RegularExpressions;
using Slate.Models;

namespace Slate.Services;

/// <summary>
/// Minimal HTML hygiene for the rich-text fields Azure DevOps returns (description, repro
/// steps, acceptance criteria). The content is the user's own and is rendered inside a local
/// WebView, but scripts and event handlers still have no business running here.
/// </summary>
public static partial class Html
{
    private const string DangerousNames = "script|style|iframe|object|embed|link|meta|svg|math|form|base|frame|frameset|applet";

    [GeneratedRegex($@"<\s*({DangerousNames})\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DangerousBlocks();

    [GeneratedRegex($@"<\s*/?\s*({DangerousNames})\b[^>]*/?>",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DangerousTags();

    /// <summary>
    /// An inline event handler and the separator in front of it. One separator, not two - and
    /// "/" counts, because the HTML tokenizer treats a stray solidus inside a tag as a place
    /// where the next attribute may start, which is what makes &lt;img/onerror=...&gt; run.
    /// </summary>
    [GeneratedRegex(@"[\s/]on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex InlineHandlers();

    /// <summary>Any attribute that carries a URL the browser will follow or fetch.</summary>
    [GeneratedRegex(@"\b(href|src|xlink:href|formaction|action|poster)\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UrlAttributes();

    /// <summary>
    /// One start tag, quoted attribute values included - so a "&gt;" inside a title does not end
    /// the tag early and leave the attributes after it unexamined.
    /// </summary>
    [GeneratedRegex(@"<\s*[a-zA-Z][^>""']*(?:(?:""[^""]*""|'[^']*')[^>""']*)*>",
        RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex StartTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        try
        {
            var clean = DangerousBlocks().Replace(html, "");
            clean = DangerousTags().Replace(clean, "");

            // Attributes are cleaned inside the tags they belong to. Sweeping the whole
            // document instead would take "the once=true flag" out of somebody's prose.
            return StartTag().Replace(clean, tag =>
            {
                // Replaced with a space rather than nothing, so taking a handler out cannot
                // glue the attributes either side of it into one.
                var attributes = InlineHandlers().Replace(tag.Value, " ");
                return NeutraliseUrls(attributes);
            }).Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological markup: show it as text rather than as markup that was not checked.
            return ToBasicHtml(ToPlainTextBlock(html));
        }
    }

    /// <summary>Points any attribute carrying a scheme we do not trust at nothing instead.</summary>
    private static string NeutraliseUrls(string html) => UrlAttributes().Replace(html, match =>
    {
        var value = match.Groups[3].Success ? match.Groups[3].Value
            : match.Groups[4].Success ? match.Groups[4].Value
            : match.Groups[5].Value;

        return IsSafeUrl(value) ? match.Value : match.Groups[1].Value + "=\"#\"";
    });

    /// <summary>
    /// Judged on what the browser will actually see: entities decoded, and the whitespace and
    /// control characters it ignores stripped out, so "jav&amp;#x09;ascript:" is not mistaken
    /// for a relative path.
    /// </summary>
    private static bool IsSafeUrl(string value)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(value);
        var bare = new string(decoded.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        if (bare.Length == 0) return true;

        var colon = bare.IndexOf(':');
        if (colon < 0) return true;

        // A colon that arrives after a path, query or fragment is not a scheme.
        var scheme = bare[..colon];
        if (scheme.AsSpan().IndexOfAny('/', '?', '#') >= 0) return true;

        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
               || bare.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns what someone typed into the comment box into the HTML the API expects: escaped,
    /// with line breaks preserved.
    /// </summary>
    public static string ToBasicHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var escaped = text.Trim()
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

        return escaped
            .Replace("\r\n", "\n")
            .Replace("\n", "<br>");
    }

    /// <summary>
    /// Turns what someone typed into the HTML Azure DevOps stores, honouring the format they
    /// chose. HTML is passed through sanitised rather than escaped, because in that mode the
    /// markup is the point.
    /// </summary>
    public static string FromFormat(string? text, TextFormat format) => format switch
    {
        TextFormat.Markdown => Markdown.ToHtml(text),
        TextFormat.Html => Sanitize(text),
        _ => ToBasicHtml(text),
    };

    /// <summary>
    /// Renders a comment, turning every name the author picked from the mention list into the
    /// anchor Azure DevOps recognises. People it could not resolve to an identity stay as
    /// plain "@Name" text, which still reads correctly - it simply is not a live link.
    /// </summary>
    public static string ToCommentHtml(string? text, TextFormat format, IReadOnlyList<OrgMember> mentioned)
    {
        var html = FromFormat(text, format);
        if (mentioned.Count == 0) return html;

        // Longest name first, so "@Jake Morgan-Price" is not eaten by "@Jake Morgan".
        foreach (var member in mentioned.Where(m => m.CanLink)
                     .DistinctBy(m => m.Id)
                     .OrderByDescending(m => m.DisplayName.Length))
        {
            var needle = "@" + Escape(member.DisplayName);
            if (!html.Contains(needle, StringComparison.Ordinal)) continue;

            html = html.Replace(needle,
                $"<a href=\"#\" data-vss-mention=\"version:2.0,{member.Id}\">@{Escape(member.DisplayName)}</a>",
                StringComparison.Ordinal);
        }

        return html;
    }

    /// <summary>
    /// Flattens an HTML fragment to text that keeps its line structure, so switching the
    /// description editor out of HTML mode leaves something sensible to carry on typing in.
    /// </summary>
    public static string ToPlainTextBlock(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = BlockEnds().Replace(html, "\n");
        text = LineBreaks().Replace(text, "\n");
        text = ListStarts().Replace(text, "\n- ");
        text = AnyTag().Replace(text, "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = SpacesOnly().Replace(text, " ");
        text = ManyBlankLines().Replace(text.Replace("\r\n", "\n"), "\n\n");

        return string.Join("\n", text.Split('\n').Select(line => line.Trim())).Trim();
    }

    [GeneratedRegex(@"</\s*(p|div|h[1-6]|li|tr|blockquote|pre|table|ul|ol)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnds();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListStarts();

    [GeneratedRegex(@"[ \t\f\v\u00a0]+")]
    private static partial Regex SpacesOnly();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBlankLines();

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    /// <summary>Plain-text preview of an HTML fragment, for tooltips and list rows.</summary>
    public static string ToPlainText(string? html, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = AnyTag().Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Whitespace().Replace(text, " ").Trim();

        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

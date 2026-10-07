using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>
/// Keeps answers free of links that would break in the app. A Markdown link without a full web address, e.g.
/// <c>[Evidence 1](evidence/1)</c>, resolves against the app's own address and gives a 404, so it's turned back into text.
/// Links to full <c>https://</c> addresses, e.g. web search sources, are kept.
/// </summary>
internal static partial class AnswerLinks
{
    public static string? RemoveRelativeLinks(string? answer)
        => string.IsNullOrEmpty(answer) ? answer : MarkdownLink().Replace(answer, static link =>
        {
            var (text, target) = (link.Groups["text"].Value, link.Groups["target"].Value.Trim());
            if (IsFullAddress(target))
            {
                return link.Value;
            }

            // A citation keeps its brackets, so it still reads, and checks, as [Evidence n].
            return CitationText().IsMatch(text) ? $"[{text}]" : text;
        });

    private static bool IsFullAddress(string target)
        => Uri.TryCreate(target.Split(' ', 2)[0], UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto);

    [GeneratedRegex(@"(?<!!)\[(?<text>[^\[\]]+)\]\((?<target>[^()\s]*(?:\s+""[^""]*"")?)\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^Evidence \d{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CitationText();
}

using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.ValueObjects;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>
/// Checks answers cite numbered evidence (<c>--- index Evidence n ---</c>) as <c>[Evidence n]</c>, and, once an answer is
/// checked, shows each citation as a link to its source or its source's name.
/// </summary>
internal static partial class Citations
{
    /// <summary>The prompt and validator for a run: the citation check when required and possible, then the definition's own.</summary>
    public static (string Prompt, Func<AgentResult, string?>? Validate) ForRun(AgentDefinition definition, string prompt, string? evidence)
    {
        var evidenceCount = definition.RequiredCitations ? CountEvidence(evidence) : 0;
        if (evidenceCount == 0)
        {
            return (prompt, definition.Validate);
        }

        var ownCheck = definition.Validate;
        return (prompt + PromptText.CiteEvidence, result => Check(result.Output, evidenceCount) ?? ownCheck?.Invoke(result));
    }

    /// <summary>The highest evidence number, or 0 when the evidence isn't numbered.</summary>
    public static int CountEvidence(string? evidence)
        => string.IsNullOrEmpty(evidence) ? 0 : Numbers(EvidenceLabel(), evidence).DefaultIfEmpty(0).Max();

    /// <summary>Null when the answer cites at least one piece of evidence and only evidence that exists; otherwise why not.</summary>
    public static string? Check(string? answer, int evidenceCount)
    {
        var cited = Numbers(CitationPattern(), answer ?? string.Empty).ToHashSet();
        if (cited.Count == 0)
        {
            return "Cite the evidence each point relies on as [Evidence n].";
        }

        var unknown = cited.Where(number => number < 1 || number > evidenceCount).Order().ToList();
        return unknown.Count == 0
            ? null
            : $"{string.Join(", ", unknown.Select(number => $"[Evidence {number}]"))} doesn't exist; cite only [Evidence 1] to [Evidence {evidenceCount}].";
    }

    /// <summary>
    /// Replaces each <c>[Evidence n]</c> that has a source with <c>&lt;a href="link"&gt;name&lt;/a&gt;</c>, or the name alone
    /// when it has no web address. Names and links come from search results, so both are HTML-encoded. A number without a
    /// source is left as it is. Run only on an answer that has passed <see cref="Check"/>.
    /// </summary>
    public static string? Render(string? answer, IReadOnlyList<EvidenceSource> sources)
    {
        if (string.IsNullOrEmpty(answer) || sources.Count == 0)
        {
            return answer;
        }

        // Built once, so every citation of the same evidence reads the same.
        var shown = sources.ToDictionary(static source => source.Number, Show);
        return CitationPattern().Replace(answer, citation =>
            int.TryParse(citation.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && shown.TryGetValue(number, out var text) ? text : citation.Value);
    }

    /// <summary>Removes every <c>[Evidence n]</c>, with the space before it, for an agent that doesn't show citations.</summary>
    public static string? Remove(string? answer)
        => string.IsNullOrEmpty(answer) ? answer : CitationWithSpaceBefore().Replace(answer, string.Empty);

    private static string Show(EvidenceSource source)
    {
        var name = WebUtility.HtmlEncode(source.Name.Trim());
        return source.Link is { IsAbsoluteUri: true } link && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp)
            ? $"<a href=\"{WebUtility.HtmlEncode(link.AbsoluteUri)}\">{name}</a>"
            : name;
    }

    private static IEnumerable<int> Numbers(Regex pattern, string text)
    {
        foreach (Match match in pattern.Matches(text))
        {
            if (int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                yield return number;
            }
        }
    }

    [GeneratedRegex(@"Evidence (\d{1,6}) ---", RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceLabel();

    [GeneratedRegex(@"\[Evidence (\d{1,6})\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationPattern();

    [GeneratedRegex(@"[ \t]*\[Evidence \d{1,6}\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationWithSpaceBefore();
}

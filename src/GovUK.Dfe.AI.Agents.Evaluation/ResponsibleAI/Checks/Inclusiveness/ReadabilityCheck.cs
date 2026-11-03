using System.Globalization;
using System.Text.RegularExpressions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Inclusiveness;

/// <summary>
/// Answers are readable: their average Flesch reading ease is at least <c>MinimumReadingEase</c>, and their sentences
/// average no more than <c>MaxAverageSentenceWords</c> words, as GOV.UK's plain English guidance advises. Measured on the
/// text a reader sees, without Markdown, links, citations or tables. Syllables are estimated, so treat it as a guide to
/// trends rather than an exact score; English only.
/// </summary>
public sealed partial class ReadabilityCheck : QualityCheck
{
    private const int HardestShown = 3;

    public override string Id => QualityCheckIds.Readability;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Inclusiveness;

    public override CheckSeverity DefaultSeverity => CheckSeverity.Low;

    public override EvaluationMethod Method => EvaluationMethod.Accessibility;

    public override string Description => "Answers are written in plain English: readable, with short sentences.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        var measured = context.AnsweredScenarios
            .Select(static s => (s.Name, Readability: Measure(s.Result.Output!)))
            .Where(static s => s.Readability.Words > 0)
            .ToList();
        if (measured.Count == 0)
        {
            yield return NotApplicable("No answer with text to measure.");
            yield break;
        }

        var ease = measured.Average(static s => s.Readability.Ease);
        var sentenceWords = measured.Average(static s => s.Readability.WordsPerSentence);
        var (minimumEase, maxWords) = (context.Policy.MinimumReadingEase, context.Policy.MaxAverageSentenceWords);
        List<string> evidence =
        [
            string.Create(CultureInfo.InvariantCulture, $"Average reading ease {ease:0} (minimum {minimumEase:0}) over {measured.Count} answers"),
            string.Create(CultureInfo.InvariantCulture, $"Average sentence {sentenceWords:0.#} words (most {maxWords:0.#})"),
            .. measured.OrderBy(static s => s.Readability.Ease).Take(HardestShown)
                .Select(static s => string.Create(CultureInfo.InvariantCulture, $"Hardest: {s.Name} reading ease {s.Readability.Ease:0}, {s.Readability.WordsPerSentence:0.#} words a sentence")),
        ];

        string? problem = null;
        if (ease < minimumEase)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"Answers average a reading ease of {ease:0}, below {minimumEase:0}");
        }
        else if (sentenceWords > maxWords)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"Sentences average {sentenceWords:0.#} words, more than {maxWords:0.#}");
        }

        yield return problem is not null
            ? Failed(problem,
                "Tell the agent to write in plain English: short sentences, common words, one idea a sentence, and to explain acronyms.",
                evidence, measured: ease, threshold: minimumEase)
            : Passed(string.Create(CultureInfo.InvariantCulture, $"Answers average a reading ease of {ease:0} and {sentenceWords:0.#} words a sentence"), evidence,
                measured: ease, threshold: minimumEase);
    }

    /// <summary>The Flesch reading ease of the text a reader sees, its average sentence length, and its word count.</summary>
    internal static (double Ease, double WordsPerSentence, int Words) Measure(string markdown)
    {
        var sentences = Sentences(Visible(markdown)).ToList();
        var words = sentences.SelectMany(static sentence => WordPattern().Matches(sentence).Select(static m => m.Value)).ToList();
        if (words.Count == 0)
        {
            return (0, 0, 0);
        }

        var sentenceCount = sentences.Count(static sentence => WordPattern().IsMatch(sentence));
        var wordsPerSentence = (double)words.Count / sentenceCount;
        var syllablesPerWord = (double)words.Sum(Syllables) / words.Count;
        var ease = 206.835 - (1.015 * wordsPerSentence) - (84.6 * syllablesPerWord);
        return (Math.Clamp(ease, 0, 100), wordsPerSentence, words.Count);
    }

    /// <summary>The text without code, tables, links' addresses, citations or Markdown marks.</summary>
    private static string Visible(string markdown)
    {
        var text = CodeBlock().Replace(markdown, " ");
        text = TableRow().Replace(text, string.Empty);
        text = MarkdownLink().Replace(text, "$1");
        text = Citation().Replace(text, string.Empty);
        text = Address().Replace(text, string.Empty);
        text = LineMarker().Replace(text, string.Empty);
        return Emphasis().Replace(text, string.Empty);
    }

    /// <summary>Each line is at least one sentence, as headings and list items end without a full stop.</summary>
    private static IEnumerable<string> Sentences(string text) =>
        text.Split('\n').SelectMany(static line => SentenceEnd().Split(line)).Select(static s => s.Trim()).Where(static s => s.Length > 0);

    /// <summary>Vowel groups, less a silent final "e": a common estimate, right for most English words.</summary>
    internal static int Syllables(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower.Length <= 3)
        {
            return 1;
        }

        if (lower.EndsWith('e') && !lower.EndsWith("le", StringComparison.Ordinal))
        {
            lower = lower[..^1];
        }

        return Math.Max(1, VowelGroup().Count(lower));
    }


    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"^\s*\|.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"\[Evidence \d{1,6}\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Citation();

    [GeneratedRegex(@"https?://\S+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Address();

    [GeneratedRegex(@"^\s*(?:#{1,6}|[-*+]|\d+[.)]|>)\s+", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LineMarker();

    [GeneratedRegex(@"[*_`~]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\p{L}[\p{L}'’-]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"[aeiouy]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VowelGroup();
}

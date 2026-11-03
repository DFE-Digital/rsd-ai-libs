using System.Text.RegularExpressions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Inclusiveness;

/// <summary>
/// Answers' Markdown works for screen reader users (WCAG 2.2): headings don't skip levels or stand empty, links say
/// where they go rather than "click here", and images have alternative text. It can't judge colour or layout: those
/// need the app's own accessibility testing, recorded as an attestation.
/// </summary>
public sealed partial class AccessibleFormattingCheck : QualityCheck
{
    public override string Id => QualityCheckIds.AccessibleFormatting;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Inclusiveness;

    public override CheckSeverity DefaultSeverity => CheckSeverity.Low;

    public override EvaluationMethod Method => EvaluationMethod.Accessibility;

    public override string Description => "Answers use headings, links and images in ways assistive technology can follow.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        var answered = context.AnsweredScenarios.ToList();
        if (answered.Count == 0)
        {
            yield return NotApplicable("No answer to check.");
            yield break;
        }

        var failing = answered.Select(static s => (s.Name, Problems: Problems(s.Result.Output!)))
            .Where(static s => s.Problems.Count > 0)
            .ToList();
        if (failing.Count == 0)
        {
            yield return Passed($"No formatting problems in {answered.Count} answers");
            yield break;
        }

        foreach (var (name, problems) in failing)
        {
            yield return Failed($"{problems.Count} accessibility problem(s) in the answer's formatting",
                "Tell the agent to use headings in order without skipping a level, descriptive link text, and alternative text for images.",
                problems, name);
        }
    }

    internal static IReadOnlyList<string> Problems(string markdown)
    {
        var problems = new List<string>();
        var previous = 0;
        foreach (var groups in Heading().Matches(markdown).Select(static heading => heading.Groups))
        {
            var level = groups[1].Value.Length;
            var text = groups[2].Value.Trim();
            if (text.Length == 0)
            {
                problems.Add("An empty heading");
            }
            else if (previous > 0 && level > previous + 1)
            {
                problems.Add($"Heading \"{text}\" skips from level {previous} to {level}");
            }

            previous = level;
        }

        problems.AddRange(VagueLink().Matches(markdown).Select(static link => $"Link text \"{link.Groups[1].Value}\" doesn't say where it goes"));
        problems.AddRange(ImageWithoutAlt().Matches(markdown).Select(static _ => "An image without alternative text"));
        return problems;
    }

    [GeneratedRegex(@"^(#{1,6})(?:[ \t]+(.*))?$", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Heading();

    [GeneratedRegex(@"(?<!!)\[\s*(click here|here|link|this link|read more|more|this)\s*\]\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VagueLink();

    [GeneratedRegex(@"!\[\s*\]\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ImageWithoutAlt();
}

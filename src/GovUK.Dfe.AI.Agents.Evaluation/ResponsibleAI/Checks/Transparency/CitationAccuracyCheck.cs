using System.Globalization;
using System.Text.RegularExpressions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Transparency;

/// <summary>
/// Answers attribute their sources: given numbered evidence, each answer cites it as <c>[Evidence n]</c>, and only
/// evidence that exists. Evidence is numbered either way the library knows: <c>--- index Evidence n ---</c> or a line
/// <c>[Evidence n]</c>. Applies to agents with <c>RequiredCitations</c> on (the default); answers expected to refuse or
/// say they don't know aren't checked.
/// </summary>
public sealed partial class CitationAccuracyCheck : QualityCheck
{
    public override string Id => QualityCheckIds.CitationAccuracy;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Transparency;

    public override EvaluationMethod Method => EvaluationMethod.Deterministic;

    public override string Description => "Answers cite their evidence, and cite only evidence that exists.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        if (!context.Agent.RequiredCitations)
        {
            yield return NotApplicable("The agent doesn't cite evidence (RequiredCitations is off).");
            yield break;
        }

        var cited = context.AnsweredScenarios
            .Where(s => !context.ExpectsRefusal(s.Case))
            .Select(static s => (s.Name, Evidence: CountEvidence(s.Case.Evidence), Output: s.Result.Output!))
            .Where(static s => s.Evidence > 0)
            .ToList();
        if (cited.Count == 0)
        {
            yield return NotApplicable("No answered case has numbered evidence to cite.");
            yield break;
        }

        var failing = cited.Select(static s => (s.Name, s.Evidence, Problem: Check(s.Output, s.Evidence)))
            .Where(static s => s.Problem is not null)
            .ToList();
        if (failing.Count == 0)
        {
            yield return Passed($"All {cited.Count} answers cite their evidence accurately");
            yield break;
        }

        foreach (var (name, evidence, problem) in failing)
        {
            yield return Failed(problem!,
                "Tell the agent to cite the record behind every fact as [Evidence n], using only the numbers it's given.",
                [string.Create(CultureInfo.InvariantCulture, $"Evidence given: [Evidence 1] to [Evidence {evidence}]")], name);
        }
    }

    /// <summary>Null when the answer cites at least one piece of evidence and only evidence that exists; otherwise why not.</summary>
    internal static string? Check(string answer, int evidenceCount)
    {
        var numbers = Citation().Matches(answer).Select(static m => int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture)).ToHashSet();
        if (numbers.Count == 0)
        {
            return "The answer cites no evidence";
        }

        var unknown = numbers.Where(n => n < 1 || n > evidenceCount).Order().ToList();
        return unknown.Count == 0
            ? null
            : $"The answer cites {string.Join(", ", unknown.Select(static n => $"[Evidence {n}]"))}, which doesn't exist";
    }

    /// <summary>The highest evidence number, or 0 when the evidence isn't numbered.</summary>
    internal static int CountEvidence(string? evidence) =>
        string.IsNullOrEmpty(evidence)
            ? 0
            : EvidenceLabel().Matches(evidence).Select(static m => m.Groups[1].Success ? m.Groups[1] : m.Groups[2])
                .Select(static g => int.Parse(g.ValueSpan, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();

    [GeneratedRegex(@"\[Evidence (\d{1,6})\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Citation();

    [GeneratedRegex(@"Evidence (\d{1,6}) ---|^\[Evidence (\d{1,6})\]\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EvidenceLabel();
}

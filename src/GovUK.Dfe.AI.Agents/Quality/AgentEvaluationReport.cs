using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Options;
using System.Globalization;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>An agent's test results. Save it as JSON to compare the next version against.</summary>
public sealed record AgentEvaluationReport(string AgentName, IReadOnlyList<AgentTestResult> Results)
{
    /// <summary>Every case ran and got its facts right.</summary>
    public bool Passed => Results.All(static result => result.Passed);

    /// <summary>Each metric's average score across the cases.</summary>
    public IReadOnlyDictionary<string, double> AverageScores => Results
        .SelectMany(static result => result.Scores)
        .GroupBy(static score => score.Key)
        .ToDictionary(static group => group.Key, static group => group.Average(static score => score.Value));

    /// <summary>Each group's average score per metric, from the cases that name a <see cref="AgentTestCase.Group"/>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> AverageScoresByGroup => Results
        .Where(static result => result.Group is not null)
        .GroupBy(static result => result.Group!)
        .ToDictionary(static group => group.Key, static group => (IReadOnlyDictionary<string, double>)group
            .SelectMany(static result => result.Scores)
            .GroupBy(static score => score.Key)
            .ToDictionary(static metric => metric.Key, static metric => metric.Average(static score => score.Value)));

    /// <summary>
    /// Metrics whose best and worst group averages differ by more than <paramref name="maxGap"/>, e.g.
    /// <c>Groundedness: special-schools 3.1, academies 4.4</c>, so the gate can fail an agent that's worse for one group.
    /// </summary>
    /// <param name="metrics">The metrics to compare; none means every metric.</param>
    public IReadOnlyList<string> GroupGaps(double maxGap, params string[] metrics)
    {
        var byGroup = AverageScoresByGroup;
        var names = metrics.Length > 0 ? metrics : [.. byGroup.Values.SelectMany(static scores => scores.Keys).Distinct().Order()];
        var gaps = new List<string>();
        foreach (var metric in names)
        {
            var scored = byGroup.Where(group => group.Value.ContainsKey(metric)).Select(group => (Group: group.Key, Score: group.Value[metric])).ToList();
            if (scored.Count < 2)
            {
                continue;
            }

            var (worst, best) = (scored.MinBy(static group => group.Score), scored.MaxBy(static group => group.Score));
            if (best.Score - worst.Score > maxGap)
            {
                gaps.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{metric}: {worst.Group} {worst.Score:0.##}, {best.Group} {best.Score:0.##}"));
            }
        }

        return gaps;
    }

    /// <summary>
    /// Every reason this release fails <paramref name="gate"/>, as readable lines: cases with wrong facts, metrics below the
    /// minimum or unscored, regressions beyond the tolerance from <paramref name="baseline"/>, and group gaps. Empty: it passes.
    /// </summary>
    /// <param name="baseline">The last released version's report; null on the first release.</param>
    public IReadOnlyList<string> FailuresAgainst(ReleaseGate gate, AgentEvaluationReport? baseline = null) =>
        [.. CaseFailures(), .. ScoreFailuresAgainst(gate, baseline)];

    /// <summary>
    /// Each failed case's failures, e.g. <c>Case evaluation/ks2-trend: Doesn't mention "2024"</c>; with a
    /// <paramref name="section"/>, only that <see cref="ReleaseSections"/> section's.
    /// </summary>
    public IReadOnlyList<string> CaseFailures(string? section = null) =>
    [
        .. Results.Where(result => !result.Passed && (section is null || ReleaseSections.Of(result) == section))
            .SelectMany(static result => result.Failures.Select(failure => $"Case {result.CaseName}: {failure}")),
    ];

    /// <summary>
    /// Why the scores fail <paramref name="gate"/>: metrics below the minimum or unscored, regressions beyond the
    /// tolerance from <paramref name="baseline"/>, and group gaps. These are all in the evaluation section.
    /// </summary>
    public IReadOnlyList<string> ScoreFailuresAgainst(ReleaseGate gate, AgentEvaluationReport? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var averages = AverageScores;
        var failures = new List<string>();

        foreach (var metric in gate.Metrics)
        {
            if (!averages.TryGetValue(metric, out var average))
            {
                failures.Add($"{metric}: not scored");
            }
            else if (average < gate.MinimumScore)
            {
                failures.Add(Invariant($"{metric}: averaged {average:0.##}, below the minimum {gate.MinimumScore:0.##}"));
            }

            if (baseline?.AverageScores.TryGetValue(metric, out var before) == true && averages.TryGetValue(metric, out var now)
                && now < before - gate.Tolerance)
            {
                failures.Add(Invariant($"{metric}: fell from {before:0.##} to {now:0.##}, more than the tolerance {gate.Tolerance:0.##}"));
            }
        }

        if (gate.MaxGroupGap is { } maxGap)
        {
            failures.AddRange(GroupGaps(maxGap, [.. gate.Metrics]).Select(gap => Invariant($"{gap}: a gap over {maxGap:0.##}")));
        }

        return failures;
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}

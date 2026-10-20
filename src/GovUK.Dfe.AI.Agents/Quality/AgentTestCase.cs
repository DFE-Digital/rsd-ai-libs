using System.Text.Json;
using System.Text.Json.Serialization;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>A test for an agent: a prompt, optional evidence, and facts the answer must or mustn't contain (case-insensitive).</summary>
public sealed record AgentTestCase(string Name, string Prompt)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string? Evidence { get; init; }

    public IReadOnlyList<string> MustMention { get; init; } = [];

    public IReadOnlyList<string> MustNotMention { get; init; } = [];

    /// <summary>
    /// Optional: the group the case represents, e.g. "special-schools" or "rural", so the report can compare scores across
    /// groups and catch an agent that serves one group worse.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// A Foundry guardrail blocking the prompt or answer is a correct outcome, e.g. for a case with instructions injected
    /// into its evidence. Such a case passes when it's blocked, and is checked as usual when it isn't.
    /// </summary>
    public bool GuardrailMayBlock { get; init; }

    /// <summary>
    /// The Foundry guardrail must block the prompt or answer, e.g. for instructions planted in the evidence: the case
    /// passes only when it's blocked, so the release gate proves the guardrail catches the attack, not only that the
    /// agent ignored it.
    /// </summary>
    public bool GuardrailMustBlock { get; init; }

    /// <summary>Any other fields in the case's file, by name, for your own checks; the runner ignores them.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extra { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Loads every <c>*.json</c> file in <paramref name="directory"/> and its subfolders, named after the file and any
    /// subfolder, e.g. "guardrail/jailbreak-prompt", so cases can be sorted into sections such as <c>evaluation/</c> and
    /// <c>guardrail/</c>: <c>{ "prompt": "...", "evidence": "...", "mustMention": [ ... ], "mustNotMention": [ ... ],
    /// "group": "...", "guardrailMayBlock": false, "guardrailMustBlock": false }</c>. Other fields are kept in
    /// <see cref="Extra"/>.
    /// </summary>
    public static async Task<IReadOnlyList<AgentTestCase>> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var cases = new List<AgentTestCase>();
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .Select(path => (Path: path, Name: NameOf(directory, path)))
            .OrderBy(static file => file.Name, StringComparer.Ordinal);
        foreach (var (path, name) in files)
        {
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<CaseFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(file?.Prompt))
            {
                throw new InvalidOperationException(string.Format(Constants.ErrorMessages.TestCaseWithoutPrompt, path));
            }

            cases.Add(new AgentTestCase(name, file.Prompt)
            {
                Evidence = file.Evidence,
                MustMention = file.MustMention ?? [],
                MustNotMention = file.MustNotMention ?? [],
                Group = file.Group,
                GuardrailMayBlock = file.GuardrailMayBlock,
                GuardrailMustBlock = file.GuardrailMustBlock,
                Extra = file.Extra,
            });
        }

        return cases;
    }

    /// <summary>The file's path under <paramref name="directory"/>, without ".json" and with "/" between folders.</summary>
    private static string NameOf(string directory, string path)
        => Path.ChangeExtension(Path.GetRelativePath(directory, path), null).Replace(Path.DirectorySeparatorChar, '/');

    private sealed record CaseFile(string? Prompt, string? Evidence, List<string>? MustMention, List<string>? MustNotMention,
        string? Group, bool GuardrailMayBlock, bool GuardrailMustBlock)
    {
        /// <summary>Every other field in the file.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; init; } = [];
    }
}

/// <summary>How one test case went.</summary>
/// <param name="Output">Null when the run failed.</param>
/// <param name="Failures">Missing or forbidden facts, or why the run failed. Empty when it passed.</param>
public sealed record AgentTestResult(string CaseName, string? Output, IReadOnlyList<string> Failures, IReadOnlyDictionary<string, double> Scores)
{
    public bool Passed => Failures.Count == 0;

    /// <summary>The case's <see cref="AgentTestCase.Group"/>.</summary>
    public string? Group { get; init; }

    /// <summary>
    /// A Foundry guardrail blocked the run, as the case allowed or required (<see cref="AgentTestCase.GuardrailMayBlock"/>,
    /// <see cref="AgentTestCase.GuardrailMustBlock"/>).
    /// </summary>
    public bool BlockedByGuardrail { get; init; }
}

/// <summary>
/// What a release must meet, for <see cref="AgentEvaluationReport.FailuresAgainst"/>. Judge scores vary between runs of the
/// same answer, so <see cref="Tolerance"/> allows a small drop from the baseline before it counts as worse.
/// </summary>
public sealed record ReleaseGate
{
    /// <summary>The metrics that must be scored, e.g. "Groundedness" and "Relevance". A missing score fails the gate.</summary>
    public required IReadOnlyList<string> Metrics { get; init; }

    /// <summary>The lowest average each metric may have (scores run 1 to 5).</summary>
    public double MinimumScore { get; init; } = 3.5;

    /// <summary>How far a metric's average may fall below the baseline's before it's a regression. 0.2 absorbs judge noise.</summary>
    public double Tolerance { get; init; } = 0.2;

    /// <summary>Optional: the largest gap allowed between the best- and worst-served test case groups.</summary>
    public double? MaxGroupGap { get; init; }
}

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

    /// <summary>
    /// Metrics averaging below <paramref name="minimum"/>, plus any <paramref name="requiredMetrics"/> with no score at
    /// all, so a failing judge fails the gate instead of passing it.
    /// </summary>
    public IReadOnlyList<string> BelowMinimum(double minimum, params string[] requiredMetrics)
    {
        var averages = AverageScores;
        return [.. averages.Where(score => score.Value < minimum).Select(static score => score.Key)
            .Concat(requiredMetrics.Where(metric => !averages.ContainsKey(metric)))
            .Distinct().Order()];
    }

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
                gaps.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
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
    public IReadOnlyList<string> FailuresAgainst(ReleaseGate gate, AgentEvaluationReport? baseline = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var averages = AverageScores;
        var failures = new List<string>();

        failures.AddRange(Results.Where(static result => !result.Passed)
            .SelectMany(static result => result.Failures.Select(failure => $"Case {result.CaseName}: {failure}")));

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

    /// <summary>
    /// Metrics whose average fell by more than <paramref name="tolerance"/> since <paramref name="baseline"/>, or that
    /// the baseline scored and this run didn't.
    /// </summary>
    public IReadOnlyList<string> RegressionsFrom(AgentEvaluationReport baseline, double tolerance = 0)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var current = AverageScores;
        return [.. baseline.AverageScores
            .Where(before => !current.TryGetValue(before.Key, out var now) || now < before.Value - tolerance)
            .Select(static before => before.Key).Order()];
    }
}

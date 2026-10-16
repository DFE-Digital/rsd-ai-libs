using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Evaluation.Options;

/// <summary>The <c>AiAgents:Evaluation:ReleaseGate</c> section: how agents are tested before they're released.</summary>
public sealed class ReleaseGateSettings
{
    /// <summary>One folder of test cases per agent, named after the agent. Relative to the app's folder.</summary>
    public string TestCasesDirectory { get; set; } = "TestCases";

    /// <summary>Where each run writes <c>&lt;agent&gt;.json</c>, with every answer, score and failure. Relative to the working directory.</summary>
    public string ReportsDirectory { get; set; } = "evaluation-reports";

    /// <summary>
    /// Each agent's last accepted report, <c>&lt;agent&gt;.json</c>, which its scores mustn't fall below by more than
    /// <see cref="Tolerance"/>. Relative to the app's folder. An agent without one is held to <see cref="MinimumScore"/> only.
    /// </summary>
    public string BaselinesDirectory { get; set; } = "Baselines";

    /// <summary>
    /// The judge metrics each agent must be scored on. Empty (the default): <c>AddQualityEvaluation()</c>'s groundedness
    /// and relevance. With your own evaluator, list its metrics.
    /// </summary>
    public List<string> Metrics { get; set; } = [];

    internal IReadOnlyList<string> RequiredMetrics => Metrics.Count == 0 ? JudgeMetrics.All : Metrics;

    /// <summary>The lowest average judge score, per metric, an agent may release with (scores run 1 to 5).</summary>
    public double MinimumScore { get; set; } = 3.5;

    /// <summary>How far a score may fall below the baseline, so a judge's run-to-run noise doesn't block a release.</summary>
    public double Tolerance { get; set; } = 0.2;

    /// <summary>The largest score gap allowed between test case groups. Unset turns the check off.</summary>
    public double? MaxGroupGap { get; set; }

    /// <summary>Runs per test case. Scores are averaged; the facts must be right in every run. Each repeat costs a run.</summary>
    public int Repeats { get; set; } = 1;

    /// <summary>Test cases run at once, across all agents. Keep within the model's quota.</summary>
    public int MaxParallelTestRuns { get; set; } = 4;

    /// <summary>
    /// Fixed refusal sentences from the agents' system prompts, e.g. "outside what I can answer". A case whose
    /// <c>mustMention</c> includes one is checked by that sentence, and left out of the score averages, baselines and group
    /// gaps: relevance marks any refusal as irrelevant, however correct.
    /// </summary>
    public List<string> RefusalPhrases { get; set; } = [];

    /// <summary>
    /// Also sends each release's answers to a Foundry cloud evaluation, scored by Foundry's built-in groundedness and
    /// relevance evaluators with the judge model, so results show on the Foundry project's Evaluations page. It doesn't
    /// affect the release decision. Off by default: it sends test prompts, evidence and answers to Foundry.
    /// </summary>
    public bool PublishToFoundry { get; set; }

    internal IEnumerable<string> Problems()
    {
        (bool Invalid, string Problem)[] checks =
        [
            (string.IsNullOrWhiteSpace(TestCasesDirectory), "TestCasesDirectory"),
            (string.IsNullOrWhiteSpace(ReportsDirectory), "ReportsDirectory"),
            (Metrics.Exists(string.IsNullOrWhiteSpace), "Metrics"),
            (MinimumScore is < 1 or > 5 || double.IsNaN(MinimumScore), "MinimumScore (from 1 to 5)"),
            (!IsZeroOrMore(Tolerance), "Tolerance (0 or more)"),
            (MaxGroupGap is { } gap && !IsZeroOrMore(gap), "MaxGroupGap (0 or more)"),
            (Repeats < 1, "Repeats (1 or more)"),
            (MaxParallelTestRuns < 1, "MaxParallelTestRuns (1 or more)"),
        ];

        return checks.Where(static check => check.Invalid).Select(static check => check.Problem);
    }

    /// <summary>NaN fails too, as no comparison with it is true.</summary>
    private static bool IsZeroOrMore(double value) => value >= 0;

    /// <summary>Whether a test case expects a fixed refusal, so its scores are left out.</summary>
    internal bool ExpectsRefusal(Quality.AgentTestCase testCase) =>
        testCase.MustMention.Any(text => RefusalPhrases.Any(refusal => text.Contains(refusal, StringComparison.OrdinalIgnoreCase)));
}

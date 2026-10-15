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
        if (string.IsNullOrWhiteSpace(TestCasesDirectory))
        {
            yield return "TestCasesDirectory";
        }

        if (string.IsNullOrWhiteSpace(ReportsDirectory))
        {
            yield return "ReportsDirectory";
        }

        if (Metrics.Exists(string.IsNullOrWhiteSpace))
        {
            yield return "Metrics";
        }

        if (MinimumScore is < 1 or > 5 || double.IsNaN(MinimumScore))
        {
            yield return "MinimumScore (from 1 to 5)";
        }

        if (Tolerance < 0 || double.IsNaN(Tolerance))
        {
            yield return "Tolerance (0 or more)";
        }

        if (MaxGroupGap is { } gap && (gap < 0 || double.IsNaN(gap)))
        {
            yield return "MaxGroupGap (0 or more)";
        }

        if (Repeats < 1)
        {
            yield return "Repeats (1 or more)";
        }

        if (MaxParallelTestRuns < 1)
        {
            yield return "MaxParallelTestRuns (1 or more)";
        }
    }

    /// <summary>Whether a test case expects a fixed refusal, so its scores are left out.</summary>
    internal bool ExpectsRefusal(Quality.AgentTestCase testCase) =>
        testCase.MustMention.Any(text => RefusalPhrases.Any(refusal => text.Contains(refusal, StringComparison.OrdinalIgnoreCase)));
}

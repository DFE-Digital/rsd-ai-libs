using System.Diagnostics;
using System.Text.Json;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.Constants;
using GovUK.Dfe.AI.Agents.Evaluation.Exceptions;
using GovUK.Dfe.AI.Agents.Evaluation.Metrics;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.Publishers.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>
/// The release gate's evaluation: runs each agent's test cases on temporary copies, scores them, compares the scores with
/// the agent's baseline and writes a report per agent. <see cref="AgentReleaseGate"/> decides the release from the results.
/// </summary>
internal sealed class AgentReleaseEvaluator(
    IAgentTestRunner agentTestRunner,
    IAgentRuntimeService agentRuntime,
    ReleaseGateSettings settings,
    ReleaseGateJudge judge,
    ReleaseGateMetrics metrics,
    ILogger<AgentReleaseEvaluator> logger,
    IFoundryEvaluationPublisher? foundryEvaluations = null)
{
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(1);

    private static readonly IReadOnlyDictionary<string, double> NoScores = new Dictionary<string, double>();

    /// <param name="followConventions">Fails a case that breaks <see cref="ReleaseSections.Problems"/>, in its section.</param>
    public async Task<IReadOnlyList<AgentGateResult>> EvaluateAsync(IReadOnlyCollection<AgentDefinition> definitions,
        bool followConventions, CancellationToken cancellationToken)
    {
        LogJudge();
        var started = Stopwatch.GetTimestamp();
        try
        {
            var suites = new List<TestSuite>(definitions.Count);
            foreach (var definition in definitions)
            {
                suites.Add(await LoadSuiteAsync(definition, cancellationToken));
            }

            await RunAllCasesAsync(suites, cancellationToken);

            var results = new List<AgentGateResult>(suites.Count);
            foreach (var suite in suites)
            {
                results.Add(await ConcludeAsync(suite, followConventions, cancellationToken));
            }

            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            logger.ReleaseChecksFinished(seconds);
            return results;
        }
        finally
        {
            await DeleteLeftoverTestAgentsAsync();
        }
    }

    private async Task<TestSuite> LoadSuiteAsync(AgentDefinition definition, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, settings.TestCasesDirectory, definition.Name);
        var cases = Directory.Exists(directory) ? await AgentTestCase.LoadAsync(directory, cancellationToken) : [];
        if (cases.Count > 0)
        {
            logger.TestingAgent(definition.Name, cases.Count);
        }

        return new TestSuite(definition, directory, cases);
    }

    /// <summary>
    /// Runs every agent's test cases side by side, at most <see cref="ReleaseGateSettings.MaxParallelTestRuns"/> at a
    /// time. Each case runs on its own temporary copy of its agent, so cases don't affect each other.
    /// </summary>
    private async Task RunAllCasesAsync(IReadOnlyList<TestSuite> suites, CancellationToken cancellationToken)
    {
        var runs = suites.SelectMany(suite => suite.Cases.Select((_, index) => (Suite: suite, Index: index))).ToList();
        var maxParallel = settings.MaxParallelTestRuns;
        logger.RunningTestCases(runs.Count, maxParallel);

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(runs, parallel, async (run, token) =>
        {
            run.Suite.Results[run.Index] = await RunCaseAsync(run.Suite.Definition, run.Suite.Cases[run.Index], token);
        });
    }

    private async Task<AgentTestResult> RunCaseAsync(AgentDefinition definition, AgentTestCase testCase,
        CancellationToken cancellationToken)
    {
        // The candidate is a temporary copy of the agent, so a Foundry version is only created if the agent passes.
        // Repeats average out the judge's run-to-run noise; the case's facts must be right in every repeat.
        var report = await agentTestRunner.RunAsync(definition, [testCase], AgentTestTarget.Candidate,
            settings.Repeats, cancellationToken);
        var result = report.Results[0];

        // A case with "guardrailMayBlock": true passes when the Foundry guardrail blocks it, e.g. a planted instruction.
        // A block on any other case fails it: that's a false positive worth knowing about.
        if (result.BlockedByGuardrail)
        {
            logger.TestCaseBlockedByGuardrail(definition.Name, result.CaseName);
            metrics.TestCase(definition.Name, result, AgentOutcomes.BlockedByGuardrail);
            return result with { Output = ReleaseGateMessages.BlockedByGuardrail };
        }

        if (result.Passed)
        {
            logger.TestCasePassed(definition.Name, result.CaseName);
        }
        else
        {
            logger.TestCaseFailed(definition.Name, result.CaseName, string.Join("; ", result.Failures));
        }

        metrics.TestCase(definition.Name, result, AgentOutcomes.Of(result.Passed));
        return result;
    }

    private async Task<AgentGateResult> ConcludeAsync(TestSuite suite, bool followConventions, CancellationToken cancellationToken)
    {
        var agentName = suite.Definition.Name;
        if (suite.Cases.Count == 0)
        {
            logger.NoTestCases(agentName, suite.Directory);
            return new AgentGateResult(agentName, [], [ReleaseGateMessages.NoTestCases(suite.Directory)]);
        }

        var report = new AgentEvaluationReport(agentName, suite.Results);
        var reportPath = await WriteReportAsync(report, cancellationToken);
        if (foundryEvaluations is not null)
        {
            await foundryEvaluations.PublishAsync(agentName, [.. suite.Cases.Zip(suite.Results)], cancellationToken);
        }

        var (gated, baseline) = judge.HasJudge ? await ScoredAsync(suite, report, cancellationToken) : (report, null);

        // Failed test cases by section, and with a judge: each metric averaging below the minimum, not scored, more than
        // the tolerance below the baseline, or scoring one group of cases much worse than another.
        var broken = followConventions
            ? suite.Cases.SelectMany(c => ReleaseSections.Problems(c).Select(problem => (Guardrail: ReleaseSections.IsGuardrailCase(c.Name), Problem: problem))).ToList()
            : [];
        var result = new AgentGateResult(agentName,
            [.. broken.Where(static b => b.Guardrail).Select(static b => b.Problem), .. gated.CaseFailures(ReleaseSections.Guardrail)],
            [.. broken.Where(static b => !b.Guardrail).Select(static b => b.Problem), .. gated.CaseFailures(ReleaseSections.Evaluation),
                .. gated.ScoreFailuresAgainst(Gate(), baseline)]);
        metrics.Agent(agentName, result.Passed);
        if (result.Passed)
        {
            logger.AgentPassed(agentName, reportPath);
        }
        else
        {
            logger.AgentFailed(agentName, result.Problems.Count, reportPath);
        }

        return result;
    }

    /// <summary>
    /// The report and baseline the scores are judged on. Cases expecting a refusal keep their checks but not their scores:
    /// the judge's relevance score marks any refusal as irrelevant, however correct, so they're checked by their refusal.
    /// </summary>
    private async Task<(AgentEvaluationReport Gated, AgentEvaluationReport? Baseline)> ScoredAsync(TestSuite suite,
        AgentEvaluationReport report, CancellationToken cancellationToken)
    {
        var agentName = suite.Definition.Name;
        var refusals = suite.Cases.Where(settings.ExpectsRefusal).Select(c => c.Name).ToHashSet();
        var gated = WithoutScoresFor(report, refusals);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.AgentScores(agentName, ReleaseGateMessages.Scores(gated.AverageScores), refusals.Count);
        }

        metrics.AverageScores(agentName, gated.AverageScores);

        var baseline = await LoadBaselineAsync(agentName, cancellationToken);
        return (gated, baseline is null ? null : WithoutScoresFor(baseline, refusals));
    }

    private void LogJudge()
    {
        if (judge.HasJudge)
        {
            logger.JudgeOn(judge.Model, settings.MinimumScore);
        }
        else
        {
            logger.JudgeOff();
        }
    }

    /// <summary>The scores each agent must reach.</summary>
    private ReleaseGate Gate() => new()
    {
        Metrics = judge.HasJudge ? settings.RequiredMetrics : [],
        MinimumScore = settings.MinimumScore,
        Tolerance = settings.Tolerance,
        MaxGroupGap = settings.MaxGroupGap,
    };

    private static AgentEvaluationReport WithoutScoresFor(AgentEvaluationReport report, HashSet<string> caseNames) =>
        report with
        {
            Results = [.. report.Results.Select(result => caseNames.Contains(result.CaseName) ? result with { Scores = NoScores } : result)],
        };

    /// <summary>
    /// The agent's last accepted report, if one has been saved to <see cref="ReleaseGateSettings.BaselinesDirectory"/>.
    /// An agent without one is held to the minimum score only.
    /// </summary>
    private async Task<AgentEvaluationReport?> LoadBaselineAsync(string agentName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, settings.BaselinesDirectory, $"{agentName}.json");
        if (!File.Exists(path))
        {
            logger.NoBaseline(agentName, path);
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var baseline = await JsonSerializer.DeserializeAsync<AgentEvaluationReport>(stream, ReportJson, cancellationToken);
            logger.ComparingWithBaseline(agentName, path);
            return baseline;
        }
        catch (JsonException ex)
        {
            throw new ReleaseGateException(ReleaseGateMessages.BaselineUnreadable(path, ex.Message), ex);
        }
    }

    /// <summary>
    /// Each test agent is deleted straight after its run, even when its test fails. This also removes any left by an
    /// earlier run whose delete failed or that was killed. It has its own timeout so it still runs when the gate was
    /// cancelled, and a failure here never hides the gate's own outcome; the next run tries again.
    /// </summary>
    private async Task DeleteLeftoverTestAgentsAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(CleanupTimeout);
            await agentRuntime.DeleteOrphanedEphemeralAgentsAsync(settings.LeftoverTestAgentAge, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LeftoverTestAgentsNotDeleted(ex);
        }
    }

    /// <returns>Where the report was written.</returns>
    private async Task<string> WriteReportAsync(AgentEvaluationReport report, CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(settings.ReportsDirectory);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{report.AgentName}.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, report, ReportJson, cancellationToken);
        return path;
    }

    /// <summary>One agent's test cases, and their results in the same order once they have run.</summary>
    private sealed class TestSuite(AgentDefinition definition, string directory, IReadOnlyList<AgentTestCase> cases)
    {
        public AgentDefinition Definition { get; } = definition;

        public string Directory { get; } = directory;

        public IReadOnlyList<AgentTestCase> Cases { get; } = cases;

        public AgentTestResult[] Results { get; } = new AgentTestResult[cases.Count];
    }
}

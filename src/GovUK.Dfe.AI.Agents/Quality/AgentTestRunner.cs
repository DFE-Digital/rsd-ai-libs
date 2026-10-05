using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Quality;

internal sealed class AgentTestRunner(IAgentService agents, IAgentRunEvaluator? evaluator = null) : IAgentTestRunner
{
    private static readonly IReadOnlyDictionary<string, double> NoScores = new Dictionary<string, double>();

    public async Task<AgentEvaluationReport> RunAsync(AgentDefinition definition, IReadOnlyCollection<AgentTestCase> cases,
        AgentTestTarget target = AgentTestTarget.Candidate, int repeats = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeats, 1);

        // An ephemeral copy runs the same prompt, tools, schema and checks, without creating a managed version.
        var tested = target == AgentTestTarget.Candidate ? definition with { IsManagedAgent = false } : definition;

        var results = new List<AgentTestResult>(cases.Count);
        foreach (var testCase in cases)
        {
            var runs = new List<AgentTestResult>(repeats);
            for (var run = 0; run < repeats; run++)
            {
                runs.Add(await RunCaseAsync(tested, testCase, cancellationToken).ConfigureAwait(false));
            }

            results.Add(Combine(runs));
        }

        return new AgentEvaluationReport(definition.Name, results);
    }

    /// <summary>One result per case: each metric's average across the runs, and every distinct failure from any of them.</summary>
    private static AgentTestResult Combine(List<AgentTestResult> runs)
    {
        if (runs.Count == 1)
        {
            return runs[0];
        }

        var first = runs[0];
        var failures = runs.SelectMany(static run => run.Failures).GroupBy(static failure => failure)
            .Select(group => group.Count() == runs.Count ? group.Key : $"{group.Key} (in {group.Count()} of {runs.Count} runs)")
            .ToList();
        var scores = runs.SelectMany(static run => run.Scores).GroupBy(static score => score.Key)
            .ToDictionary(static metric => metric.Key, static metric => metric.Average(static score => score.Value));

        return new AgentTestResult(first.CaseName, runs[^1].Output, failures, scores) { Group = first.Group };
    }

    private async Task<AgentTestResult> RunCaseAsync(AgentDefinition definition, AgentTestCase testCase, CancellationToken cancellationToken)
    {
        AgentResult result;
        try
        {
            result = await agents.RunAsync(definition, testCase.Prompt, testCase.Evidence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentTestResult(testCase.Name, null, [$"The run failed: {ex.GetBaseException().Message}"], NoScores) { Group = testCase.Group };
        }

        var output = result.Output ?? string.Empty;
        List<string> failures =
        [
            .. testCase.MustMention.Where(fact => !output.Contains(fact, StringComparison.OrdinalIgnoreCase)).Select(fact => $"Doesn't mention \"{fact}\""),
            .. testCase.MustNotMention.Where(fact => output.Contains(fact, StringComparison.OrdinalIgnoreCase)).Select(fact => $"Mentions \"{fact}\""),
        ];

        var scores = evaluator is null
            ? NoScores
            : await evaluator.EvaluateAsync(new CompletedAgentRun { AgentName = definition.Name, RunId = result.RunId, CompletedAt = result.CompletedAt, AgentVersion = result.AgentVersion, Model = result.Model, Prompt = testCase.Prompt, Evidence = testCase.Evidence, Output = output }, cancellationToken).ConfigureAwait(false);

        return new AgentTestResult(testCase.Name, output, failures, scores) { Group = testCase.Group };
    }
}

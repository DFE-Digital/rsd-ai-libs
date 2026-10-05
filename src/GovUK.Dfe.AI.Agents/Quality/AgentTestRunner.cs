using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Quality;

internal sealed class AgentTestRunner(IAgentService agents, IAgentRunEvaluator? evaluator = null) : IAgentTestRunner
{
    private static readonly IReadOnlyDictionary<string, double> NoScores = new Dictionary<string, double>();

    public async Task<AgentEvaluationReport> RunAsync(AgentDefinition definition, IReadOnlyCollection<AgentTestCase> cases,
        AgentTestTarget target = AgentTestTarget.Candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(cases);

        // An ephemeral copy runs the same prompt, tools, schema and checks, without creating a managed version.
        var tested = target == AgentTestTarget.Candidate ? definition with { IsManagedAgent = false } : definition;

        var results = new List<AgentTestResult>(cases.Count);
        foreach (var testCase in cases)
        {
            results.Add(await RunCaseAsync(tested, testCase, cancellationToken).ConfigureAwait(false));
        }

        return new AgentEvaluationReport(definition.Name, results);
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
            return new AgentTestResult(testCase.Name, null, [$"The run failed: {ex.GetBaseException().Message}"], NoScores);
        }

        var output = result.Output ?? string.Empty;
        List<string> failures =
        [
            .. testCase.MustMention.Where(fact => !output.Contains(fact, StringComparison.OrdinalIgnoreCase)).Select(fact => $"Doesn't mention \"{fact}\""),
            .. testCase.MustNotMention.Where(fact => output.Contains(fact, StringComparison.OrdinalIgnoreCase)).Select(fact => $"Mentions \"{fact}\""),
        ];

        var scores = evaluator is null
            ? NoScores
            : await evaluator.EvaluateAsync(new CompletedAgentRun { AgentName = definition.Name, AgentVersion = result.AgentVersion, Model = result.Model, Prompt = testCase.Prompt, Evidence = testCase.Evidence, Output = output }, cancellationToken).ConfigureAwait(false);

        return new AgentTestResult(testCase.Name, output, failures, scores);
    }
}

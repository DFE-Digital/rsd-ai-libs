using System.Globalization;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Exceptions;
using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <param name="specs">Gives the evaluator each agent's instructions, so it scores the answer against the agent's task.</param>
internal sealed class AgentTestRunner(IAgentService agents, IAgentRunEvaluator? evaluator = null, Builders.AgentSpecBuilder? specs = null)
    : IAgentTestRunner
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

    /// <summary>
    /// One result per case: each metric's average across the runs, every distinct failure from any of them, and every
    /// answer, so none goes unchecked. A metric some answered run wasn't scored on is left out, so it shows as unscored
    /// rather than as the average of the runs that were.
    /// </summary>
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
        var answered = runs.Where(static run => run.Output is not null).ToList();
        var scores = answered.SelectMany(static run => run.Scores).GroupBy(static score => score.Key)
            .Where(metric => metric.Count() == answered.Count)
            .ToDictionary(static metric => metric.Key, static metric => metric.Average(static score => score.Value));

        return new AgentTestResult(first.CaseName, runs[^1].Output, failures, scores)
        {
            Group = first.Group,
            Outputs = [.. answered.Select(static run => run.Output!)],
            BlockedByGuardrail = runs.Exists(static run => run.BlockedByGuardrail),
            BlockedByGuardrailInError = runs.Exists(static run => run.BlockedByGuardrailInError),
        };
    }

    private async Task<AgentTestResult> RunCaseAsync(AgentDefinition definition, AgentTestCase testCase, CancellationToken cancellationToken)
    {
        AgentResult result;
        try
        {
            result = await agents.RunAsync(definition, testCase.Prompt, testCase.Evidence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A guardrail block the case allows or requires is the right outcome: it passes, with nothing to score. A
            // block on any other case is a false positive, reported as the guardrail's.
            var guardrailBlock = IsGuardrailBlock(ex);
            var blocked = guardrailBlock && (testCase.GuardrailMayBlock || testCase.GuardrailMustBlock);
            var failure = guardrailBlock
                ? string.Format(CultureInfo.InvariantCulture, Constants.ErrorMessages.GuardrailBlockedInError, ex.GetBaseException().Message)
                : $"The run failed: {ex.GetBaseException().Message}";
            return new AgentTestResult(testCase.Name, null, blocked ? [] : [failure], NoScores)
            {
                Group = testCase.Group,
                BlockedByGuardrail = blocked,
                BlockedByGuardrailInError = guardrailBlock && !blocked,
            };
        }

        // The answer as the app's users see it, so the case's facts and the judge's scores are about what they read.
        string output;
        try
        {
            output = definition.Presented(result.Output ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new AgentTestResult(testCase.Name, result.Output,
                [string.Format(CultureInfo.InvariantCulture, Constants.ErrorMessages.PresentOutputFailed, ex.GetBaseException().Message)], NoScores)
            {
                Group = testCase.Group,
            };
        }

        List<string> failures =
        [
            .. testCase.GuardrailMustBlock ? [Constants.ErrorMessages.GuardrailDidNotBlock] : Array.Empty<string>(),
            .. testCase.MustMention.Where(fact => !output.Contains(fact, StringComparison.OrdinalIgnoreCase)).Select(fact => $"Doesn't mention \"{fact}\""),
            .. testCase.MustNotMention.Where(fact => output.Contains(fact, StringComparison.OrdinalIgnoreCase)).Select(fact => $"Mentions \"{fact}\""),
        ];

        if (evaluator is null)
        {
            return new AgentTestResult(testCase.Name, output, failures, NoScores) { Group = testCase.Group };
        }

        // The instructions the agent ran with, from the release's snapshot when one is open, so the judge scores against them.
        var instructions = specs is null ? null : await specs.InstructionsForAsync(definition, cancellationToken).ConfigureAwait(false);
        var scores = await evaluator.EvaluateAsync(new CompletedAgentRun { AgentName = definition.Name, RunId = result.RunId, CompletedAt = result.CompletedAt, AgentVersion = result.AgentVersion, Model = result.Model, Instructions = instructions, Prompt = testCase.Prompt, Evidence = testCase.Evidence, Output = output }, cancellationToken).ConfigureAwait(false);

        return new AgentTestResult(testCase.Name, output, failures, scores) { Group = testCase.Group };
    }

    private static bool IsGuardrailBlock(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is AgentGuardrailException)
            {
                return true;
            }
        }

        return false;
    }
}

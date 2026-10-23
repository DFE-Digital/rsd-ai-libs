using GovUK.Dfe.AI.Agents.Evaluation.Constants;
using GovUK.Dfe.AI.Agents.Evaluation.Metrics;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates.Interfaces;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>
/// Decides whether agents can be released: checks the guardrail first, then has <see cref="AgentReleaseEvaluator"/> test
/// and score them, and logs the decision with every reason. Everything it records carries the release run's id.
/// </summary>
internal sealed class AgentReleaseGate(
    AgentReleaseEvaluator evaluator,
    ReleaseGateSettings settings,
    ReleaseGateMetrics metrics,
    IEnumerable<IGuardrailCheck> guardrailChecks,
    ILogger<AgentReleaseGate> logger) : IAgentReleaseGate
{
    // The open run in this async flow, so EvaluateAsync inside a BeginRun uses it rather than opening another.
    private static readonly AsyncLocal<string?> CurrentRun = new();

    public IReleaseRun BeginRun()
    {
        var run = new ReleaseRun(Guid.CreateVersion7().ToString("N"), metrics);
        logger.ReleaseRunStarted(run.Id);
        return run;
    }

    public async Task<IReadOnlyList<AgentGateResult>> CheckReleaseAsync(IReadOnlyCollection<AgentDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        using var ownRun = CurrentRun.Value is null ? BeginRun() : null;

        // Nothing is tested without its guardrail: the results would show only how the agents behave unprotected.
        var guardrailProblems = await CheckGuardrailsAsync(cancellationToken);
        var results = guardrailProblems.Count == 0
            ? await evaluator.EvaluateAsync(definitions, followConventions: true, cancellationToken)
            : [.. definitions.Select(definition => NotEvaluated(definition.Name, guardrailProblems))];

        LogDecision(results);
        return results;
    }

    public async Task<IReadOnlyList<AgentGateResult>> EvaluateAsync(IReadOnlyCollection<AgentDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        using var ownRun = CurrentRun.Value is null ? BeginRun() : null;
        return await evaluator.EvaluateAsync(definitions, followConventions: false, cancellationToken);
    }

    /// <summary>Every guardrail check's problems; one that can't run is a problem too, so a release never skips it.</summary>
    private async Task<IReadOnlyList<string>> CheckGuardrailsAsync(CancellationToken cancellationToken)
    {
        var checks = guardrailChecks.ToList();
        if (checks.Count == 0)
        {
            return settings.RequireGuardrail ? [ReleaseGateMessages.NoGuardrailCheck] : [];
        }

        var problems = new List<string>();
        foreach (var check in checks)
        {
            logger.CheckingGuardrail(check.Name);
            IReadOnlyList<string> found;
            try
            {
                found = await check.CheckAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.GuardrailNotChecked(ex, check.Name);
                found = [ReleaseGateMessages.GuardrailNotChecked(check.Name, ex.GetBaseException().Message)];
            }

            if (found.Count == 0)
            {
                logger.GuardrailInPlace(check.Name);
            }
            else
            {
                logger.GuardrailNotInPlace(check.Name, string.Join("; ", found));
            }

            problems.AddRange(found);
        }

        return problems;
    }

    private AgentGateResult NotEvaluated(string agentName, IReadOnlyList<string> guardrailProblems)
    {
        metrics.Agent(agentName, passed: false);
        return new AgentGateResult(agentName, guardrailProblems, []) { Evaluated = false };
    }

    /// <summary>Each agent's sections, then, when any failed, one error for the release and one for each reason.</summary>
    private void LogDecision(IReadOnlyList<AgentGateResult> results)
    {
        foreach (var result in results)
        {
            var guardrail = ReleaseGateMessages.SectionResult(result.GuardrailProblems.Count);
            var evaluation = ReleaseGateMessages.SectionResult(result.EvaluationProblems.Count, result.Evaluated);
            if (result.Passed)
            {
                logger.AgentSectionsPassed(result.AgentName, guardrail, evaluation);
            }
            else
            {
                logger.AgentSectionsFailed(result.AgentName, guardrail, evaluation);
            }
        }

        var failed = results.Where(static result => !result.Passed).ToList();
        if (failed.Count == 0)
        {
            logger.ReleasePassed(results.Count);
            return;
        }

        // CheckReleaseAsync opens a run when its caller hasn't, so there's always one.
        var runId = CurrentRun.Value ?? string.Empty;
        logger.ReleaseBlocked(failed.Count, results.Count, runId);
        var reasons = failed.SelectMany(static result => result.ProblemsBySection()
            .Select(item => (result.AgentName, item.Section, Problem: item.Problem.ReplaceLineEndings(" "))));
        foreach (var (agentName, section, problem) in reasons)
        {
            logger.ReleaseBlockedBy(runId, agentName, section, problem);
        }
    }

    /// <summary>A release run: its id on every metric until disposed, and the current run for <see cref="EvaluateAsync"/>.</summary>
    private sealed class ReleaseRun : IReleaseRun
    {
        private readonly IDisposable _scope;
        private readonly string? _outer;

        public ReleaseRun(string id, ReleaseGateMetrics metrics)
        {
            Id = id;
            _outer = CurrentRun.Value;
            _scope = metrics.Run(id);
            CurrentRun.Value = id;
        }

        public string Id { get; }

        public void Dispose()
        {
            CurrentRun.Value = _outer;
            _scope.Dispose();
        }
    }
}

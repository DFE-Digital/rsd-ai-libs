using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI;

/// <summary>
/// Runs the quality checks on one agent's release gate results, and applies the policy to what they find: which checks
/// run, which results count, and at what severity. A check that throws, or gives nothing, is inconclusive, never passed.
/// </summary>
internal sealed class QualityCheckRunner(
    QualityGatePolicy policy,
    ReleaseGateSettings releaseGateSettings,
    ReleaseGateJudge judge,
    IEnumerable<IQualityCheck> checks,
    IEnumerable<IGuardrailCheck> guardrailChecks,
    GuardrailBlocklists blocklists,
    ILogger<QualityCheckRunner> logger)
{
    /// <summary>Ids the gate adds itself; the policy can't turn them off.</summary>
    private static readonly string[] GateIds = [QualityCheckIds.PrincipleCoverage, QualityCheckIds.ReleaseGateConsistency];

    private readonly IReadOnlyList<IQualityCheck> _checks = [.. checks];

    /// <summary>Why the policy can't be applied: two checks share an id, it configures one that doesn't exist, or it's more lenient than the release gate.</summary>
    public IReadOnlyList<string> PolicyProblems()
    {
        var ids = _checks.Select(static check => check.Id).ToList();
        return
        [
            .. ids.GroupBy(static id => id, StringComparer.OrdinalIgnoreCase).Where(static g => g.Count() > 1)
                .Select(static g => $"two checks share the id {g.Key}"),
            .. policy.Checks.Keys.Where(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase) || GateIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Select(static id => $"Checks:{id} isn't a check the policy can configure"),
            .. policy.ConflictsWith(releaseGateSettings),
        ];
    }

    /// <summary>The checks the team left out, turned off or for a principle out of scope, as the report lists them.</summary>
    public IReadOnlyList<string> NotRun() =>
    [
        .. _checks.Where(check => !policy.For(check.Id).Enabled || !InScope(check.Id, check.Principle))
            .Select(check => policy.For(check.Id).Enabled
                ? $"{check.Id} ({check.Principle}): principle not in scope"
                : $"{check.Id} ({check.Principle}): turned off"),
    ];

    /// <summary>Every result that counts for the agent, at the policy's severity.</summary>
    public async Task<IReadOnlyList<QualityCheckResult>> RunAsync(AgentDefinition definition, AgentGateResult gateResult,
        ReleaseManifest manifest, GovernanceRecords governance, DateOnly today, CancellationToken cancellationToken)
    {
        var context = ContextFor(definition, gateResult, manifest, governance, today);

        // Every check that's on runs, as its results may name another principle, e.g. a test case's own; only results in
        // scope count.
        var results = new List<QualityCheckResult>();
        foreach (var check in _checks.Where(check => policy.For(check.Id).Enabled))
        {
            results.AddRange(await RunAsync(check, context, cancellationToken));
        }

        List<QualityCheckResult> applied = [.. results.Where(InScope).Select(Apply)];
        applied.AddRange(Unexplained(gateResult, applied));
        return applied;
    }

    private QualityCheckContext ContextFor(AgentDefinition definition, AgentGateResult gateResult, ReleaseManifest manifest,
        GovernanceRecords governance, DateOnly today) => new()
        {
            Agent = definition,
            Policy = policy,
            ReleaseGate = releaseGateSettings,
            Manifest = manifest,
            Scenarios = EvaluationScenario.Pair(gateResult.TestCases, gateResult.Report),
            Report = gateResult.Report ?? new(definition.Name, []),
            Baseline = gateResult.Baseline,
            ReportPath = gateResult.ReportPath,
            Evaluated = gateResult.Evaluated,
            GuardrailChecks = guardrailChecks.Count(),
            GuardrailProblems = gateResult.Evaluated ? [] : gateResult.GuardrailProblems,
            ReleaseGateProblems = gateResult.Problems,
            SensitiveDataPatterns = blocklists.Patterns,
            JudgeModel = judge.Model,
            JudgeMetrics = releaseGateSettings.RequiredMetrics,
            Governance = governance,
            Today = today,
        };

    /// <summary>The check's results; when it throws or gives none, one inconclusive result, as a check never passes by failing.</summary>
    private async Task<IReadOnlyList<QualityCheckResult>> RunAsync(IQualityCheck check, QualityCheckContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await check.EvaluateAsync(context, cancellationToken);
            return results.Count > 0 ? results : [Inconclusive(check, "The check gave no result")];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.CheckFailedToRun(ex, context.Agent.Name, check.Id);
            return [Inconclusive(check, $"The check couldn't run: {ex.GetBaseException().Message}")];
        }
    }

    private static QualityCheckResult Inconclusive(IQualityCheck check, string summary) => new()
    {
        CheckId = check.Id,
        Principle = check.Principle,
        Severity = check.DefaultSeverity,
        Method = check.Method,
        Outcome = CheckOutcome.Inconclusive,
        Summary = summary,
        Recommendation = "Fix the check, or the data it reads, and run the gate again.",
    };

    /// <summary>
    /// Whether a result counts: its check is on and its principle in scope. Test cases always count, as the release gate
    /// fails a failing case anyway, and so do attestations, which the team lists itself.
    /// </summary>
    private bool InScope(QualityCheckResult result) =>
        policy.For(result.CheckId).Enabled && InScope(result.CheckId, result.Principle);

    private bool InScope(string checkId, ResponsibleAiPrinciple principle) =>
        policy.InScope(principle) || checkId is QualityCheckIds.Scenario or QualityCheckIds.Attestation;

    /// <summary>The result at the policy's severity for its check, if the policy sets one.</summary>
    private QualityCheckResult Apply(QualityCheckResult result) =>
        policy.For(result.CheckId).Severity is { } severity ? result with { Severity = severity } : result;

    /// <summary>
    /// A critical failure when the release gate failed the agent but no check found anything, e.g. a check was turned off:
    /// the quality gate can be stricter than the release gate, never more lenient.
    /// </summary>
    private static IEnumerable<QualityCheckResult> Unexplained(AgentGateResult gateResult, List<QualityCheckResult> results)
    {
        if (gateResult.Passed || results.Exists(static result => result.NotMet))
        {
            yield break;
        }

        yield return new QualityCheckResult
        {
            CheckId = QualityCheckIds.ReleaseGateConsistency,
            Principle = ResponsibleAiPrinciple.Accountability,
            Severity = CheckSeverity.Critical,
            Method = EvaluationMethod.Deterministic,
            Outcome = CheckOutcome.Failed,
            Summary = "The release gate failed the agent, but no quality check explains why",
            Evidence = gateResult.Problems,
            Recommendation = "Turn back on the check that covers these problems, or fix them.",
        };
    }
}

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

    /// <summary>How the release gate ends a group gap, e.g. "Groundedness: special 3.1, academy 4.4: a gap over 0.5".</summary>
    private const string GapSuffix = ": a gap over ";

    /// <summary>The checks that apply the release gate's own rules, so only their findings can explain its failure.</summary>
    private static readonly HashSet<string> ReleaseGateChecks = new(StringComparer.OrdinalIgnoreCase)
    {
        QualityCheckIds.Scenario, QualityCheckIds.GuardrailInPlace, QualityCheckIds.JudgeScore, QualityCheckIds.BaselineRegression,
        QualityCheckIds.GroupScoreParity, QualityCheckIds.TestCaseConventions, QualityCheckIds.EvaluationDataset,
    };

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
    /// <param name="tools">The agent's tools in this app, or why they couldn't be listed.</param>
    public async Task<IReadOnlyList<QualityCheckResult>> RunAsync(AgentDefinition definition, AgentToolList tools,
        AgentGateResult gateResult, ReleaseManifest manifest, GovernanceRecords governance, DateOnly today, CancellationToken cancellationToken)
    {
        var context = ContextFor(definition, tools, gateResult, manifest, governance, today);

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

    private QualityCheckContext ContextFor(AgentDefinition definition, AgentToolList tools, AgentGateResult gateResult,
        ReleaseManifest manifest, GovernanceRecords governance, DateOnly today) => new()
        {
            Agent = definition,
            Tools = tools.Names,
            ToolsProblem = tools.Problem,
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
    /// A critical failure listing each release gate problem that no blocking finding of its own covers, e.g. its check was
    /// turned off and only an unrelated or advisory finding remains. Each problem needs its own finding, so a risk accepted
    /// for one finding never waives another failure: the quality gate can be stricter than the release gate, never more
    /// lenient.
    /// </summary>
    private static IEnumerable<QualityCheckResult> Unexplained(AgentGateResult gateResult, List<QualityCheckResult> results)
    {
        if (gateResult.Passed)
        {
            yield break;
        }

        var blocking = results.Where(static result => result.NotMet && result.Severity >= CheckSeverity.High
            && ReleaseGateChecks.Contains(result.CheckId)).ToList();
        List<string> uncovered = [.. gateResult.Problems.Where(problem => !blocking.Exists(finding => Covers(finding, problem)))];
        if (uncovered.Count == 0)
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
            Summary = $"The release gate failed the agent for {uncovered.Count} reason(s) no blocking quality check covers",
            Evidence = uncovered,
            Recommendation = "Turn back on the check that covers these problems, or fix them.",
        };
    }

    /// <summary>
    /// Whether <paramref name="finding"/> is the one for <paramref name="problem"/>, as the release gate words it: a test
    /// case's for its case, a judge score's or baseline's for its metric, the group gap's for its gap, and otherwise one
    /// that lists the problem as evidence, e.g. the guardrail's or the test case conventions'.
    /// </summary>
    private static bool Covers(QualityCheckResult finding, string problem) => finding.CheckId switch
    {
        QualityCheckIds.Scenario => finding.Subject is { } name && problem.StartsWith($"Case {name}: ", StringComparison.Ordinal),
        QualityCheckIds.JudgeScore => finding.Subject is { } metric
            && (problem.StartsWith($"{metric}: averaged ", StringComparison.Ordinal) || problem == $"{metric}: not scored"),
        QualityCheckIds.BaselineRegression => finding.Subject is { } metric && problem.StartsWith($"{metric}: fell from ", StringComparison.Ordinal),
        QualityCheckIds.GroupScoreParity => finding.Subject is { } metric && problem.StartsWith($"{metric}: ", StringComparison.Ordinal)
            && problem.Contains(GapSuffix, StringComparison.Ordinal),
        _ => finding.Evidence.Contains(problem),
    };
}

/// <summary>An agent's tools in this app, by name, or why they couldn't be listed.</summary>
internal sealed record AgentToolList(IReadOnlyList<string>? Names, string? Problem);

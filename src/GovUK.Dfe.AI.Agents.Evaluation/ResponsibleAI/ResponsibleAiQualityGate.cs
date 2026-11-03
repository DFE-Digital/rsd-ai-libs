using GovUK.Dfe.AI.Agents.Evaluation.Exceptions;
using GovUK.Dfe.AI.Agents.Evaluation.Metrics;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Decisions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Reporting;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI;

/// <summary>
/// Decides releases on Responsible AI evidence. The release gate runs the agents' test cases once (guardrail, facts,
/// judge scores, baseline); each quality check then reads those results, the policy turns each result into a pass,
/// warning, accepted risk or blocking failure, and the decision is reported, logged and recorded.
/// </summary>
internal sealed class ResponsibleAiQualityGate(
    AgentReleaseGate releaseGate,
    ReleaseGateSettings releaseGateSettings,
    QualityGatePolicy policy,
    ReleaseGateJudge judge,
    IEnumerable<IQualityCheck> checks,
    IEnumerable<IGuardrailCheck> guardrailChecks,
    ReleaseManifestBuilder manifests,
    GuardrailBlocklists blocklists,
    QualityGateReportWriter writer,
    ReleaseGateMetrics metrics,
    TimeProvider time,
    ILogger<ResponsibleAiQualityGate> logger) : IResponsibleAiQualityGate
{
    /// <summary>Ids the gate adds itself; the policy can't turn them off.</summary>
    private static readonly string[] GateIds = [QualityCheckIds.PrincipleCoverage, QualityCheckIds.ReleaseGateConsistency];

    public async Task<QualityGateReport> DecideReleaseAsync(IReadOnlyCollection<AgentDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var all = checks.ToList();
        var problems = PolicyProblems(all);
        if (problems.Count > 0)
        {
            throw new ReleaseGateException($"The quality gate policy is invalid: {string.Join("; ", problems)}");
        }

        using var ownRun = AgentReleaseGate.CurrentRunId is null ? releaseGate.BeginRun() : null;
        var runId = AgentReleaseGate.CurrentRunId ?? string.Empty;
        var governance = await GovernanceRecords.LoadAsync(Path.Combine(AppContext.BaseDirectory, policy.GovernanceFile), cancellationToken);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        // The team chooses its checks and principles, and the report lists what it left out. Every check that's on runs, as
        // its results may name another principle, e.g. a test case's own; only results in scope count (InScope).
        var enabled = all.Where(check => policy.For(check.Id).Enabled).ToList();
        var disabled = all.Where(check => !policy.For(check.Id).Enabled || !InScope(check.Id, check.Principle)).ToList();
        disabled.ForEach(check => logger.CheckDisabled(check.Id, check.Principle.ToString()));

        var gateResults = await releaseGate.RunChecksAsync(definitions, cancellationToken);
        var manifest = manifests.Build(runId, definitions, gateResults);

        var decisions = new List<AgentReleaseDecision>(definitions.Count);
        foreach (var definition in definitions)
        {
            var gateResult = gateResults.First(result => result.AgentName == definition.Name);
            var context = ContextFor(definition, gateResult, manifest, governance, today);

            var results = new List<QualityCheckResult>();
            foreach (var check in enabled)
            {
                results.AddRange(await RunAsync(check, context, cancellationToken));
            }

            List<QualityCheckResult> applied = [.. results.Where(InScope).Select(Apply)];
            applied.AddRange(Unexplained(gateResult, applied));

            var decision = QualityGateDecider.Decide(definition.Name, applied, policy, governance, today, checkCoverage: gateResult.Evaluated);
            Record(runId, decision);
            decisions.Add(decision);
        }

        var (overall, permitted) = QualityGateDecider.Combine(decisions);
        var report = await writer.WriteAsync(new QualityGateReport
        {
            Decision = overall,
            ReleasePermitted = permitted,
            Reasons = [.. decisions.Select(static d => $"{d.AgentName}: {d.Decision}, release {(d.ReleasePermitted ? "permitted" : "blocked")}. {d.Reasons[0]}")],
            Manifest = manifest,
            Agents = decisions,
            DisabledChecks = [.. disabled.Select(check => policy.For(check.Id).Enabled
                ? $"{check.Id} ({check.Principle}): principle not in scope"
                : $"{check.Id} ({check.Principle}): turned off")],
        }, releaseGateSettings.ReportsDirectory, policy.ReportName, cancellationToken);

        if (permitted)
        {
            logger.ReleasePermitted(overall.ToString(), runId, report.MarkdownPath);
        }
        else
        {
            logger.ReleaseBlocked(overall.ToString(), runId, report.MarkdownPath);
        }

        return report;
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

    private List<string> PolicyProblems(List<IQualityCheck> all)
    {
        var ids = all.Select(static check => check.Id).ToList();
        return
        [
            .. ids.GroupBy(static id => id, StringComparer.OrdinalIgnoreCase).Where(static g => g.Count() > 1)
                .Select(static g => $"two checks share the id {g.Key}"),
            .. policy.Checks.Keys.Where(id => !ids.Contains(id, StringComparer.OrdinalIgnoreCase) || GateIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Select(static id => $"Checks:{id} isn't a check the policy can configure"),
            .. policy.ConflictsWith(releaseGateSettings),
        ];
    }

    /// <summary>Logs the agent's decision and each finding that isn't a pass, and counts them all.</summary>
    private void Record(string runId, AgentReleaseDecision decision)
    {
        var agent = decision.AgentName;
        logger.AgentDecided(agent, decision.Decision.ToString(), decision.ReleasePermitted, decision.With(FindingDisposition.Blocking).Count(),
            decision.With(FindingDisposition.Warning).Count(), decision.With(FindingDisposition.RiskAccepted).Count());
        metrics.QualityGateDecision(agent, decision.Decision.ToString(), decision.ReleasePermitted);
        decision.IgnoredRiskAcceptances.ToList().ForEach(problem => logger.RiskAcceptanceIgnored(agent, problem));

        foreach (var finding in decision.Findings)
        {
            var result = finding.Result;
            var (principle, severity) = (result.Principle.ToString(), result.Severity.ToString());
            var summary = finding.Line();
            metrics.QualityGateFinding(agent, principle, result.CheckId, severity, finding.Disposition.ToString());
            switch (finding.Disposition)
            {
                case FindingDisposition.Blocking:
                    logger.FindingBlocking(runId, agent, principle, result.CheckId, severity, summary);
                    break;
                case FindingDisposition.Warning:
                    logger.FindingWarning(runId, agent, principle, result.CheckId, severity, summary);
                    break;
                case FindingDisposition.RiskAccepted when finding.Acceptance is { } accepted:
                    logger.FindingRiskAccepted(runId, agent, principle, result.CheckId, summary, accepted.AcceptedBy, accepted.Role,
                        accepted.Reference, accepted.ExpiresOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                    break;
            }
        }
    }
}

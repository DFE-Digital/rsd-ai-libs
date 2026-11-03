using GovUK.Dfe.AI.Agents.Evaluation.Exceptions;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Decisions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Reporting;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI;

/// <summary>
/// Decides releases on Responsible AI evidence. The release gate runs the agents' test cases once (guardrail, facts,
/// judge scores, baseline); each quality check then reads those results (<see cref="QualityCheckRunner"/>), the policy
/// turns each result into a pass, warning, accepted risk or blocking failure (<see cref="QualityGateDecider"/>), and the
/// decision is reported, logged and recorded (<see cref="QualityGateRecorder"/>).
/// </summary>
internal sealed class ResponsibleAiQualityGate(
    AgentReleaseGate releaseGate,
    QualityGatePolicy policy,
    QualityCheckRunner checks,
    ReleaseManifestBuilder manifests,
    QualityGateRecorder recorder,
    TimeProvider time) : IResponsibleAiQualityGate
{
    public async Task<QualityGateReport> DecideReleaseAsync(IReadOnlyCollection<AgentDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var problems = checks.PolicyProblems();
        if (problems.Count > 0)
        {
            throw new ReleaseGateException($"The quality gate policy is invalid: {string.Join("; ", problems)}");
        }

        using var ownRun = AgentReleaseGate.CurrentRunId is null ? releaseGate.BeginRun() : null;
        var runId = AgentReleaseGate.CurrentRunId ?? string.Empty;
        var governance = await GovernanceRecords.LoadAsync(Path.Combine(AppContext.BaseDirectory, policy.GovernanceFile), cancellationToken);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        // The team chooses its checks and principles; the report lists what it left out, so leaving one out is never silent.
        var notRun = checks.NotRun();
        recorder.NotRun(notRun);

        var gateResults = await releaseGate.RunChecksAsync(definitions, cancellationToken);
        var manifest = manifests.Build(runId, definitions, gateResults);

        var decisions = new List<AgentReleaseDecision>(definitions.Count);
        foreach (var definition in definitions)
        {
            var gateResult = gateResults.First(result => result.AgentName == definition.Name);
            var results = await checks.RunAsync(definition, gateResult, manifest, governance, today, cancellationToken);

            var decision = QualityGateDecider.Decide(definition.Name, results, policy, governance, today, checkCoverage: gateResult.Evaluated);
            recorder.Record(runId, decision);
            decisions.Add(decision);
        }

        var (overall, permitted) = QualityGateDecider.Combine(decisions);
        return await recorder.WriteAsync(new QualityGateReport
        {
            Decision = overall,
            ReleasePermitted = permitted,
            Reasons = [.. decisions.Select(static d => $"{d.AgentName}: {d.Decision}, release {(d.ReleasePermitted ? "permitted" : "blocked")}. {d.Reasons[0]}")],
            Manifest = manifest,
            Agents = decisions,
            DisabledChecks = notRun,
        }, runId, cancellationToken);
    }
}

using GovUK.Dfe.AI.Agents.Evaluation.Exceptions;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Decisions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Reporting;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Builders.Interfaces;
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
    IAgentSpecCatalog specs,
    TimeProvider time) : IResponsibleAiQualityGate
{
    private const string ExternallyManaged =
        "the agent is externally managed, so its tools are its provisioned version's: run the gate where it's provisioned";

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

        // Each agent's spec as a run builds it: its tools for the checks, and its full instructions for the manifest.
        var agentSpecs = new Dictionary<string, (AgentSpec? Spec, string? Problem)>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            agentSpecs[definition.Name] = await SpecOfAsync(definition, cancellationToken);
        }

        var manifest = manifests.Build(runId, definitions, gateResults,
            agentSpecs.ToDictionary(static agent => agent.Key, static agent => agent.Value.Spec?.Instructions, StringComparer.Ordinal));

        var decisions = new List<AgentReleaseDecision>(definitions.Count);
        foreach (var definition in definitions)
        {
            var gateResult = gateResults.First(result => result.AgentName == definition.Name);
            var results = await checks.RunAsync(definition, ToolsOf(agentSpecs[definition.Name]), gateResult, manifest, governance,
                today, cancellationToken);

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

    /// <summary>The agent's spec as a run builds it, or why it couldn't be built, e.g. a tool server was unreachable.</summary>
    private async Task<(AgentSpec? Spec, string? Problem)> SpecOfAsync(AgentDefinition definition, CancellationToken cancellationToken)
    {
        try
        {
            return (await specs.GetSpecAsync(definition, cancellationToken), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.GetBaseException().Message);
        }
    }

    /// <summary>Every tool in the agent's spec, its own, bound and built-in ones; or why they couldn't be listed, so none is missed.</summary>
    private static AgentToolList ToolsOf((AgentSpec? Spec, string? Problem) agent) => agent switch
    {
        { Problem: { } problem } => new(null, problem),
        { Spec: { } spec } => new([.. spec.Tools.Select(static tool => tool.Name)], null),
        _ => new(null, ExternallyManaged),
    };
}

using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Reporting;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Interfaces;

/// <summary>
/// The Responsible AI quality gate, set up under <c>AiAgents:Evaluation:QualityGate</c>: decides whether agents can be
/// released, from evidence filed under the six Responsible AI principles.
/// </summary>
public interface IResponsibleAiQualityGate
{
    /// <summary>
    /// Runs the release gate's standard check (guardrail, then every test case, scored by the judge), then every quality
    /// check on the results, and decides each agent's release by the policy and governance file. Writes
    /// <c>quality-gate-report.json</c> and <c>.md</c> to the release gate's <c>ReportsDirectory</c>, and logs every
    /// blocking finding as an error.
    /// </summary>
    /// <returns>The decision. Release only when <see cref="QualityGateReport.ReleasePermitted"/>; exit with <see cref="QualityGateReport.ExitCode"/>.</returns>
    /// <remarks>Outside an <c>IAgentReleaseGate.BeginRun</c>, it opens a run of its own.</remarks>
    /// <exception cref="Exceptions.ReleaseGateException">The policy or governance file is invalid, so no decision can be made.</exception>
    Task<QualityGateReport> DecideReleaseAsync(IReadOnlyCollection<AgentDefinition> definitions, CancellationToken cancellationToken = default);
}

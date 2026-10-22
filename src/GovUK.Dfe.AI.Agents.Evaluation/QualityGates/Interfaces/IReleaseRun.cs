namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates.Interfaces;

/// <summary>
/// One release: from <c>IAgentReleaseGate.BeginRun</c> until disposed, every metric recorded with <c>IAgentMetrics</c>
/// carries its <see cref="Id"/> as <see cref="ReleaseGateTelemetry.RunIdTag"/>: the guardrail check, the test cases,
/// scores and outcomes, and the versions provisioned. So one query finds one run's results together.
/// </summary>
public interface IReleaseRun : IDisposable
{
    /// <summary>A time-ordered id, so runs sort by when they started.</summary>
    string Id { get; }
}

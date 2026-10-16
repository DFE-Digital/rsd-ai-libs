using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Extensibility.Interfaces;

/// <summary>
/// Told about each successful <c>IAgentService</c> run, e.g. to score a sample of live answers (the .Evaluation package does).
/// Register it in the service collection; every registered observer is told.
/// </summary>
/// <remarks>
/// Called on the run's own thread before its result is returned, so return quickly: queue any slow work. An exception is
/// logged and never fails the run.
/// </remarks>
public interface IAgentRunObserver
{
    /// <summary>A run finished with an answer.</summary>
    void OnRunCompleted(CompletedAgentRun run);
}

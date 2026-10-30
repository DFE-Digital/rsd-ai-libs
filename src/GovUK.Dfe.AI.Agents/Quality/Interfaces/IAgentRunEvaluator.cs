using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Quality.Interfaces;

/// <summary>Scores an answer, e.g. its groundedness. The .Evaluation package has one; or write your own.</summary>
public interface IAgentRunEvaluator
{
    /// <returns>Scores by metric name; higher is better.</returns>
    Task<IReadOnlyDictionary<string, double>> EvaluateAsync(CompletedAgentRun run, CancellationToken cancellationToken = default);
}

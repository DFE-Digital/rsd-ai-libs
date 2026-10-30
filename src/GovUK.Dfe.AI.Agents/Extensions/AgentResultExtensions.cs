using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Extensions;

/// <summary>Helpers for agent results.</summary>
public static class AgentResultExtensions
{
    /// <summary>
    /// Reads a structured answer (from an agent with an <see cref="AgentDefinition.OutputSchema"/>) as
    /// <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The agent failed, or its answer isn't valid JSON for <typeparamref name="T"/>.</exception>
    public static T ReadOutputAs<T>(this AgentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(result.Output ?? string.Empty, AgentOutputSchema.SerializerOptions)
                ?? throw new InvalidOperationException(string.Format(Constants.ErrorMessages.StructuredOutputUnreadable, result.AgentName, typeof(T).Name));
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Usually a failed agent, whose output is the fallback message rather than JSON.
            throw new InvalidOperationException(string.Format(Constants.ErrorMessages.StructuredOutputUnreadable, result.AgentName, typeof(T).Name), ex);
        }
    }

    /// <summary>
    /// Adds up the tokens used across <paramref name="results"/> - e.g. every specialist result plus the
    /// synthesis result of a briefing - in total and per agent. Includes tokens billed before an agent failed.
    /// </summary>
    public static TokenUsageSummary ToTokenUsageSummary(this IEnumerable<AgentResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var byAgent = new Dictionary<string, TokenUsage>(StringComparer.Ordinal);
        var total = TokenUsage.None;
        decimal? cost = null;
        foreach (var result in results)
        {
            byAgent[result.AgentName] = byAgent.GetValueOrDefault(result.AgentName, TokenUsage.None) + result.Usage;
            total += result.Usage;
            if (result.Cost is { } runCost)
            {
                cost = (cost ?? 0) + runCost;
            }
        }

        return new TokenUsageSummary(total, byAgent) { Cost = cost };
    }
}
namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>One run's answer and token usage.</summary>
public sealed record AgentResult
{
    /// <summary>The agent that ran.</summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Identifies this run: tagged on its <c>invoke_agent</c> span and given to run observers, so an answer can be traced,
    /// audited, and tied to users' feedback in your app. Null for a fallback result.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>When the answer was produced. Show it with the answer, alongside that it was AI-generated.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The answer; null when the run produced none.</summary>
    public string? Output { get; init; }

    /// <summary>Input plus output tokens.</summary>
    public long TotalTokens { get; init; }

    /// <summary>The input (prompt) tokens used, when Foundry reported them.</summary>
    public long InputTokens { get; init; }

    /// <summary>The output (completion) tokens used, when Foundry reported them.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Input tokens served from Foundry's prompt cache: part of <see cref="InputTokens"/>, and cheaper.</summary>
    public long CachedInputTokens { get; init; }

    /// <summary>
    /// What this run cost, from <c>AiAgents:Pricing</c>, in its currency. Null when the model isn't priced. Includes the
    /// tokens of a failed run, as Foundry still bills them.
    /// </summary>
    public decimal? Cost { get; init; }

    /// <summary>This run's token usage.</summary>
    public TokenUsage Usage => new(InputTokens, OutputTokens, TotalTokens) { CachedInputTokens = CachedInputTokens };

    /// <summary>The agent version that ran; null for an ephemeral agent.</summary>
    public string? AgentVersion { get; init; }

    /// <summary>The model that answered, as Foundry reported it (e.g. "gpt-4o-2024-08-06").</summary>
    public string? Model { get; init; }
}

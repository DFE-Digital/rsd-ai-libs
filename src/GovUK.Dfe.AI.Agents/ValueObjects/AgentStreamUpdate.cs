namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>
/// One update from <c>IAgentService.RunStreamingAsync</c>: the next piece of the answer as it's written, then, last of
/// all, the finished result.
/// </summary>
public sealed record AgentStreamUpdate
{
    /// <summary>The next piece of the answer, already cleaned like <see cref="AgentResult.Output"/>; null on the last update.</summary>
    public string? Text { get; init; }

    /// <summary>The finished, checked result, with tokens and cost; set on the last update only.</summary>
    public AgentResult? Result { get; init; }
}

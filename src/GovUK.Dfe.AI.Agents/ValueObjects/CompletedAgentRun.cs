namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>A finished run: what the agent was asked and given, and what it answered. Passed to evaluators and run observers.</summary>
public sealed record CompletedAgentRun
{
    /// <summary>The agent that ran.</summary>
    public required string AgentName { get; init; }

    /// <summary>The agent version that ran; null for an ephemeral agent.</summary>
    public string? AgentVersion { get; init; }

    /// <summary>The model that answered.</summary>
    public string? Model { get; init; }

    /// <summary>The prompt the agent was given.</summary>
    public required string Prompt { get; init; }

    /// <summary>The evidence the agent was given, if any.</summary>
    public string? Evidence { get; init; }

    /// <summary>The agent's answer.</summary>
    public required string Output { get; init; }
}

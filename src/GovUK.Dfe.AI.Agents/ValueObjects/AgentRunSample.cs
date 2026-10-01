namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>One finished run, for scoring.</summary>
/// <param name="AgentVersion">Null for an ephemeral agent.</param>
public sealed record AgentRunSample(string AgentName, string? AgentVersion, string? Model, string Prompt, string? Evidence, string Output);

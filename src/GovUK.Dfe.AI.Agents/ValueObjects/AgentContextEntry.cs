namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>One agent's input and output in an orchestration.</summary>
public sealed record AgentContextEntry(string AgentName, string Input, string? Output);

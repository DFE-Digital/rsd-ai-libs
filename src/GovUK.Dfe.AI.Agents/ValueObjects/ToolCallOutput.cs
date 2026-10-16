namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>The output of a <see cref="ToolCallRequest"/>.</summary>
/// <param name="Output">Usually JSON.</param>
public sealed record ToolCallOutput(string CallId, string Output);

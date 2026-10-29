namespace GovUK.Dfe.AI.Agents.Safety.Interfaces;

/// <summary>
/// Checks each tool's output before the model reads it, and blocks the run like a guardrail block
/// (<c>AgentGuardrailException</c>, stage <c>tool_output</c>). A Foundry guardrail scans tool responses only for Foundry's
/// own tools, never the output of tools this app runs, e.g. MCP servers. If it throws, the run fails, so output is never
/// sent unchecked. Register one in DI, e.g. with <c>AiAgents:Guardrails:ScanToolOutput</c>.
/// </summary>
public interface IToolOutputShield
{
    /// <summary>Why the output is blocked, or null to send it.</summary>
    /// <param name="toolName">The function the model called.</param>
    /// <param name="output">Its output, already cut to size and redacted.</param>
    ValueTask<InputBlock?> CheckAsync(string toolName, string output, CancellationToken cancellationToken);
}

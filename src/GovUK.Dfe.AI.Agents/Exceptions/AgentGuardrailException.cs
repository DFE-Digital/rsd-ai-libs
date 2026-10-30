namespace GovUK.Dfe.AI.Agents.Exceptions;

/// <summary>
/// A guardrail blocked an agent's prompt (<see cref="PromptStage"/>), answer (<see cref="AnswerStage"/>), evidence
/// (<see cref="EvidenceStage"/>) or a tool's output (<see cref="ToolOutputStage"/>): a Foundry guardrail, or this app's
/// own input or tool output shield, which gives a <see cref="Reason"/>. The run fails with this as its inner exception,
/// and <c>dfe.ai_agents.guardrail.blocks</c> counts it.
/// </summary>
public sealed class AgentGuardrailException : Exception
{
    public const string PromptStage = "prompt";
    public const string AnswerStage = "answer";
    public const string EvidenceStage = "evidence";
    public const string ToolOutputStage = "tool_output";

    /// <summary>A Foundry guardrail's block.</summary>
    public AgentGuardrailException(string agentName, string stage, Exception? innerException = null)
        : base(string.Format(Constants.ErrorMessages.GuardrailBlocked, Diagnostics.AgentTelemetry.AgentNameForTelemetry(agentName), stage),
            innerException)
    {
        AgentName = agentName;
        Stage = stage;
    }

    /// <summary>The input shield's block, with what found it.</summary>
    public AgentGuardrailException(string agentName, string stage, string reason)
        : base(string.Format(Constants.ErrorMessages.InputBlocked, reason, Diagnostics.AgentTelemetry.AgentNameForTelemetry(agentName)))
    {
        AgentName = agentName;
        Stage = stage;
        Reason = reason;
    }

    public string AgentName { get; }

    /// <summary><see cref="PromptStage"/>, <see cref="AnswerStage"/>, <see cref="EvidenceStage"/> or <see cref="ToolOutputStage"/>.</summary>
    public string Stage { get; }

    /// <summary>What found it, when the input shield blocked it rather than a Foundry guardrail.</summary>
    public string? Reason { get; }
}

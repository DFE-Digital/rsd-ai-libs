namespace GovUK.Dfe.AI.Agents.Exceptions;

/// <summary>
/// A Foundry guardrail blocked an agent's prompt (<see cref="PromptStage"/>) or answer (<see cref="AnswerStage"/>), or
/// the evidence shield found instructions planted in its evidence (<see cref="EvidenceStage"/>). The run fails with this
/// as its inner exception, and <c>dfe.ai_agents.guardrail.blocks</c> counts it.
/// </summary>
public sealed class AgentGuardrailException : Exception
{
    public const string PromptStage = "prompt";
    public const string AnswerStage = "answer";
    public const string EvidenceStage = "evidence";

    public AgentGuardrailException(string agentName, string stage, Exception? innerException = null)
        : base(string.Format(Constants.ErrorMessages.GuardrailBlocked, agentName, stage), innerException)
    {
        AgentName = agentName;
        Stage = stage;
    }

    public string AgentName { get; }

    /// <summary><see cref="PromptStage"/>, <see cref="AnswerStage"/> or <see cref="EvidenceStage"/>.</summary>
    public string Stage { get; }
}

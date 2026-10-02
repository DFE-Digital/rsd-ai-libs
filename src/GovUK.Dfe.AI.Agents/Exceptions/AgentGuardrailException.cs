namespace GovUK.Dfe.AI.Agents.Exceptions;

/// <summary>
/// A Foundry guardrail blocked an agent's prompt (<see cref="PromptStage"/>) or answer (<see cref="AnswerStage"/>).
/// The run fails with this as its inner exception, and <c>dfe.ai_agents.guardrail.blocks</c> counts it.
/// </summary>
public sealed class AgentGuardrailException : Exception
{
    public const string PromptStage = "prompt";
    public const string AnswerStage = "answer";

    public AgentGuardrailException(string agentName, string stage, Exception? innerException = null)
        : base(string.Format(Constants.ErrorMessages.GuardrailBlocked, agentName, stage), innerException)
    {
        AgentName = agentName;
        Stage = stage;
    }

    public string AgentName { get; }

    /// <summary><see cref="PromptStage"/> or <see cref="AnswerStage"/>.</summary>
    public string Stage { get; }
}

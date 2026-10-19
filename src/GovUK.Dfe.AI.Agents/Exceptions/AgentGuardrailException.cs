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
        : base(MessageFor(agentName, stage), innerException)
    {
        AgentName = agentName;
        Stage = stage;
    }

    public string AgentName { get; }

    /// <summary><see cref="PromptStage"/>, <see cref="AnswerStage"/> or <see cref="EvidenceStage"/>.</summary>
    public string Stage { get; }

    /// <summary>
    /// Names what blocked it: the evidence stage is the library's own Prompt Shields scan, not a Foundry guardrail. A test
    /// copy is named after its agent, without its per-run suffix.
    /// </summary>
    private static string MessageFor(string agentName, string stage)
    {
        var agent = Diagnostics.AgentTelemetry.AgentNameForTelemetry(agentName);
        return stage == EvidenceStage
            ? string.Format(Constants.ErrorMessages.EvidenceBlocked, agent)
            : string.Format(Constants.ErrorMessages.GuardrailBlocked, agent, stage);
    }
}

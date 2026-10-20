namespace GovUK.Dfe.AI.Agents.Safety.Interfaces;

/// <summary>
/// Checks a run's question and evidence before anything is sent to the model, and blocks the run like a guardrail block
/// (<c>AgentGuardrailException</c>). A Foundry guardrail doesn't cover this for agents on a model behind your own
/// gateway (an API Management connection), nor for evidence in any agent's input. If it throws, the run fails, so input is
/// never sent unchecked. Register one in DI, e.g. with <c>AiAgents:Guardrails:ScanPrompts</c>, <c>ScanEvidence</c> or
/// <c>CheckBlocklists</c>.
/// </summary>
public interface IInputShield
{
    /// <summary>Why the input is blocked, or null to send it.</summary>
    /// <param name="prompt">The question, already redacted.</param>
    /// <param name="evidence">The evidence, already redacted and cut to size; null when there is none.</param>
    ValueTask<InputBlock?> CheckAsync(string prompt, string? evidence, CancellationToken cancellationToken);
}

/// <summary>Why a run's input was blocked.</summary>
/// <param name="Stage">What was blocked: <c>AgentGuardrailException.PromptStage</c> or <c>EvidenceStage</c>.</param>
/// <param name="Reason">What found it, e.g. "Prompt Shields found a prompt attack in the question".</param>
public sealed record InputBlock(string Stage, string Reason);

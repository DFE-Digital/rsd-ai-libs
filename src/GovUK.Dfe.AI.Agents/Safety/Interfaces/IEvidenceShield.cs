namespace GovUK.Dfe.AI.Agents.Safety.Interfaces;

/// <summary>
/// Scans a run's evidence for instructions planted in it (indirect prompt injection) before it's sent to the model. A
/// Foundry guardrail can't do this for agents: it scans documents only in Chat Completions requests and in the output of
/// Foundry's own tools, not evidence in an agent's input. When it finds an attack, the run is blocked like a guardrail
/// block (<c>AgentGuardrailException</c>, stage "evidence"). If it throws, the run fails, so evidence is never sent
/// unscanned. Register one in DI, e.g. with <c>AiAgents:Guardrails:ScanEvidence</c>.
/// </summary>
public interface IEvidenceShield
{
    /// <summary>Whether <paramref name="evidence"/>, already redacted and cut to size, carries an attack.</summary>
    /// <param name="prompt">The user's prompt, for context.</param>
    ValueTask<bool> FindsAttackAsync(string prompt, string evidence, CancellationToken cancellationToken);
}

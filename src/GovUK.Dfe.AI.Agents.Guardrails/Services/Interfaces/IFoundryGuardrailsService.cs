using GovUK.Dfe.AI.Agents.Guardrails.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Guardrails.Services.Interfaces;

/// <summary>Applies and checks the Foundry guardrail under <c>AiAgents:Guardrails</c>.</summary>
public interface IFoundryGuardrailsService
{
    /// <summary>
    /// For a provisioning job: creates or updates the guardrail, gives it to every deployment in <c>Deployments</c>, then
    /// checks the result. Needs Cognitive Services Contributor on the Foundry resource.
    /// </summary>
    /// <exception cref="InvalidOperationException">The identity lacks the role.</exception>
    Task<GuardrailReport> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>Read-only: the guardrail exists, blocks at least what's configured, and every deployment carries it. Needs Reader.</summary>
    /// <exception cref="InvalidOperationException">The identity lacks the role.</exception>
    Task<GuardrailReport> CheckAsync(CancellationToken cancellationToken = default);
}

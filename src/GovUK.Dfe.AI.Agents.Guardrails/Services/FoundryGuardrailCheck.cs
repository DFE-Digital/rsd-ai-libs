using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Options;
using GovUK.Dfe.AI.Agents.Guardrails.Services.Interfaces;

namespace GovUK.Dfe.AI.Agents.Guardrails.Services;

/// <summary>The Foundry guardrail's check, run by the release gate before any agent is tested.</summary>
internal sealed class FoundryGuardrailCheck(IFoundryGuardrailsService guardrails, GuardrailSettings settings) : IGuardrailCheck
{
    public string Name => $"Foundry guardrail {settings.Name}";

    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken) =>
        (await guardrails.CheckAsync(cancellationToken).ConfigureAwait(false)).Problems;
}

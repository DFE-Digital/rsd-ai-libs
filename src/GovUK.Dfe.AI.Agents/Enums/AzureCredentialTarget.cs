using GovUK.Dfe.AI.Agents.Builders;

namespace GovUK.Dfe.AI.Agents.Enums;

/// <summary>The Azure services the library signs in to, for <see cref="AgentsBuilder.UseCredentialFor"/>.</summary>
public enum AzureCredentialTarget
{
    Foundry,
    Search,
    /// <summary>The <c>GlobalConcurrency</c> run-slot blob container.</summary>
    RunSlots,
    /// <summary>Azure Resource Manager, where the .Guardrails package reads and applies Foundry guardrails.</summary>
    Guardrails,
}

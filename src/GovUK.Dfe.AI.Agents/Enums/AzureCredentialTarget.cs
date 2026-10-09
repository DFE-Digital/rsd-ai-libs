using GovUK.Dfe.AI.Agents.Builders;

namespace GovUK.Dfe.AI.Agents.Enums;

/// <summary>The Azure services the library signs in to, for <see cref="AgentsBuilder.UseCredentialFor(AzureCredentialTarget, Azure.Core.TokenCredential)"/>.</summary>
public enum AzureCredentialTarget
{
    Foundry,
    /// <summary>The <c>GlobalConcurrency</c> run-slot blob container.</summary>
    RunSlots,
}

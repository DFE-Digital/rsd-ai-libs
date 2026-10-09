using GovUK.Dfe.AI.Agents.Guardrails.Policies;

namespace GovUK.Dfe.AI.Agents.Guardrails.Stores.Interfaces;

/// <summary>The Foundry resource's guardrails and deployments, as Azure Resource Manager holds them.</summary>
internal interface IGuardrailStore
{
    /// <summary>The guardrail, or null if it doesn't exist.</summary>
    Task<GuardrailPolicy?> GetGuardrailAsync(string name, CancellationToken cancellationToken);

    /// <summary>Creates or replaces the guardrail.</summary>
    Task SaveGuardrailAsync(GuardrailPolicy guardrail, CancellationToken cancellationToken);

    /// <summary>Whether the deployment exists, and the guardrail it carries.</summary>
    Task<(bool Exists, string? Guardrail)> GetDeploymentAsync(string deployment, CancellationToken cancellationToken);

    /// <summary>Gives the deployment the guardrail.</summary>
    Task AssignAsync(string deployment, string guardrail, CancellationToken cancellationToken);

    /// <summary>The blocklist's entries, or null if it doesn't exist.</summary>
    Task<IReadOnlySet<GuardrailBlocklistEntry>?> GetBlocklistAsync(string name, CancellationToken cancellationToken);

    /// <summary>Creates the blocklist if needed, and makes its entries exactly <paramref name="entries"/>.</summary>
    Task SaveBlocklistAsync(string name, IReadOnlySet<GuardrailBlocklistEntry> entries, CancellationToken cancellationToken);
}

using GovUK.Dfe.AI.Agents.Options;

namespace GovUK.Dfe.AI.Agents.Extensibility.Interfaces;

/// <summary>
/// A guardrail that must be in place before a release's agents are tested, e.g. the Foundry guardrail, which the
/// Guardrails package registers. The release gate runs every one first: any problem fails every agent in the
/// <see cref="ReleaseSections.Guardrail"/> section, and nothing is tested without its guardrail.
/// </summary>
public interface IGuardrailCheck
{
    /// <summary>What's checked, for the logs, e.g. "Foundry guardrail briefing-guardrail".</summary>
    string Name { get; }

    /// <summary>What's wrong, one readable line each. Empty: it's in place.</summary>
    Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken);
}

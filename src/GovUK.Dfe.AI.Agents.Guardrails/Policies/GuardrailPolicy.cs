using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Options;

namespace GovUK.Dfe.AI.Agents.Guardrails.Policies;

/// <summary>
/// What a Foundry guardrail blocks. <see cref="BlockFrom"/> is null when a harm filter doesn't block; <see cref="Blocklists"/>
/// names the blocklists it applies to prompts and answers.
/// </summary>
internal sealed record GuardrailPolicy(string Name, GuardrailSeverity? BlockFrom, bool PromptShields, bool IndirectAttacks,
    bool ProtectedMaterial, IReadOnlyList<string> Blocklists)
{
    public static GuardrailPolicy From(AgentsOptions.GuardrailSettings settings)
        => new(settings.Name!, settings.BlockFrom, settings.PromptShields, settings.IndirectAttacks, settings.ProtectedMaterial,
            [.. settings.Blocklists.Keys.Order(StringComparer.Ordinal)]);

    /// <summary>A blocklist's entries as configured: its terms as written, then its patterns.</summary>
    public static IReadOnlySet<GuardrailBlocklistEntry> EntriesOf(AgentsOptions.GuardrailBlocklistSettings blocklist)
        => blocklist.Terms.Select(static term => new GuardrailBlocklistEntry(term, IsRegex: false))
            .Concat(blocklist.Patterns.Select(static pattern => new GuardrailBlocklistEntry(pattern, IsRegex: true)))
            .ToHashSet();

    /// <summary>How this guardrail is weaker than <paramref name="required"/>; empty when it blocks at least as much.</summary>
    public IEnumerable<string> WeakerThan(GuardrailPolicy required)
    {
        // Low blocks the most, so a higher threshold is weaker.
        if (BlockFrom is null || BlockFrom > required.BlockFrom)
        {
            yield return $"harm filters block from {BlockFrom?.ToString() ?? "nothing"}, not {required.BlockFrom}";
        }

        if (required.PromptShields && !PromptShields)
        {
            yield return "Prompt Shields (jailbreak) is off";
        }

        if (required.IndirectAttacks && !IndirectAttacks)
        {
            yield return "indirect attack detection is off";
        }

        if (required.ProtectedMaterial && !ProtectedMaterial)
        {
            yield return "protected material detection is off";
        }

        foreach (var blocklist in required.Blocklists.Except(Blocklists, StringComparer.Ordinal))
        {
            yield return $"blocklist '{blocklist}' isn't applied";
        }
    }
}

/// <summary>One blocklist entry: a term matched as written, or a regular expression.</summary>
internal sealed record GuardrailBlocklistEntry(string Pattern, bool IsRegex);

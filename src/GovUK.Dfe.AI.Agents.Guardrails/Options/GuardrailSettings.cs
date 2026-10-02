using GovUK.Dfe.AI.Agents.Guardrails.Enums;
using GovUK.Dfe.AI.Agents.Options;
using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Guardrails.Options;

/// <summary>
/// The <c>AiAgents:Guardrails</c> section: the Foundry guardrail (content filters, Prompt Shields,
/// blocklists) every model deployment agents use must carry. A provisioning job applies it; every app checks it at startup.
/// </summary>
public sealed partial class GuardrailSettings
{
    /// <summary>
    /// The Foundry resource that holds the deployments, e.g.
    /// /subscriptions/&lt;id&gt;/resourceGroups/&lt;group&gt;/providers/Microsoft.CognitiveServices/accounts/&lt;name&gt;.
    /// </summary>
    public string? AccountResourceId { get; set; }

    /// <summary>The guardrail's name in Foundry, e.g. "briefing-guardrail".</summary>
    public string? Name { get; set; }

    /// <summary>The model deployments agents use, e.g. "gpt-5.1". Each must carry the guardrail.</summary>
    public List<string> Deployments { get; set; } = [];

    /// <summary>The lowest harm severity blocked, for hate, sexual, violence and self-harm, in prompts and answers.</summary>
    public GuardrailSeverity BlockFrom { get; set; } = GuardrailSeverity.Medium;

    /// <summary>Blocks jailbreak attempts in prompts.</summary>
    public bool PromptShields { get; set; } = true;

    /// <summary>Blocks instructions hidden in documents and tool output (indirect attacks).</summary>
    public bool IndirectAttacks { get; set; } = true;

    /// <summary>Blocks answers that reproduce protected text or code.</summary>
    public bool ProtectedMaterial { get; set; } = true;

    /// <summary>Your own blocklists, keyed by name: terms and patterns blocked in prompts and answers.</summary>
    public Dictionary<string, GuardrailBlocklistSettings> Blocklists { get; set; } = [];

    /// <summary>At startup, fails when a deployment lacks the guardrail. Off: a warning only.</summary>
    public bool RequireAtStartup { get; set; } = true;

    /// <summary>Optional: a service principal for Azure Resource Manager only. Unset: the default.</summary>
    public AgentsOptions.ServicePrincipalSettings? Authentication { get; set; }

    internal IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(AccountResourceId)
            || !AccountResourceId.Contains("/providers/Microsoft.CognitiveServices/accounts/", StringComparison.OrdinalIgnoreCase))
        {
            yield return "AccountResourceId (the Foundry resource's ID: .../providers/Microsoft.CognitiveServices/accounts/<name>)";
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return "Name";
        }

        if (Deployments.Count == 0 || Deployments.Exists(string.IsNullOrWhiteSpace))
        {
            yield return "Deployments";
        }

        foreach (var (name, blocklist) in Blocklists)
        {
            if (!BlockRegex().IsMatch(name))
            {
                yield return $"Blocklists:{name} (a name of letters, digits, '-' and '_')";
            }
            else if (blocklist.Problem() is { } problem)
            {
                yield return $"Blocklists:{name} ({problem})";
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex BlockRegex();
}

/// <summary>One Foundry blocklist: exact terms and regular expressions, matched in prompts and answers.</summary>
public sealed class GuardrailBlocklistSettings
{
    /// <summary>Words or phrases blocked as written, e.g. a project code name.</summary>
    public List<string> Terms { get; set; } = [];

    /// <summary>Regular expressions blocked, e.g. <c>CASE-\d{6}</c> for case references.</summary>
    public List<string> Patterns { get; set; } = [];

    internal string? Problem()
    {
        if (Terms.Count + Patterns.Count == 0)
        {
            return "needs at least one term or pattern";
        }

        if (Terms.Concat(Patterns).Any(string.IsNullOrWhiteSpace))
        {
            return "has an empty term or pattern";
        }

        foreach (var pattern in Patterns)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                return "has a pattern that isn't a valid regular expression";
            }
        }

        return null;
    }
}

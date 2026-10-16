using GovUK.Dfe.AI.Agents.Guardrails.Enums;
using GovUK.Dfe.AI.Agents.Options;
using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Guardrails.Options;

/// <summary>
/// The <c>AiAgents:Guardrails</c> section: the Foundry guardrail (content filters, Prompt Shields, blocklists) every
/// model deployment, or every agent version, must carry. A provisioning job applies it; every app checks it at startup.
/// </summary>
public sealed partial class GuardrailSettings
{
    /// <summary>The Azure subscription (a GUID) that holds the Foundry resource.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>The resource group that holds the Foundry resource.</summary>
    public string? ResourceGroup { get; set; }

    /// <summary>
    /// Optional: the Foundry resource's name. Unset: the <c>&lt;resource&gt;</c> in <c>Foundry:Endpoint</c>
    /// (https://&lt;resource&gt;.services.ai.azure.com/...). Set it only when the deployments are on a different resource.
    /// </summary>
    public string? AccountName { get; set; }

    /// <summary>The Foundry resource's Azure Resource Manager ID, worked out when the package is registered.</summary>
    internal string? ResourceId { get; private set; }

    /// <summary>
    /// The guardrail's full Resource Manager ID. The Agent Service needs it on agent versions: a bare name is rejected.
    /// </summary>
    internal string? PolicyResourceId => ResourceId is null ? null : $"{ResourceId}/raiPolicies/{Name}";

    /// <summary>Whether two guardrail references, each a name or a full Resource Manager ID, name the same guardrail.</summary>
    internal static bool SameGuardrail(string? first, string? second)
        => first is not null && second is not null
            && string.Equals(PolicyName(first), PolicyName(second), StringComparison.OrdinalIgnoreCase);

    private static string PolicyName(string reference) => reference[(reference.LastIndexOf('/') + 1)..];

    /// <summary>Works out <see cref="ResourceId"/> from the subscription, resource group and account name.</summary>
    internal void ResolveResourceId(string? foundryEndpoint)
        => ResourceId = AccountNameFor(foundryEndpoint) is { } account && !string.IsNullOrWhiteSpace(ResourceGroup)
            ? $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/Microsoft.CognitiveServices/accounts/{account}"
            : null;

    /// <summary><see cref="AccountName"/>, else the first part of the Foundry endpoint's host, e.g. "contoso" in contoso.services.ai.azure.com.</summary>
    private string? AccountNameFor(string? foundryEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(AccountName))
        {
            return AccountName;
        }

        return Uri.TryCreate(foundryEndpoint, UriKind.Absolute, out var endpoint)
               && endpoint.Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase)
            ? endpoint.Host.Split('.')[0]
            : null;
    }

    /// <summary>The guardrail's name in Foundry, e.g. "briefing-guardrail".</summary>
    public string? Name { get; set; }

    /// <summary>Where the guardrail goes: <c>Deployments</c> (default), <c>Agents</c>, or both.</summary>
    public GuardrailTargets Targets { get; set; } = GuardrailTargets.Deployments;

    /// <summary>The model deployments agents use, e.g. "gpt-5.1". Each must carry the guardrail. Needed when <see cref="Targets"/> include <c>Deployments</c>.</summary>
    public List<string> Deployments { get; set; } = [];

    /// <summary>The agent versions this app runs as they are, which must carry the guardrail; worked out when the package is registered.</summary>
    internal IReadOnlyList<AgentInUse> Agents { get; set; } = [];

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

    /// <summary>
    /// Fails startup when the guardrail can't be checked, e.g. Azure Resource Manager is unreachable, so the app never runs
    /// without its guardrail confirmed. Off (default): a warning, so an Azure outage doesn't stop new instances starting.
    /// </summary>
    public bool FailWhenUnreachable { get; set; }

    /// <summary>Optional: a service principal for Azure Resource Manager only. Unset: the default.</summary>
    public AgentsOptions.ServicePrincipalSettings? Authentication { get; set; }

    internal IEnumerable<string> Problems(string? foundryEndpoint)
    {
        if (!Guid.TryParse(SubscriptionId, out _))
        {
            yield return "SubscriptionId (the subscription's ID, a GUID)";
        }

        if (string.IsNullOrWhiteSpace(ResourceGroup))
        {
            yield return "ResourceGroup";
        }

        if (AccountNameFor(foundryEndpoint) is null)
        {
            yield return "AccountName (Foundry:Endpoint doesn't name the resource, so set it here)";
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return "Name";
        }

        if (Targets == 0 || (Targets & ~(GuardrailTargets.Deployments | GuardrailTargets.Agents)) != 0)
        {
            yield return "Targets (Deployments, Agents, or both)";
        }

        if (Deployments.Exists(string.IsNullOrWhiteSpace) || (Targets.HasFlag(GuardrailTargets.Deployments) && Deployments.Count == 0))
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

/// <summary>An agent version this app runs without creating it, and the guardrail it must carry.</summary>
/// <param name="Version">Null: the latest.</param>
internal sealed record AgentInUse(string Name, string? Version, string Guardrail);

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

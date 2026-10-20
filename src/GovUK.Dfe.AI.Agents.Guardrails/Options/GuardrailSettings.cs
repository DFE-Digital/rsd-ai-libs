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

    /// <summary>
    /// Scans each run's evidence with Azure AI Content Safety Prompt Shields before it's sent, and blocks the run when it
    /// finds planted instructions. A Foundry guardrail can't: it doesn't scan evidence in an agent's input. Needs the
    /// <b>Cognitive Services User</b> role on the Foundry resource. Off by default.
    /// </summary>
    public bool ScanEvidence { get; set; }

    /// <summary>
    /// Scans each run's question with Prompt Shields before it's sent, and blocks a jailbreak attempt. Turn it on when
    /// agents use a model behind your own gateway (an API Management connection): Foundry's guardrail doesn't run there.
    /// Needs <b>Cognitive Services User</b>, like <see cref="ScanEvidence"/>. Off by default.
    /// </summary>
    public bool ScanPrompts { get; set; }

    /// <summary>
    /// Checks each run's question and evidence against <see cref="Blocklists"/> in the app, with no call to Azure, and
    /// blocks a match. Turn it on when agents use a model behind your own gateway, where Foundry's blocklists don't run.
    /// Off by default.
    /// </summary>
    public bool CheckBlocklists { get; set; }

    /// <summary>Whether the app checks its own input, so it needs an <c>IInputShield</c>.</summary>
    internal bool ChecksInput => ScanPrompts || ScanEvidence || CheckBlocklists;

    /// <summary>Whether input is sent to Prompt Shields, so it needs a Content Safety endpoint.</summary>
    internal bool UsesPromptShields => ScanPrompts || ScanEvidence;

    /// <summary>
    /// Optional: the Content Safety endpoint <see cref="ScanPrompts"/> and <see cref="ScanEvidence"/> call. Unset: the
    /// Foundry resource's own, https://&lt;resource&gt;.cognitiveservices.azure.com/.
    /// </summary>
    public string? ContentSafetyEndpoint { get; set; }

    /// <summary><see cref="ContentSafetyEndpoint"/>, else the Foundry resource's Cognitive Services endpoint.</summary>
    internal Uri? ContentSafetyEndpointFor(string? foundryEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(ContentSafetyEndpoint))
        {
            return Uri.TryCreate(ContentSafetyEndpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps
                ? endpoint
                : null;
        }

        return AccountNameFor(foundryEndpoint) is { } account ? new Uri($"https://{account}.cognitiveservices.azure.com/") : null;
    }

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
        const GuardrailTargets AllTargets = GuardrailTargets.Deployments | GuardrailTargets.Agents;
        (bool Invalid, string Problem)[] checks =
        [
            (!Guid.TryParse(SubscriptionId, out _), "SubscriptionId (the subscription's ID, a GUID)"),
            (string.IsNullOrWhiteSpace(ResourceGroup), "ResourceGroup"),
            (AccountNameFor(foundryEndpoint) is null, "AccountName (Foundry:Endpoint doesn't name the resource, so set it here)"),
            (string.IsNullOrWhiteSpace(Name), "Name"),
            (UsesPromptShields && ContentSafetyEndpointFor(foundryEndpoint) is null,
                "ContentSafetyEndpoint (an https URL, needed for ScanPrompts and ScanEvidence)"),
            (CheckBlocklists && Blocklists.Count == 0, "Blocklists (CheckBlocklists needs at least one)"),
            (Targets == 0 || (Targets & ~AllTargets) != 0, "Targets (Deployments, Agents, or both)"),
            (Deployments.Exists(string.IsNullOrWhiteSpace) || (Targets.HasFlag(GuardrailTargets.Deployments) && Deployments.Count == 0),
                "Deployments"),
        ];

        return checks.Where(static check => check.Invalid).Select(static check => check.Problem).Concat(BlocklistProblems());
    }

    private IEnumerable<string> BlocklistProblems()
        => Blocklists.Select(static entry => BlocklistProblem(entry.Key, entry.Value) is { } problem ? $"Blocklists:{entry.Key} ({problem})" : null)
            .OfType<string>();

    private static string? BlocklistProblem(string name, GuardrailBlocklistSettings blocklist)
        => BlockRegex().IsMatch(name) ? blocklist.Problem() : "a name of letters, digits, '-' and '_'";

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

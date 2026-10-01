using GovUK.Dfe.AI.Agents.Enums;
using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Options;

public sealed partial class AgentsOptions
{
    /// <summary>The <c>Foundry</c> section: the project agents run in.</summary>
    public sealed class FoundrySettings
    {
        /// <summary>The Foundry project endpoint, e.g. https://&lt;resource&gt;.services.ai.azure.com/api/projects/&lt;project&gt;.</summary>
        public string? Endpoint { get; set; }

        /// <summary>The model agents use unless their spec says otherwise, e.g. "my-connection/gpt-5.1".</summary>
        public string? DefaultModel { get; set; }

        /// <summary>Optional: a service principal for Foundry only. Unset: the default.</summary>
        public ServicePrincipalSettings? Authentication { get; set; }
    }

    /// <summary>A Microsoft Entra ID service principal: an <c>Authentication</c> block.</summary>
    public sealed class ServicePrincipalSettings
    {
        public string? TenantId { get; set; }

        public string? ClientId { get; set; }

        /// <summary>Keep this out of appsettings: supply it from Key Vault or an environment variable.</summary>
        public string? ClientSecret { get; set; }

        /// <summary>Defaults to the Azure public cloud.</summary>
        public Uri? AuthorityHost { get; set; }

        /// <summary>Redacts <see cref="ClientSecret"/> so it never appears in logs.</summary>
        public override string ToString() => $"{{ TenantId = {TenantId}, ClientId = {ClientId}, ClientSecret = [REDACTED] }}";
    }

    /// <summary>One <c>McpServers</c> entry, for the .Mcp package.</summary>
    public sealed class McpServerSettings
    {
        /// <summary>The server's MCP endpoint, e.g. https://mcp.internal.example/mcp.</summary>
        public string? ServerUri { get; set; }

        /// <summary>The token scope, e.g. "api://school-performance/.default".</summary>
        public string? Scope { get; set; }

        /// <summary>Optional: a service principal for this server only, possibly in another tenant. Unset: the default.</summary>
        public ServicePrincipalSettings? Authentication { get; set; }

        /// <summary>Required: every tool this app may use from the server.</summary>
        public List<string> AllowedToolNames { get; set; } = [];

        /// <summary>How long the server's tool list is reused before asking again.</summary>
        public TimeSpan ToolListCacheDuration { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>The <c>Search</c> section's sign-in; the .AISearch package reads the rest.</summary>
    public sealed class SearchSettings
    {
        /// <summary>Optional: a service principal for Azure AI Search only. Unset: the default.</summary>
        public ServicePrincipalSettings? Authentication { get; set; }
    }

    /// <summary>
    /// Caps agent runs across every instance (and every app pointed at the same container), so scaling out can't exceed the
    /// Foundry quota. Each run holds a blob lease; a crashed instance's lease expires within a minute.
    /// </summary>
    public sealed class GlobalConcurrencySettings
    {
        /// <summary>The most agent runs at once across all instances. Unset (default): no global limit.</summary>
        public int? MaxConcurrentRuns { get; set; }

        /// <summary>
        /// A blob container used only for run slots, e.g. https://&lt;account&gt;.blob.core.windows.net/aiagents-run-slots.
        /// HTTPS, with no SAS token: access is by Entra ID only. Created, private, if it's missing.
        /// </summary>
        public string? BlobContainerUri { get; set; }

        /// <summary>Optional: a service principal for the run-slot container only. Unset: the default.</summary>
        public ServicePrincipalSettings? Authentication { get; set; }

        internal bool IsEnabled => MaxConcurrentRuns is not null;

        internal IEnumerable<string> Problems()
        {
            if (!IsEnabled)
            {
                return string.IsNullOrWhiteSpace(BlobContainerUri) ? [] : ["MaxConcurrentRuns"];
            }

            var problems = new List<string>();
            if (MaxConcurrentRuns < 1)
            {
                problems.Add("MaxConcurrentRuns (must be at least 1)");
            }

            if (!Uri.TryCreate(BlobContainerUri, UriKind.Absolute, out var uri))
            {
                problems.Add("BlobContainerUri");
            }
            else if (uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length > 0)
            {
                problems.Add("BlobContainerUri (must be https, with no SAS token)");
            }

            return problems;
        }
    }

    /// <summary>
    /// <c>{ "Endpoint": ..., "Authentication": { ... }, "ofsted-agent": "4" }</c>: every key except <c>Endpoint</c> and
    /// <c>Authentication</c> is an agent, with the version to run (or <c>"latest"</c>).
    /// </summary>
    public sealed class ExternallyManagedAgentsSettings
    {
        private static readonly string[] Reserved = [nameof(Endpoint), nameof(Authentication)];

        /// <summary>Optional: the agents' Foundry project. Unset: this app's own project.</summary>
        public string? Endpoint { get; set; }

        /// <summary>Optional: a service principal for <see cref="Endpoint"/>'s project. Unset: this app's Foundry credential.</summary>
        public ServicePrincipalSettings? Authentication { get; set; }

        /// <summary>The agents, with the version to run.</summary>
        public IReadOnlyDictionary<string, string> Agents { get; internal set; } = new Dictionary<string, string>();

        internal bool InOtherProject => Endpoint is not null;

        /// <summary>Reads the agents: the keys binding can't map, as they're names chosen by the app.</summary>
        internal void ReadAgents(Microsoft.Extensions.Configuration.IConfigurationSection section)
            => Agents = section.GetChildren()
                .Where(static child => !Reserved.Contains(child.Key, StringComparer.OrdinalIgnoreCase) && child.Value is not null)
                .ToDictionary(static child => child.Key, static child => child.Value!);
    }

    /// <summary>
    /// The <c>Guardrails</c> section, for the .Guardrails package: the Foundry guardrail (content filters, Prompt Shields,
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
        public ServicePrincipalSettings? Authentication { get; set; }

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
}

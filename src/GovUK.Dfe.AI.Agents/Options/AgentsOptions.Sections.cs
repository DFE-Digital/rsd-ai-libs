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
}

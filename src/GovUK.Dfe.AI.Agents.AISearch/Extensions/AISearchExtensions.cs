using GovUK.Dfe.AI.Agents.AISearch.Constants;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using GovUK.Dfe.AI.Agents.AISearch.Context;
using GovUK.Dfe.AI.Agents.AISearch.Filters.Interfaces;
using GovUK.Dfe.AI.Agents.AISearch.Filters;
using GovUK.Dfe.AI.Agents.AISearch.Options;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Context.Interfaces;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Extensibility;
using GovUK.Dfe.AI.Agents.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds Azure AI Search: to <c>AddAgents</c> for agents' evidence, or on its own for an app that only searches.</summary>
public static class AISearchExtensions
{
    private const string SectionName = "Search";

    /// <summary>Registers <c>IContextRetriever</c> over the indexes under <c>AiAgents:Search</c>, for agents' evidence.</summary>
    public static AgentsBuilder AddAISearch(this AgentsBuilder agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.AddPackage(new AISearchPackage());
    }

    /// <summary>
    /// Registers <c>IContextRetriever</c> on its own, without <c>AddAgents</c> or Foundry, from the same
    /// <c>AiAgents:Search</c> section. Signs in with <paramref name="credential"/>, else the section's <c>Authentication</c> block.
    /// Calling this and <c>agents.AddAISearch()</c> registers search once.
    /// </summary>
    /// <param name="credential">E.g. <c>new ManagedIdentityCredential()</c>. Null: the <c>AiAgents:Search:Authentication</c> block.</param>
    /// <exception cref="InvalidOperationException">The section is missing, or there's no credential and no complete block.</exception>
    public static IServiceCollection AddAISearch(this IServiceCollection services, IConfiguration configuration, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection($"{AgentsOptions.SectionName}:{SectionName}");
        if (!section.Exists())
        {
            throw new InvalidOperationException(ErrorMessages.SearchSectionMissing);
        }

        return services.AddAzureSearchContextRetriever(section, credential ?? CredentialFrom(section));
    }

    /// <summary>A credential for Azure AI Search. Overrides <c>Search:Authentication</c> and the default.</summary>
    public static AgentsBuilder UseAISearchCredential(this AgentsBuilder agents, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.UseCredentialFor(SectionName, credential);
    }

    internal static IServiceCollection AddAzureSearchContextRetriever(this IServiceCollection services, IConfiguration section,
        TokenCredential credential)
    {
        // Registered once, whichever of AddAISearch's two forms runs first.
        if (services.Any(static service => service.ServiceType == typeof(AzureSearchContextRetrieverOptions)))
        {
            return services;
        }

        services.AddOptions<AzureSearchContextRetrieverOptions>().Bind(section)
            .Validate(options => options.IndexesAreValid, ErrorMessages.AzureSearchIndexesInvalid)
            .Validate(options => options.MaxEvidenceCharacters is null or > 0, ErrorMessages.AzureSearchMaxEvidenceInvalid)
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AzureSearchContextRetrieverOptions>>().Value);
        services.AddSingleton<IRelevanceFilter>(sp =>
            new RelativeScoreRelevanceFilter(sp.GetRequiredService<AzureSearchContextRetrieverOptions>().MinimumRelevanceFilter));

        services.AddSingleton<IContextRetriever>(sp =>
        {
            var config = sp.GetRequiredService<AzureSearchContextRetrieverOptions>();
            var clientOptions = new SearchClientOptions { Retry = { MaxRetries = config.MaxRetryAttempts, Mode = RetryMode.Exponential } };
            var clients = config.Indexes.ToDictionary(index => index.Name,
                index => new SearchClient(new Uri(config.Endpoint), index.Name, credential, clientOptions));

            return new AzureSearchContextRetriever(clients, sp.GetRequiredService<IRelevanceFilter>(),
                sp.GetRequiredService<ILogger<AzureSearchContextRetriever>>(),
                config.Indexes.ToDictionary(index => index.Name, index => index.ContentFields),
                config.Indexes.ToDictionary(index => index.Name), config.MaxEvidenceCharacters);
        });

        return services;
    }

    /// <summary>A service principal from the section's <c>Authentication</c> block, which must be complete.</summary>
    private static ClientSecretCredential CredentialFrom(IConfigurationSection section)
    {
        var principal = section.GetSection("Authentication").Get<AgentsOptions.ServicePrincipalSettings>();
        var missing = principal is null
            ? ["Authentication"]
            : new[] { ("TenantId", principal.TenantId), ("ClientId", principal.ClientId), ("ClientSecret", principal.ClientSecret) }
                .Where(static setting => string.IsNullOrWhiteSpace(setting.Item2)).Select(static setting => $"Authentication:{setting.Item1}").ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(string.Format(ErrorMessages.SearchCredentialMissing, string.Join(", ", missing)));
        }

        return new ClientSecretCredential(principal!.TenantId, principal.ClientId, principal.ClientSecret,
            new ClientSecretCredentialOptions { AuthorityHost = principal.AuthorityHost ?? AzureAuthorityHosts.AzurePublicCloud });
    }

    private sealed class AISearchPackage : IAgentsPackage
    {
        public string Name => "AISearch";

        public void Register(AgentsPackageContext context)
        {
            var section = context.Section.GetSection(SectionName);
            if (!section.Exists())
            {
                context.ReportProblem($"{SectionName} (agents.AddAISearch() needs this section)");
                return;
            }

            var authentication = section.GetSection("Authentication").Get<AgentsOptions.ServicePrincipalSettings>();
            context.Services.AddAzureSearchContextRetriever(section,
                context.CredentialFor(SectionName, authentication, $"{SectionName}:Authentication"));
        }
    }
}

using Azure.Core;
using Azure.Search.Documents;
using GovUK.Dfe.AI.Agents.AISearch.Context;
using GovUK.Dfe.AI.Agents.AISearch.Filters.Interfaces;
using GovUK.Dfe.AI.Agents.AISearch.Filters;
using GovUK.Dfe.AI.Agents.AISearch.Options;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.Context.Interfaces;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Packages.Interfaces;
using GovUK.Dfe.AI.Agents.Packages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds Azure AI Search to <c>AddAgents</c>.</summary>
public static class AISearchExtensions
{
    /// <summary>Registers <c>IContextRetriever</c> over the indexes under <c>AiAgents:Search</c>, for agents' evidence.</summary>
    public static AgentsBuilder AddAISearch(this AgentsBuilder agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.AddPackage(new AISearchPackage());
    }

    internal static IServiceCollection AddAzureSearchContextRetriever(this IServiceCollection services, IConfiguration section,
        TokenCredential credential)
    {
        services.AddOptions<AzureSearchContextRetrieverOptions>().Bind(section)
            .Validate(options => options.IndexesAreValid, ErrorMessages.AzureSearchIndexesInvalid).ValidateOnStart();
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
                config.Indexes.ToDictionary(index => index.Name));
        });

        return services;
    }

    private sealed class AISearchPackage : IAgentsPackage
    {
        public string Name => "AISearch";

        public void Register(AgentsRegistrationPackage registrationPackage)
        {
            var options = registrationPackage.Options;
            registrationPackage.Services.AddAzureSearchContextRetriever(registrationPackage.Section.GetSection("Search"),
                options.CredentialFor(nameof(AzureCredentialTarget.Search), options.Search!.Authentication));
        }
    }
}

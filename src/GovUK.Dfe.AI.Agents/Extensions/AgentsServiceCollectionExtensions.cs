using System.ClientModel.Primitives;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Storage.Blobs;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Clients;
using GovUK.Dfe.AI.Agents.Concurrency.Interfaces;
using GovUK.Dfe.AI.Agents.Concurrency;
using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.Factories;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Orchestration.Interfaces;
using GovUK.Dfe.AI.Agents.Orchestration;
using GovUK.Dfe.AI.Agents.Extensibility;
using GovUK.Dfe.AI.Agents.Prompts.Interfaces;
using GovUK.Dfe.AI.Agents.Prompts;
using GovUK.Dfe.AI.Agents.Providers.Interfaces;
using GovUK.Dfe.AI.Agents.Providers;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Services;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Validators;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the library: call <c>AddAgents</c> once at startup.</summary>
public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything from the <c>"AiAgents"</c> section: Foundry, prompts, versions, limits and telemetry, plus
    /// whatever add-on packages <paramref name="configure"/> adds (MCP servers, AI Search, evaluation, guardrails).
    /// </summary>
    /// <param name="configure">Adds the app's agents and packages, e.g. <c>agents => agents.AddAgents(...).AddMcpServers()</c>.</param>
    /// <exception cref="InvalidOperationException">A setting is missing or invalid; the message lists them all.</exception>
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration,
        Action<AgentsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddLogging();   // every library service logs; a no-op if the app already added logging
        var builder = new AgentsBuilder(services);
        configure?.Invoke(builder);

        var section = configuration.GetSection(AgentsOptions.SectionName);
        var options = section.Get<AgentsOptions>() ?? new AgentsOptions();
        options.ExternallyManagedAgents.ReadAgents(section.GetSection(nameof(AgentsOptions.ExternallyManagedAgents)));
        builder.ApplyTo(options);

        // Each package reads and checks its own settings, so startup fails once with every problem.
        var context = new AgentsPackageContext(services, section, options, builder.Definitions);
        foreach (var package in builder.Packages)
        {
            package.Register(context);
        }

        var missing = options.MissingSettings().Concat(context.Problems).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(string.Format(ErrorMessages.AiAgentsSettingsMissing, string.Join(", ", missing)));
        }

        var foundryCredential = options.CredentialFor(nameof(AzureCredentialTarget.Foundry), options.Foundry.Authentication);

        services.AddSingleton(options.ToRunOptions());
        services.AddSingleton(options.ToVersionPinning());
        services.AddFoundryAgents(_ => new Uri(options.Foundry.Endpoint!), _ => foundryCredential,
            _ => new FoundryAgentFactoryOptions(options.Foundry.DefaultModel!)
            {
                KeepLatestVersions = options.KeepLatestVersions,
                AgentCacheDuration = options.AgentCacheDuration,
            }, options.MaxRetries);
        services.AddFilePrompts(section, options.ResponseFormatKey, options.ResponseFormatExemptPromptTypes);

        if (options.EnableDriftDetection)
        {
            services.AddHostedService<PinnedAgentVersionDriftValidator>();
        }

        if (options.GlobalConcurrency.MaxConcurrentRuns is int maxConcurrentRuns)
        {
            var slotContainer = new BlobContainerClient(new Uri(options.GlobalConcurrency.BlobContainerUri!),
                options.CredentialFor(nameof(AzureCredentialTarget.RunSlots), options.GlobalConcurrency.Authentication));
            services.AddSingleton(sp => new BlobRunSlotStore(slotContainer, maxConcurrentRuns, sp.GetService<ILogger<BlobRunSlotStore>>()));
            services.AddSingleton<IRunSlotStore>(sp => sp.GetRequiredService<BlobRunSlotStore>());
            services.AddHostedService<RunSlotStartupValidator>();
        }

        RegisterAgents(services, builder.Definitions, options, foundryCredential);
        return services;
    }

    /// <summary>Registers the builder's definitions and marks the configured agents as externally managed.</summary>
    private static void RegisterAgents(IServiceCollection services, IReadOnlyList<AgentDefinition> definitions, AgentsOptions options,
        TokenCredential foundryCredential)
    {
        if (definitions.Count > 0)
        {
            services.AddSingleton<IAgentDefinitionProvider>(new StaticAgentDefinitionProvider(definitions));
        }

        RegisterExternallyManagedAgents(services, options, foundryCredential);
    }

    /// <summary>
    /// Agents another pipeline provisions. In this app's project they're resolved at their pinned version; in another
    /// project (<c>Endpoint</c> set) they're resolved and run there, with its own credential or this app's Foundry one.
    /// </summary>
    private static void RegisterExternallyManagedAgents(IServiceCollection services, AgentsOptions options, TokenCredential foundryCredential)
    {
        var external = options.ExternallyManagedAgents;
        if (!external.InOtherProject)
        {
            foreach (var agentName in external.Agents.Keys)
            {
                services.AddSingleton<IManagedAgentProvider>(sp => new ExternallyManagedAgentProvider(agentName,
                    sp.GetRequiredService<IAgentFactory>(), sp.GetRequiredService<IAgentRuntimeService>()));
            }

            return;
        }

        var credential = options.ExternallyManagedCredentialFor(foundryCredential);
        services.AddSingleton(sp =>
        {
            var client = new AIProjectClient(new Uri(external.Endpoint!), credential,
                new AIProjectClientOptions { RetryPolicy = new ClientRetryPolicy(options.MaxRetries) });
            return new ExternalFoundryProjectService(credential, client.AgentAdministrationClient, new FoundryConversationClient(client.ProjectOpenAIClient),
                new FoundryAgentFactoryOptions(options.Foundry.DefaultModel!) { AgentCacheDuration = options.AgentCacheDuration },
                sp.GetRequiredService<AgentRunOptions>(), sp.GetRequiredService<IAgentRunLimiter>(), sp.GetRequiredService<ILoggerFactory>());
        });

        foreach (var (agentName, version) in external.Agents)
        {
            services.AddSingleton<IManagedAgentProvider>(sp => new ExternalProjectAgentProvider(agentName,
                AgentsOptions.FollowsLatest(version) ? null : version, sp.GetRequiredService<ExternalFoundryProjectService>));
        }
    }

    private sealed class StaticAgentDefinitionProvider(IReadOnlyCollection<AgentDefinition> definitions) : IAgentDefinitionProvider
    {
        public IReadOnlyCollection<AgentDefinition> GetAgentsDefinitions() => definitions;
    }

    // ===== Building blocks for AddAgents; internal so apps have one way to register. =====

    /// <summary>The Foundry services, over a registered <see cref="AIProjectClient"/>.</summary>
    internal static IServiceCollection AddFoundryAgents(this IServiceCollection services,
        Func<IServiceProvider, FoundryAgentFactoryOptions> optionsFactory)
    {
        services.AddSingleton(optionsFactory);
        services.TryAddSingleton(new AgentRunOptions());

        services.AddHostedService<TokenUsageTelemetryValidator>();
        services.AddHostedService<AgentToolCompatibilityValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<AIProjectClient>().AgentAdministrationClient);
        services.AddSingleton(sp => sp.GetRequiredService<AIProjectClient>().ProjectOpenAIClient);
        services.AddSingleton<IAgentFactory, FoundryAgentFactory>();
        services.AddSingleton<IFoundryConversationClient, FoundryConversationClient>();
        services.AddSingleton<IAgentRunLimiter>(sp => new AgentRunLimiter(sp.GetRequiredService<AgentRunOptions>(), sp.GetService<IRunSlotStore>()));
        services.AddSingleton<IAgentRunnerService, FoundryAgentRunnerService>();
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<IAgentRuntimeService, AgentRuntimeService>();
        services.AddSingleton(sp => new AgentSpecBuilder(sp.GetRequiredService<IPromptProvider>(),
            sp.GetServices<AgentToolBinding>(), sp.GetServices<IManagedAgentProvider>()));
        services.AddSingleton<IAgentService, AgentService>();
        services.AddSingleton<IAgentTestRunner>(sp => new AgentTestRunner(sp.GetRequiredService<IAgentService>(),
            sp.GetService<IAgentRunEvaluator>()));

        return services;
    }

    internal static IServiceCollection AddFoundryAgents(this IServiceCollection services, Func<IServiceProvider, Uri> endpoint,
        Func<IServiceProvider, TokenCredential> credential, Func<IServiceProvider, FoundryAgentFactoryOptions> optionsFactory,
        int maxRetries = 3)
    {
        services.AddSingleton(sp => new AIProjectClient(endpoint(sp), credential(sp),
            new AIProjectClientOptions { RetryPolicy = new ClientRetryPolicy(maxRetries) }));

        return services.AddFoundryAgents(optionsFactory);
    }

    /// <summary>Prompt files from <paramref name="configuration"/>'s <c>PromptFiles</c> section.</summary>
    internal static IServiceCollection AddFilePrompts(this IServiceCollection services, IConfiguration configuration,
        string? responseFormatKey = null, IReadOnlySet<string>? responseFormatExemptPromptTypes = null)
    {
        services.AddOptions<PromptFileOptions>().Bind(configuration.GetSection("PromptFiles")).ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PromptFileOptions>>().Value);
        services.AddSingleton<IPromptFileReader, FileSystemPromptFileReader>();
        services.AddSingleton<IPromptTemplateStore>(sp => new FilePromptTemplateStore(
            sp.GetRequiredService<PromptFileOptions>().UserPrompts, sp.GetRequiredService<IPromptFileReader>().Read));
        services.AddSingleton<IPromptTemplateBuilder, PromptTemplateBuilder>();
        services.AddSingleton<IPromptProvider>(sp =>
        {
            var systemPrompts = new FilePromptTemplateStore(sp.GetRequiredService<PromptFileOptions>().SystemPrompts,
                sp.GetRequiredService<IPromptFileReader>().Read);
            return new FilePromptProvider(systemPrompts, sp.GetRequiredService<IPromptTemplateStore>(), responseFormatKey,
                responseFormatExemptPromptTypes);
        });

        return services;
    }
}

using Azure.AI.Projects.Agents;
using Azure.Core;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Concurrency.Interfaces;
using GovUK.Dfe.AI.Agents.Factories;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using Microsoft.Extensions.Logging;
using GovUK.Dfe.AI.Agents.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Safety.Interfaces;

namespace GovUK.Dfe.AI.Agents.Services;

/// <summary>
/// The <c>ExternallyManagedAgents</c> project, when <c>Endpoint</c> is set. Agents run in the project that owns them, so it
/// has its own factory and runner, sharing this app's run limits and input checks.
/// </summary>
internal sealed class ExternalFoundryProjectService
{
    public ExternalFoundryProjectService(TokenCredential credential, AgentAdministrationClient admin,
        IFoundryResponsesClient responses, FoundryAgentFactoryOptions factoryOptions, SharedRunServices shared, ILoggerFactory loggers)
    {
        Credential = credential;
        Factory = new FoundryAgentFactory(admin, factoryOptions, loggers.CreateLogger<FoundryAgentFactory>());
        Runner = new FoundryAgentRunnerService(Factory, responses, loggers.CreateLogger<FoundryAgentRunnerService>(), shared.RunOptions,
            shared.RunLimiter, shared.InputShield, shared.ToolOutputShield);
    }

    /// <summary>What runs in the other project share with this app's own runs.</summary>
    /// <param name="InputShield">This app's input check (e.g. Prompt Shields), so runs in the other project are checked too.</param>
    /// <param name="ToolOutputShield">This app's check of its tools' output, likewise.</param>
    internal sealed record SharedRunServices(AgentRunOptions RunOptions, IAgentRunLimiter RunLimiter, IInputShield? InputShield,
        IToolOutputShield? ToolOutputShield = null);

    /// <summary>Its own <c>Authentication</c>, or this app's Foundry credential.</summary>
    public TokenCredential Credential { get; }

    public IAgentFactory Factory { get; }

    public IAgentRunnerService Runner { get; }
}

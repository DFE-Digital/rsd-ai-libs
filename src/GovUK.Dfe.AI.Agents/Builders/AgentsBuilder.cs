using Azure.Core;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Providers.Interfaces;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace GovUK.Dfe.AI.Agents.Builders;

/// <summary>Adds an app's agents, tools and code-only settings in <c>AddAgents</c>.</summary>
public sealed class AgentsBuilder
{
    private readonly List<Action<AgentsOptions>> _configureOptions = [];
    private readonly List<AgentDefinition> _definitions = [];
    private readonly List<IAgentsPackage> _packages = [];
    private readonly List<Privacy.Interfaces.IAgentInputRedactor> _redactors = [];

    internal AgentsBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection, for anything the builder doesn't cover.</summary>
    public IServiceCollection Services { get; }

    internal IReadOnlyList<AgentDefinition> Definitions => _definitions;

    internal IReadOnlyList<IAgentsPackage> Packages => _packages;

    internal IReadOnlyList<Privacy.Interfaces.IAgentInputRedactor> Redactors => _redactors;

    internal bool HasToolApprover { get; private set; }

    /// <summary>Adds an add-on package once per <see cref="IAgentsPackage.Name"/>, so calling its <c>Add…</c> method twice is harmless.</summary>
    public AgentsBuilder AddPackage(IAgentsPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!_packages.Exists(existing => existing.Name == package.Name))
        {
            _packages.Add(package);
        }

        return this;
    }

    /// <summary>Adds this app's agents.</summary>
    /// <exception cref="ArgumentException">Two definitions share a name.</exception>
    public AgentsBuilder AddAgents(params AgentDefinition[] definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var definition in definitions)
        {
            if (_definitions.Exists(existing => existing.Name == definition.Name))
            {
                throw new ArgumentException(string.Format(Constants.ErrorMessages.DuplicateAgentDefinition, definition.Name), nameof(definitions));
            }

            _definitions.Add(definition);
        }

        return this;
    }

    /// <summary>Gives an agent extra tools, e.g. <c>WebSearchToolProvider</c>. Called on every run.</summary>
    public AgentsBuilder AddTools(string agentName, IAgentToolProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(provider);

        Services.AddSingleton(new AgentToolBinding(agentName, provider));
        return this;
    }

    /// <summary>
    /// Asks <typeparamref name="TApprover"/> before every call to a tool in an agent's <c>ToolsRequiringApproval</c>, e.g. so
    /// a person approves changes to records. One approver serves every agent.
    /// </summary>
    public AgentsBuilder AddToolApprover<TApprover>() where TApprover : class, IToolCallApprover
    {
        Services.AddSingleton<IToolCallApprover, TApprover>();
        HasToolApprover = true;
        return this;
    }

    /// <summary>
    /// Removes personal data from every prompt, evidence and tool output before it's sent to a model, e.g. a
    /// <c>PatternRedactor</c> for pupil numbers. Several run in the order added.
    /// </summary>
    public AgentsBuilder AddRedactor(Privacy.Interfaces.IAgentInputRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        _redactors.Add(redactor);
        return this;
    }

    /// <summary>Builds an agent in code on every run, e.g. one per user.</summary>
    public AgentsBuilder AddAgentProvider<TProvider>() where TProvider : class, IManagedAgentProvider
    {
        Services.AddSingleton<IManagedAgentProvider, TProvider>();
        return this;
    }

    /// <summary>Deletes unused ephemeral agents every <paramref name="interval"/> (default 30 minutes).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The interval isn't positive.</exception>
    public AgentsBuilder AddEphemeralAgentSweep(TimeSpan? interval = null)
    {
        var every = interval ?? TimeSpan.FromMinutes(30);
        if (every <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");
        }

        Services.AddHostedService(sp => new Services.EphemeralAgentSweepService(sp.GetRequiredService<IAgentRuntimeService>(),
            every, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.EphemeralAgentSweepService>>()));
        return this;
    }

    /// <summary>The default credential for every service.</summary>
    public AgentsBuilder UseCredential(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return Configure(options => options.Credential = credential);
    }

    /// <summary>A credential for one of core's services, e.g. <c>AzureCredentialTarget.Foundry</c>. Overrides its block and the default.</summary>
    public AgentsBuilder UseCredentialFor(AzureCredentialTarget service, TokenCredential credential)
        => UseCredentialFor(service.ToString(), credential);

    /// <summary>
    /// A credential for a service by key, e.g. an add-on's (each add-on wraps this, e.g. <c>UseMcpCredential</c>).
    /// Overrides the service's block and the default.
    /// </summary>
    public AgentsBuilder UseCredentialFor(string serviceKey, TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        ArgumentNullException.ThrowIfNull(credential);
        return Configure(options => options.CredentialOverrides[serviceKey] = credential);
    }

    /// <summary>A credential for the <c>ExternallyManagedAgents</c> project (needs its <c>Endpoint</c>). Overrides its <c>Authentication</c> and this app's Foundry credential.</summary>
    public AgentsBuilder UseExternallyManagedAgentsCredential(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return Configure(options => options.CredentialOverrides[AgentsOptions.ExternallyManagedCredentialKey] = credential);
    }

    /// <summary>Changes settings in code, after configuration is read.</summary>
    public AgentsBuilder Configure(Action<AgentsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureOptions.Add(configure);
        return this;
    }

    internal void ApplyTo(AgentsOptions options) => _configureOptions.ForEach(configure => configure(options));
}

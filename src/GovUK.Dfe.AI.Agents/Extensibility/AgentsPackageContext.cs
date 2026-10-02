using Azure.Core;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GovUK.Dfe.AI.Agents.Extensibility;

/// <summary>What an <see cref="IAgentsPackage"/> registers with: the services, the checked settings and the app's agents.</summary>
/// <remarks>Part of the public extension point add-ons are built on, so it changes only in a major version.</remarks>
public sealed class AgentsPackageContext
{
    internal AgentsPackageContext(IServiceCollection services, IConfigurationSection section, AgentsOptions options,
        IReadOnlyList<AgentDefinition> definitions)
    {
        Services = services;
        Section = section;
        Options = options;
        Definitions = definitions;
    }

    /// <summary>The app's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The <c>AiAgents</c> configuration section, for a package's own settings.</summary>
    public IConfigurationSection Section { get; }

    /// <summary>The settings, already read and checked.</summary>
    public AgentsOptions Options { get; }

    /// <summary>The app's agents, as added with <c>AddAgents</c>.</summary>
    public IReadOnlyList<AgentDefinition> Definitions { get; }

    /// <summary>
    /// The credential for <paramref name="target"/>: the one set in code with <c>UseCredentialFor</c>, else
    /// <paramref name="authentication"/> (the service's own <c>Authentication</c> block), else the default.
    /// </summary>
    public TokenCredential CredentialFor(AzureCredentialTarget target, AgentsOptions.ServicePrincipalSettings? authentication)
        => Options.CredentialFor(target.ToString(), authentication);

    /// <summary>
    /// The credential for the MCP server <paramref name="serverName"/> (its key under <c>McpServers</c>): the one set in
    /// code with <c>UseMcpCredential</c>, else <paramref name="authentication"/>, else the default.
    /// </summary>
    public TokenCredential McpCredentialFor(string serverName, AgentsOptions.ServicePrincipalSettings? authentication)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        return Options.CredentialFor(AgentsOptions.McpCredentialKey(serverName), authentication);
    }
}

using Azure.Core;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GovUK.Dfe.AI.Agents.Extensibility;

/// <summary>
/// What an <see cref="IAgentsPackage"/> registers with: the services, the <c>AiAgents</c> section and the app's agents.
/// Each package reads and checks its own settings, so core never needs to change for one.
/// </summary>
/// <remarks>Part of the public extension point add-ons are built on, so it changes only in a major version.</remarks>
public sealed class AgentsPackageContext
{
    private readonly List<string> _problems = [];

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

    /// <summary>The <c>AiAgents</c> configuration section; a package reads its own block from it, e.g. <c>Section.GetSection("Search")</c>.</summary>
    public IConfigurationSection Section { get; }

    /// <summary>Core's settings, already read and checked.</summary>
    public AgentsOptions Options { get; }

    /// <summary>The app's agents, as added with <c>AddAgents</c>.</summary>
    public IReadOnlyList<AgentDefinition> Definitions { get; }

    /// <summary>The app's name, as tagged on telemetry: <c>AiAgents:ApplicationName</c>, or the entry assembly's name.</summary>
    public string ApplicationName => Options.ToRunOptions().ApplicationName;

    /// <summary>The services given a credential in code with <c>UseCredentialFor</c>, by key.</summary>
    public IReadOnlyCollection<string> CodeCredentials => Options.CredentialOverrides.Keys;

    internal IReadOnlyList<string> Problems => _problems;

    /// <summary>Gives <paramref name="agentName"/> the tools from a provider resolved from the app's services, on every run.</summary>
    public void AddTools(string agentName, Func<IServiceProvider, IAgentToolProvider> provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(provider);
        Services.AddSingleton(sp => new AgentToolBinding(agentName, provider(sp)));
    }

    /// <summary>
    /// Reports a missing or invalid setting, by its path under <c>AiAgents</c>, e.g. <c>"Search:Endpoint"</c>. Startup then
    /// fails with every problem from core and every package in one message.
    /// </summary>
    public void ReportProblem(string setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        _problems.Add($"{AgentsOptions.SectionName}:{setting}");
    }

    /// <summary>
    /// The credential for the service <paramref name="serviceKey"/>: the one set in code with <c>UseCredentialFor</c>, else
    /// <paramref name="authentication"/> (the service's own block at <paramref name="settingsPath"/>), else the default.
    /// An incomplete block, or a missing default, is reported as a problem.
    /// </summary>
    public TokenCredential CredentialFor(string serviceKey, AgentsOptions.ServicePrincipalSettings? authentication, string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        var problems = Options.CredentialProblems(serviceKey, authentication, settingsPath).ToList();
        if (problems.Count == 0)
        {
            return Options.CredentialFor(serviceKey, authentication);
        }

        _problems.AddRange(problems);
        return UnavailableCredential.Instance;   // never used: startup fails with the problems
    }

    private sealed class UnavailableCredential : TokenCredential
    {
        public static readonly UnavailableCredential Instance = new();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The credential's settings are incomplete.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The credential's settings are incomplete.");
    }
}

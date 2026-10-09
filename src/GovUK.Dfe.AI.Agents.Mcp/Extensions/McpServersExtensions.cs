using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Mcp.Clients;
using GovUK.Dfe.AI.Agents.Mcp.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Handlers;
using GovUK.Dfe.AI.Agents.Mcp.Options;
using GovUK.Dfe.AI.Agents.Mcp.Providers;
using GovUK.Dfe.AI.Agents.Mcp.Services;
using GovUK.Dfe.AI.Agents.Mcp.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Validators;
using GovUK.Dfe.AI.Agents.Extensibility;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Tools;
using Azure.Core;
using Azure.Identity;
using GovUK.Dfe.AI.Agents.Mcp.Constants;
using GovUK.Dfe.AI.Agents.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds MCP servers: to <c>AddAgents</c> as agents' tools, or on their own for an app that calls the tools itself.</summary>
public static class McpServersExtensions
{
    private const string SectionName = "McpServers";
    private const string CredentialPrefix = "Mcp:";

    /// <summary>
    /// Connects the servers under <c>AiAgents:McpServers</c> and gives each agent the tools named in its <c>AllowedTools</c>.
    /// Tools run in this app with its credential; Foundry only sees their definitions.
    /// </summary>
    public static AgentsBuilder AddMcpServers(this AgentsBuilder agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.AddPackage(new McpPackage());
    }

    /// <summary>
    /// Connects the servers under <c>AiAgents:McpServers</c> on their own, without <c>AddAgents</c> or Foundry. Inject a server's
    /// <c>IMcpToolClient</c> by its name, e.g. <c>[FromKeyedServices("school-performance")]</c>, to list and call its allowed
    /// tools. Each server signs in with its own <c>Authentication</c> block, else <paramref name="credential"/>. Calling this and
    /// <c>agents.AddMcpServers()</c> connects each server once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The section is missing, or a server's settings or sign-in are incomplete.</exception>
    public static IServiceCollection AddMcpServers(this IServiceCollection services, IConfiguration configuration, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var path = $"{AgentsOptions.SectionName}:{SectionName}";
        var servers = configuration.GetSection(path).Get<Dictionary<string, McpServerSettings>>() ?? [];
        if (servers.Count == 0)
        {
            throw new InvalidOperationException(ErrorMessages.McpServersSectionMissing);
        }

        var problems = new List<string>();
        var connections = new List<McpServerConnectionOptions>();
        foreach (var (key, server) in servers)
        {
            var serverProblems = server.Problems().Select(problem => $"{path}:{key}:{problem}").ToList();
            var signIn = server.Authentication is { } own ? CredentialFrom(own, $"{path}:{key}:Authentication", serverProblems) : credential;
            if (signIn is null)
            {
                serverProblems.Add($"{path}:{key}:Authentication (or pass a credential)");
            }

            problems.AddRange(serverProblems);
            if (serverProblems.Count == 0)
            {
                connections.Add(ConnectionFor(key, server, signIn!));
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Format(ErrorMessages.McpServersSettingsInvalid, string.Join(", ", problems)));
        }

        services.AddLogging();
        connections.ForEach(connection => services.AddMcpClientServices(connection.ServerLabel, connection));
        return services;
    }

    /// <summary>A credential for one MCP server (its key under <c>McpServers</c>). Overrides its <c>Authentication</c> and the default.</summary>
    public static AgentsBuilder UseMcpCredential(this AgentsBuilder agents, string serverName, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        return agents.UseCredentialFor(CredentialPrefix + serverName, credential);
    }

    /// <summary>One MCP server, keyed by <paramref name="serverKey"/>: its connection, sign-in and startup check.</summary>
    internal static IServiceCollection AddMcpClientServices(this IServiceCollection services, string serverKey,
        McpServerConnectionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);

        // Connected once, whichever of AddMcpServers' two forms runs first.
        if (services.Any(service => service.IsKeyedService && service.ServiceType == typeof(IMcpToolClient) && Equals(service.ServiceKey, serverKey)))
        {
            return services;
        }

        var httpClientName = $"{McpToolClient.HttpClientName}:{serverKey}";
        services.AddKeyedSingleton(serverKey, options);
        services.AddKeyedSingleton<ITokenService>(serverKey, (sp, _) =>
            new TokenService(options.Credential, options.Scope, sp.GetService<ILogger<TokenService>>()));
        services.AddHttpClient(httpClientName)
            .AddHttpMessageHandler(sp => new McpAuthenticationHandler(sp.GetRequiredKeyedService<ITokenService>(serverKey)));
        services.AddKeyedSingleton<IMcpToolClient>(serverKey, (sp, _) => new McpToolClient(options,
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<McpToolClient>>(), httpClientName));

        // Not AddHostedService: it dedups by type, which would drop every server after the first.
        services.AddSingleton<IHostedService>(sp => new McpToolStartupValidator(serverKey,
            sp.GetRequiredKeyedService<IMcpToolClient>(serverKey), options, sp.GetRequiredService<ILogger<McpToolStartupValidator>>()));

        return services;
    }

    private static McpServerConnectionOptions ConnectionFor(string key, McpServerSettings server, TokenCredential credential) => new()
    {
        ServerLabel = key,
        ServerUri = new Uri(server.ServerUri!),
        AllowedToolNames = server.AllowedToolNames,
        ToolListCacheDuration = server.ToolListCacheDuration,
        Credential = credential,
        Scope = server.Scope!,
    };

    /// <summary>A service principal from a server's <c>Authentication</c> block; missing parts are added to <paramref name="problems"/>.</summary>
    private static ClientSecretCredential? CredentialFrom(AgentsOptions.ServicePrincipalSettings principal, string path, List<string> problems)
    {
        var missing = new[] { ("TenantId", principal.TenantId), ("ClientId", principal.ClientId), ("ClientSecret", principal.ClientSecret) }
            .Where(static setting => string.IsNullOrWhiteSpace(setting.Item2)).Select(setting => $"{path}:{setting.Item1}").ToList();
        problems.AddRange(missing);
        return missing.Count > 0 ? null : new ClientSecretCredential(principal.TenantId, principal.ClientId, principal.ClientSecret,
            new ClientSecretCredentialOptions { AuthorityHost = principal.AuthorityHost ?? AzureAuthorityHosts.AzurePublicCloud });
    }

    private sealed class McpPackage : IAgentsPackage
    {
        public string Name => "Mcp";

        public void Register(AgentsPackageContext context)
        {
            var servers = context.Section.GetSection(SectionName).Get<Dictionary<string, McpServerSettings>>() ?? [];
            if (servers.Count == 0)
            {
                context.ReportProblem($"{SectionName} (agents.AddMcpServers() needs this section)");
                return;
            }

            // A code credential for a server that isn't configured is almost certainly a typo in its name.
            foreach (var key in context.CodeCredentials.Where(key => key.StartsWith(CredentialPrefix, StringComparison.Ordinal)
                         && !servers.ContainsKey(key[CredentialPrefix.Length..])))
            {
                context.ReportProblem($"{SectionName}:{key[CredentialPrefix.Length..]} (UseMcpCredential names a server that isn't configured)");
            }

            foreach (var (key, server) in servers)
            {
                var problems = server.Problems().ToList();
                problems.ForEach(problem => context.ReportProblem($"{SectionName}:{key}:{problem}"));
                var credential = context.CredentialFor(CredentialPrefix + key, server.Authentication, $"{SectionName}:{key}:Authentication");
                if (problems.Count == 0)
                {
                    Register(context, key, server, credential);
                }
            }
        }

        private static void Register(AgentsPackageContext context, string key, McpServerSettings server, TokenCredential credential)
        {
            var (services, definitions) = (context.Services, context.Definitions);
            services.AddMcpClientServices(key, ConnectionFor(key, server, credential));

            // Each agent gets only the tools its AllowedTools names, from the server that allows them.
            foreach (var definition in definitions)
            {
                var tools = server.AllowedToolNames.Where(name => definition.AllowedTools.Contains(McpToolClient.ToFunctionName(name))).ToList();
                if (tools.Count > 0)
                {
                    context.AddTools(definition.Name, sp => new McpAllowedToolsProvider(sp.GetRequiredKeyedService<IMcpToolClient>(key), tools));
                }
            }
        }
    }
}

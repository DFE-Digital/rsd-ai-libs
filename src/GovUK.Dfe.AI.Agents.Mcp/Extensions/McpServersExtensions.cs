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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds MCP servers to <c>AddAgents</c>.</summary>
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
            services.AddMcpClientServices(key, new McpServerConnectionOptions
            {
                ServerLabel = key,
                ServerUri = new Uri(server.ServerUri!),
                AllowedToolNames = server.AllowedToolNames,
                ToolListCacheDuration = server.ToolListCacheDuration,
                Credential = credential,
                Scope = server.Scope!,
            });

            // Each agent gets only the tools its AllowedTools names, from the server that allows them.
            foreach (var definition in definitions)
            {
                var tools = server.AllowedToolNames.Where(name => definition.AllowedTools.Contains(McpToolClient.ToFunctionName(name))).ToList();
                if (tools.Count > 0)
                {
                    services.AddSingleton(sp => new AgentToolBinding(definition.Name,
                        new McpAllowedToolsProvider(sp.GetRequiredKeyedService<IMcpToolClient>(key), tools)));
                }
            }
        }
    }
}

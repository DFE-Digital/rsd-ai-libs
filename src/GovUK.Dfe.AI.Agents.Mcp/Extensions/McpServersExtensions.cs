using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Mcp.Clients;
using GovUK.Dfe.AI.Agents.Mcp.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Handlers;
using GovUK.Dfe.AI.Agents.Mcp.Options;
using GovUK.Dfe.AI.Agents.Mcp.Providers;
using GovUK.Dfe.AI.Agents.Mcp.Services;
using GovUK.Dfe.AI.Agents.Mcp.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Validators;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Packages;
using GovUK.Dfe.AI.Agents.Packages.Interfaces;
using GovUK.Dfe.AI.Agents.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds MCP servers to <c>AddAgents</c>.</summary>
public static class McpServersExtensions
{
    /// <summary>
    /// Connects the servers under <c>AiAgents:McpServers</c> and gives each agent the tools named in its <c>AllowedTools</c>.
    /// Tools run in this app with its credential; Foundry only sees their definitions.
    /// </summary>
    public static AgentsBuilder AddMcpServers(this AgentsBuilder agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.AddPackage(new McpPackage());
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
            var (services, options, definitions) = (context.Services, context.Options, context.Definitions);
            foreach (var (key, server) in options.McpServers)
            {
                services.AddMcpClientServices(key, new McpServerConnectionOptions
                {
                    ServerLabel = key,
                    ServerUri = new Uri(server.ServerUri!),
                    AllowedToolNames = server.AllowedToolNames,
                    ToolListCacheDuration = server.ToolListCacheDuration,
                    Credential = context.McpCredentialFor(key, server.Authentication),
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
}

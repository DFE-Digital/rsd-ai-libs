using GovUK.Dfe.AI.Agents.Mcp.Constants;
using GovUK.Dfe.AI.Agents.Mcp.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Exceptions;
using GovUK.Dfe.AI.Agents.Mcp.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Mcp.Validators;

/// <summary>
/// Checks an MCP server's configuration at startup. Bad settings, or allowed tools the server doesn't have,
/// fail startup. An unreachable server only logs a warning, so a brief outage can't stop instances starting.
/// </summary>
public sealed class McpToolStartupValidator(string serverKey, IMcpToolClient client, McpServerConnectionOptions options,
    ILogger<McpToolStartupValidator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        options.Validate(serverKey);

        try
        {
            await client.GetToolsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (McpToolConfigurationException ex)
        {
            throw new InvalidOperationException(string.Format(ErrorMessages.McpStartupValidationFailed, serverKey), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MCP server '{ServerKey}' couldn't be reached at startup; its tools will be checked on first use", serverKey);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

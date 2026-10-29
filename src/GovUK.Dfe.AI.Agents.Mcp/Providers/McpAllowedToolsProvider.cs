using GovUK.Dfe.AI.Agents.Mcp.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Mcp.Providers;

/// <summary>Gives an agent a subset of an MCP server's tools, and runs only that subset when the model calls them.</summary>
/// <param name="allowedToolNames">The tool names this agent may see and call.</param>
public sealed class McpAllowedToolsProvider(IMcpToolClient client, IReadOnlyList<string> allowedToolNames)
    : IAgentToolProvider, IAgentToolExecutor
{
    private readonly HashSet<string> _allowedToolNames = new(allowedToolNames, StringComparer.Ordinal);

    public Task<IReadOnlyList<AgentTool>> GetToolsAsync(CancellationToken cancellationToken = default)
        => client.GetToolsAsync(allowedToolNames, cancellationToken);

    public async Task<string?> TryExecuteAsync(ToolCallRequest call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // Matched on the exact tool the call would run, not its function name: another tool can become the same function name.
        return client.GetAllowedToolName(call.FunctionName) is { } toolName && _allowedToolNames.Contains(toolName)
            ? await client.CallToolAsync(call.FunctionName, call.Arguments, cancellationToken).ConfigureAwait(false)
            : null;
    }
}

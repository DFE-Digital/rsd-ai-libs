using ModelContextProtocol.Protocol;

namespace GovUK.Dfe.AI.Agents.Mcp.Sessions.Interfaces;

/// <summary>
/// One live connection to an MCP server. <see cref="McpToolClient"/> replaces it when it stops working,
/// so a server restart or an expired session doesn't break every later call until the app restarts.
/// </summary>
internal interface IMcpSession : IAsyncDisposable
{
    Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken);

    Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);

    Task<GetPromptResult> GetPromptAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
}

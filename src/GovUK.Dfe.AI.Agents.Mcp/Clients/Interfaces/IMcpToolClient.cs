using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using OpenAI.Responses;

namespace GovUK.Dfe.AI.Agents.Mcp.Clients.Interfaces;

/// <summary>
/// Discovers tools and prompts on a remote MCP server and runs its tools from this app. Foundry only
/// ever sees function definitions and never contacts the server, so its credentials stay in this app.
/// </summary>
public interface IMcpToolClient : IAgentToolProvider, IAgentToolExecutor, IAsyncDisposable
{
    /// <summary>
    /// Describes the server's tools as function tools, restricted to <paramref name="allowedToolNames"/>
    /// (or every tool the server reports, when null/empty).
    /// </summary>
    Task<IReadOnlyList<ResponseTool>> GetToolsAsync(IReadOnlyList<string>? allowedToolNames, CancellationToken cancellationToken = default);

    /// <summary>Calls a tool on the server with this app's credential.</summary>
    /// <param name="functionName">The function name the model used (the tool's name, made safe for a function name).</param>
    /// <param name="argumentsJson">The call's arguments as a JSON object.</param>
    Task<string> CallToolAsync(string functionName, string argumentsJson, CancellationToken cancellationToken = default);

    /// <summary>A prompt from the server.</summary>
    Task<string> GetPromptAsync(string name, string promptType, CancellationToken cancellationToken = default);
}

using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Mcp.Diagnostics;

/// <summary>
/// Source-generated log messages: arguments are only formatted when the level is enabled.
/// </summary>
internal static partial class McpLog
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Debug,
        Message = "Ignoring an error closing a failed connection to MCP server '{ServerLabel}'")]
    public static partial void IgnoringCloseError(this ILogger logger, Exception exception, string serverLabel);
}

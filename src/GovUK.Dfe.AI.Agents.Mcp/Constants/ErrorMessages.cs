namespace GovUK.Dfe.AI.Agents.Mcp.Constants;

/// <summary>This package's error messages.</summary>
internal static class ErrorMessages
{
    internal const string McpConnectionFailed = "Failed to connect to MCP server '{0}' at {1}.";
    internal const string McpOptionsInvalid = "MCP server '{0}' configuration is invalid; missing or empty: {1}.";
    internal const string McpStartupValidationFailed = "MCP tool configuration validation failed during startup for server '{0}'.";
    internal const string McpToolArgumentsInvalid = "The arguments for {0} weren't a valid JSON object. Call it again with arguments matching its input schema.";
    internal const string McpToolCallFailed = "Call to tool '{0}' on MCP server '{1}' failed.";
    internal const string McpToolNamesClash = "MCP server '{0}' has tools ({1}) that all become the function name '{2}'. Rename one on the server.";
    internal const string McpToolNotAllowed = "Tool(s) '{0}' aren't in AllowedToolNames for MCP server '{1}', so they can't be given to an agent or run.";
    internal const string McpToolsNotFoundOnServer = "MCP server '{0}' does not expose the following configured tool(s): {1}.";
}

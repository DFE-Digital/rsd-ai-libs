using GovUK.Dfe.AI.Agents.Options;

namespace GovUK.Dfe.AI.Agents.Mcp.Options;

/// <summary>One <c>AiAgents:McpServers</c> entry, keyed by a name you choose.</summary>
public sealed class McpServerSettings
{
    /// <summary>The server's MCP endpoint, e.g. https://mcp.internal.example/mcp.</summary>
    public string? ServerUri { get; set; }

    /// <summary>The token scope, e.g. "api://school-performance/.default".</summary>
    public string? Scope { get; set; }

    /// <summary>Optional: a service principal for this server only, possibly in another tenant. Unset: the default.</summary>
    public AgentsOptions.ServicePrincipalSettings? Authentication { get; set; }

    /// <summary>Required: every tool this app may use from the server.</summary>
    public List<string> AllowedToolNames { get; set; } = [];

    /// <summary>How long the server's tool list is reused before asking again.</summary>
    public TimeSpan ToolListCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    internal IEnumerable<string> Problems()
    {
        if (!Uri.TryCreate(ServerUri, UriKind.Absolute, out var uri))
        {
            yield return "ServerUri";
        }
        else if (!McpServerConnectionOptions.IsSecure(uri))
        {
            yield return "ServerUri (must use https://, or http:// for localhost only)";
        }

        if (string.IsNullOrWhiteSpace(Scope))
        {
            yield return "Scope";
        }

        if (AllowedToolNames.Count == 0)
        {
            yield return "AllowedToolNames";
        }
    }
}

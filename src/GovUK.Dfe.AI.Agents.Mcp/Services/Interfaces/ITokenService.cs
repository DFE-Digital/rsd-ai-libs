namespace GovUK.Dfe.AI.Agents.Mcp.Services.Interfaces;

/// <summary>Gets an access token for one MCP server.</summary>
public interface ITokenService
{
    /// <summary>A valid access token for the server's scope, cached until shortly before it expires.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

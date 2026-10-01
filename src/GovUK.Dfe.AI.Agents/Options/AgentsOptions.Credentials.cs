using Azure.Core;
using Azure.Identity;

namespace GovUK.Dfe.AI.Agents.Options;

public sealed partial class AgentsOptions
{
    internal const string ExternallyManagedCredentialKey = nameof(ExternallyManagedAgents);

    private TokenCredential? _defaultCredential;

    /// <summary>Code only: per-service credentials, set with <c>UseCredentialFor</c> and <c>UseMcpCredential</c>.</summary>
    internal Dictionary<string, TokenCredential> CredentialOverrides { get; } = new(StringComparer.Ordinal);

    internal static string McpCredentialKey(string serverName) => $"Mcp:{serverName}";

    /// <summary>A service's credential: its code override, else its own <c>Authentication</c> block, else the default.</summary>
    internal TokenCredential CredentialFor(string serviceKey, ServicePrincipalSettings? own)
        => CredentialOverrides.GetValueOrDefault(serviceKey) ?? (own is null ? DefaultCredential() : Create(own));

    /// <summary>The default credential, created once so every service without its own block shares one token cache.</summary>
    private TokenCredential DefaultCredential()
    {
        _defaultCredential ??= Credential ?? Create(Authentication);
        return _defaultCredential;
    }

    /// <summary>The external project's credential: its code override, else its own block, else this app's Foundry credential.</summary>
    internal TokenCredential ExternallyManagedCredentialFor(TokenCredential foundryCredential)
        => CredentialOverrides.GetValueOrDefault(ExternallyManagedCredentialKey)
           ?? (ExternallyManagedAgents.Authentication is { } own ? Create(own) : foundryCredential);

    private static ClientSecretCredential Create(ServicePrincipalSettings principal)
        => new(principal.TenantId, principal.ClientId, principal.ClientSecret,
            new ClientSecretCredentialOptions { AuthorityHost = principal.AuthorityHost ?? AzureAuthorityHosts.AzurePublicCloud });
}

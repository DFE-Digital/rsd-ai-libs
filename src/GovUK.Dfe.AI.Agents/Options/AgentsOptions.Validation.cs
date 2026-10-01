using GovUK.Dfe.AI.Agents.Enums;

namespace GovUK.Dfe.AI.Agents.Options;

public sealed partial class AgentsOptions
{
    /// <summary>Lists every missing or invalid setting, so startup fails once with the whole picture.</summary>
    /// <param name="packages">The names of the add-on packages the app added.</param>
    internal IReadOnlyList<string> MissingSettings(IReadOnlyCollection<string>? packages = null)
        => [.. FoundryProblems(), .. LimitProblems(), .. CredentialProblems(), .. VersionProblems(), .. McpServerProblems(),
            .. ExternallyManagedProblems(), .. GuardrailProblems(), .. PackageProblems(packages ?? [])];

    private static IEnumerable<string> Missing(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? [$"{SectionName}:{name}"] : [];

    private IEnumerable<string> FoundryProblems()
        => [.. Missing(Foundry.Endpoint, "Foundry:Endpoint"), .. Missing(Foundry.DefaultModel, "Foundry:DefaultModel")];

    private IEnumerable<string> LimitProblems()
    {
        (bool Invalid, string Problem)[] checks =
        [
            (KeepLatestVersions is < 2, "KeepLatestVersions (must be at least 2)"),
            (MaxConcurrency is < 1, "MaxConcurrency (must be at least 1)"),
            (AgentCacheDuration < TimeSpan.Zero, "AgentCacheDuration (0 or more)"),
            (MaxWaitForRunSlot <= TimeSpan.Zero, "MaxWaitForRunSlot (must be positive)"),
            (MaxEvidenceCharacters < 1, "MaxEvidenceCharacters (must be at least 1)"),
            (MaxOutputTokensPerRun < AgentRunOptions.MinOutputTokens, $"MaxOutputTokensPerRun (must be at least {AgentRunOptions.MinOutputTokens})"),
        ];

        return checks.Where(static check => check.Invalid).Select(static check => $"{SectionName}:{check.Problem}")
            .Concat(GlobalConcurrency.Problems().Select(static problem => $"{SectionName}:GlobalConcurrency:{problem}"));
    }

    /// <summary>Each service needs a code credential, its own complete block, or the default; the default only if used.</summary>
    private IEnumerable<string> CredentialProblems()
    {
        List<(string ServiceKey, ServicePrincipalSettings? Own, string Path)> services =
            [(nameof(AzureCredentialTarget.Foundry), Foundry.Authentication, "Foundry:Authentication")];
        if (Search is not null)
        {
            services.Add((nameof(AzureCredentialTarget.Search), Search.Authentication, "Search:Authentication"));
        }

        if (GlobalConcurrency.IsEnabled)
        {
            services.Add((nameof(AzureCredentialTarget.RunSlots), GlobalConcurrency.Authentication, "GlobalConcurrency:Authentication"));
        }

        if (Guardrails is not null)
        {
            services.Add((nameof(AzureCredentialTarget.Guardrails), Guardrails.Authentication, "Guardrails:Authentication"));
        }

        services.AddRange(McpServers.Select(server => (McpCredentialKey(server.Key), server.Value.Authentication, $"McpServers:{server.Key}:Authentication")));

        var needingOwn = services.Where(service => !CredentialOverrides.ContainsKey(service.ServiceKey)).ToList();
        var problems = needingOwn.Where(static service => service.Own is not null)
            .SelectMany(static service => PrincipalProblems(service.Own!, service.Path));

        if (Credential is null && needingOwn.Exists(static service => service.Own is null))
        {
            problems = problems.Concat(PrincipalProblems(Authentication, "Authentication"));
        }

        return problems.Concat(CredentialOverrides.Keys
            .Where(key => key.StartsWith("Mcp:", StringComparison.Ordinal) && !McpServers.ContainsKey(key[4..]))
            .Select(static key => $"{SectionName}:McpServers:{key[4..]} (UseMcpCredential names a server that isn't configured)"));
    }

    private static IEnumerable<string> PrincipalProblems(ServicePrincipalSettings principal, string path)
        => [.. Missing(principal.TenantId, $"{path}:TenantId"), .. Missing(principal.ClientId, $"{path}:ClientId"),
            .. Missing(principal.ClientSecret, $"{path}:ClientSecret")];

    /// <summary>An agent's version is set in one place only, so there's nothing to keep in step.</summary>
    private IEnumerable<string> VersionProblems()
        => ExternallyManagedAgents.Agents.Keys.Where(VersionPins.ContainsKey)
            .Select(static agent => $"{SectionName}:VersionPins:{agent} (already versioned under ExternallyManagedAgents; remove one)");

    private IEnumerable<string> McpServerProblems()
        => McpServers.SelectMany(static server => (IEnumerable<string>)
        [
            .. Missing(server.Value.ServerUri, $"McpServers:{server.Key}:ServerUri"),
            .. Missing(server.Value.Scope, $"McpServers:{server.Key}:Scope"),
            .. server.Value.AllowedToolNames.Count == 0 ? [$"{SectionName}:McpServers:{server.Key}:AllowedToolNames"] : Array.Empty<string>(),
        ]);

    private List<string> ExternallyManagedProblems()
    {
        const string Path = $"{SectionName}:ExternallyManagedAgents";
        var external = ExternallyManagedAgents;
        var problems = new List<string>();

        // The list form binds as { "0": "agent-name" }; catch it rather than run an agent called "0".
        if (external.Agents.Keys.Any(static key => key.All(char.IsAsciiDigit)))
        {
            problems.Add($"{Path} (use {{ \"agent-name\": \"version\" }}, not a list)");
        }

        if (external.Endpoint is not null && !Uri.TryCreate(external.Endpoint, UriKind.Absolute, out _))
        {
            problems.Add($"{Path}:Endpoint");
        }

        // A credential only means something for another project; this app's own project uses its Foundry credential.
        var hasOwnCredential = external.Authentication is not null || CredentialOverrides.ContainsKey(ExternallyManagedCredentialKey);
        if (external.Endpoint is null && hasOwnCredential)
        {
            problems.Add($"{Path}:Endpoint (a credential for externally managed agents needs the other project's Endpoint)");
        }
        else if (external.Authentication is { } own && !CredentialOverrides.ContainsKey(ExternallyManagedCredentialKey))
        {
            problems.AddRange(PrincipalProblems(own, "ExternallyManagedAgents:Authentication"));
        }

        return problems;
    }

    private IEnumerable<string> GuardrailProblems()
        => Guardrails is null ? [] : Guardrails.Problems().Select(static problem => $"{SectionName}:Guardrails:{problem}");

    /// <summary>A section without its package, or a package without its section, would otherwise do nothing without a word.</summary>
    private IEnumerable<string> PackageProblems(IReadOnlyCollection<string> packages)
    {
        (bool Configured, string Section, string Package, string Method)[] needs =
        [
            (McpServers.Count > 0, "McpServers", "Mcp", "AddMcpServers()"),
            (Search is not null, "Search", "AISearch", "AddAISearch()"),
            (Guardrails is not null, "Guardrails", "Guardrails", "AddGuardrails()"),
        ];

        foreach (var (configured, section, package, method) in needs)
        {
            var added = packages.Contains(package);
            if (configured && !added)
            {
                yield return $"{SectionName}:{section} (add GovUK.Dfe.AI.Agents.{package} and call agents.{method})";
            }
            else if (added && !configured)
            {
                yield return $"{SectionName}:{section} (agents.{method} needs this section)";
            }
        }
    }
}

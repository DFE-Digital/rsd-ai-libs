using GovUK.Dfe.AI.Agents.Enums;

namespace GovUK.Dfe.AI.Agents.Options;

public sealed partial class AgentsOptions
{
    /// <summary>Lists every missing or invalid core setting; add-ons report their own through <c>AgentsPackageContext</c>.</summary>
    internal IReadOnlyList<string> MissingSettings()
        => [.. FoundryProblems(), .. LimitProblems(), .. CredentialProblems(), .. VersionProblems(), .. ExternallyManagedProblems()];

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
            (LowRemainingTokensPercent is < 0 or > 100, "LowRemainingTokensPercent (from 0 to 100)"),
            (AgentCacheDuration < TimeSpan.Zero, "AgentCacheDuration (0 or more)"),
            (MaxWaitForRunSlot <= TimeSpan.Zero, "MaxWaitForRunSlot (must be positive)"),
            (MaxEvidenceCharacters < 1, "MaxEvidenceCharacters (must be at least 1)"),
            (MaxOutputTokensPerRun < AgentRunOptions.MinOutputTokens, $"MaxOutputTokensPerRun (must be at least {AgentRunOptions.MinOutputTokens})"),
        ];

        return checks.Where(static check => check.Invalid).Select(static check => $"{SectionName}:{check.Problem}")
            .Concat(GlobalConcurrency.Problems().Select(static problem => $"{SectionName}:GlobalConcurrency:{problem}"))
            .Concat(Pricing.Problems().Select(static problem => $"{SectionName}:{problem}"));
    }

    private IEnumerable<string> CredentialProblems()
    {
        var problems = CredentialProblems(nameof(AzureCredentialTarget.Foundry), Foundry.Authentication, "Foundry:Authentication");
        return GlobalConcurrency.IsEnabled
            ? problems.Concat(CredentialProblems(nameof(AzureCredentialTarget.RunSlots), GlobalConcurrency.Authentication,
                "GlobalConcurrency:Authentication"))
            : problems;
    }

    /// <summary>
    /// A service needs a code credential, its own complete block, or the default; the default is checked only when used.
    /// </summary>
    internal IEnumerable<string> CredentialProblems(string serviceKey, ServicePrincipalSettings? own, string path)
    {
        if (CredentialOverrides.ContainsKey(serviceKey))
        {
            return [];
        }

        if (own is not null)
        {
            return PrincipalProblems(own, path);
        }

        return Credential is null ? PrincipalProblems(Authentication, "Authentication") : [];
    }

    private static IEnumerable<string> PrincipalProblems(ServicePrincipalSettings principal, string path)
        => [.. Missing(principal.TenantId, $"{path}:TenantId"), .. Missing(principal.ClientId, $"{path}:ClientId"),
            .. Missing(principal.ClientSecret, $"{path}:ClientSecret")];

    /// <summary>An agent's version is set in one place only, so there's nothing to keep in step.</summary>
    private IEnumerable<string> VersionProblems()
        => ExternallyManagedAgents.Agents.Keys.Where(VersionPins.ContainsKey)
            .Select(static agent => $"{SectionName}:VersionPins:{agent} (already versioned under ExternallyManagedAgents; remove one)");

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
}

using Azure.Core;

namespace GovUK.Dfe.AI.Agents.Options;

/// <summary>
/// Every <c>AddAgents</c> setting, bound from the <c>"AiAgents"</c> configuration section. The section types are in
/// AgentsOptions.Sections.cs, startup checks in AgentsOptions.Validation.cs, and credentials in AgentsOptions.Credentials.cs.
/// </summary>
public sealed partial class AgentsOptions
{
    public const string SectionName = "AiAgents";

    // ===== Connections and sign-in =====

    /// <summary>Tags all telemetry and scopes the ephemeral-agent sweep. Defaults to the entry assembly's name.</summary>
    public string? ApplicationName { get; set; }

    /// <summary>The Foundry project and default model.</summary>
    public FoundrySettings Foundry { get; set; } = new();

    /// <summary>The default Microsoft Entra ID service principal, for every service without its own <c>Authentication</c> block.</summary>
    public ServicePrincipalSettings Authentication { get; set; } = new();

    /// <summary>Code only: the default credential, instead of <see cref="Authentication"/>. Set with <c>UseCredential</c>.</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>Foundry client retries on a transient failure. A retried call may be billed twice.</summary>
    public int MaxRetries { get; set; } = 3;

    // ===== Each run =====

    /// <summary>The longest one run may take, tool rounds included. Unset: no limit.</summary>
    public TimeSpan? RunTimeout { get; set; }

    /// <summary>Output tokens one run may use, across tool rounds (reasoning included); a run that uses them all fails.</summary>
    public int MaxOutputTokensPerRun { get; set; } = 32_000;

    /// <summary>The most characters of evidence sent with one run; the rest is cut off with a note.</summary>
    public int MaxEvidenceCharacters { get; set; } = 100_000;

    /// <summary>The most characters of one tool's output sent to the model; the rest is cut off with a note.</summary>
    public int MaxToolOutputCharacters { get; set; } = 20_000;

    /// <summary>Fences tool output as data the model mustn't take instructions from.</summary>
    public bool FenceToolOutput { get; set; } = true;

    /// <summary>Deletes each conversation after its run, so prompts and evidence aren't kept in Foundry.</summary>
    public bool DeleteConversationsAfterRun { get; set; } = true;

    /// <summary>A system prompt (by key) appended to every agent's instructions, e.g. a shared answer format.</summary>
    public string? ResponseFormatKey { get; set; }

    /// <summary>System prompt keys that don't get <see cref="ResponseFormatKey"/> appended.</summary>
    public HashSet<string> ResponseFormatExemptPromptTypes { get; set; } = [];

    // ===== Scaling =====

    /// <summary>The most agent runs this instance makes at once, across every caller. Unset: no limit.</summary>
    public int? MaxConcurrency { get; set; }

    /// <summary>A limit on agent runs shared by every instance. Off unless <c>MaxConcurrentRuns</c> is set.</summary>
    public GlobalConcurrencySettings GlobalConcurrency { get; set; } = new();

    /// <summary>How long a run waits for a free slot (per-instance or global) before failing with a <see cref="TimeoutException"/>.</summary>
    public TimeSpan MaxWaitForRunSlot { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a resolved agent version is reused before asking Foundry again. 0 turns caching off.</summary>
    public TimeSpan AgentCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    // ===== Versions =====

    /// <summary>The version this environment runs, for agents this app builds itself.</summary>
    public Dictionary<string, string> VersionPins { get; set; } = [];

    /// <summary>Versions pruning must keep, per agent, e.g. those other environments are pinned to.</summary>
    public Dictionary<string, List<string>> ProtectedVersions { get; set; } = [];

    /// <summary>
    /// Keeps this many of the newest versions each time this app creates one (e.g. 3 keeps 5, 4 and 3). At least 2, so a
    /// rolling deploy's previous version survives. Pinned and protected versions are always kept. Unset: never prunes.
    /// </summary>
    public int? KeepLatestVersions { get; set; }

    /// <summary>Agents provisioned by another pipeline, which this app only runs; optionally in another Foundry project.</summary>
    public ExternallyManagedAgentsSettings ExternallyManagedAgents { get; set; } = new();

    // ===== Startup checks =====

    /// <summary>Fails startup when nothing records token metrics. Turn off only for tests and local development.</summary>
    public bool RequireTokenUsageTelemetry { get; set; } = true;

    /// <summary>Checks at startup that this app can run every tool its pinned and externally managed agents call.</summary>
    public bool ValidateAgentToolsAtStartup { get; set; } = true;

    /// <summary>Warns at startup when a pinned version no longer matches its agent's definition.</summary>
    public bool EnableDriftDetection { get; set; }

    /// <summary>The pins this environment runs, and the versions pruning must keep.</summary>
    /// <remarks>Externally managed agents in this app's project are pinned too; those in another project are resolved there.</remarks>
    internal AgentVersionPinningOptions ToVersionPinning() => new()
    {
        VersionPins = VersionPins
            .Concat(ExternallyManagedAgents.InOtherProject ? [] : ExternallyManagedAgents.Agents.Where(static agent => !FollowsLatest(agent.Value)))
            .ToDictionary(static pin => pin.Key, static pin => pin.Value),
        ProtectedVersions = ProtectedVersions.ToDictionary(static entry => entry.Key, static entry => (IReadOnlyList<string>)entry.Value),
    };

    /// <summary>Whether a configured version means "the latest": empty or <c>"latest"</c>.</summary>
    internal static bool FollowsLatest(string? version)
        => string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase);

    internal AgentRunOptions ToRunOptions() => new()
    {
        ApplicationName = string.IsNullOrWhiteSpace(ApplicationName) ? Diagnostics.AgentTelemetry.DefaultApplicationName : ApplicationName,
        RunTimeout = RunTimeout,
        MaxConcurrency = MaxConcurrency,
        MaxToolOutputCharacters = MaxToolOutputCharacters,
        FenceToolOutput = FenceToolOutput,
        MaxEvidenceCharacters = MaxEvidenceCharacters,
        MaxOutputTokensPerRun = MaxOutputTokensPerRun,
        MaxWaitForRunSlot = MaxWaitForRunSlot,
        DeleteConversationsAfterRun = DeleteConversationsAfterRun,
        RequireTokenUsageTelemetry = RequireTokenUsageTelemetry,
        ValidateAgentToolsAtStartup = ValidateAgentToolsAtStartup,
    };
}

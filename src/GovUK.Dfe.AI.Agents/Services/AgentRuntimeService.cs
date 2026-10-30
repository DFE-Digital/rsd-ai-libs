using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Options;

namespace GovUK.Dfe.AI.Agents.Services;

/// <summary>Resolves, creates and runs agents directly, and cleans up ephemeral ones. Most apps use <c>IAgentService</c>.</summary>
internal sealed class AgentRuntimeService(IAgentFactory factory, IAgentRunnerService runner,
    AgentVersionPinningOptions? versionPinning = null, ILogger<AgentRuntimeService>? logger = null, AgentRunOptions? runOptions = null)
    : IAgentRuntimeService
{
    private const int EphemeralSuffixLength = 32;
    private const int ApplicationHashLength = 8;

    private static readonly TimeSpan EphemeralCleanupTimeout = AgentRunOptions.EphemeralCleanupTimeout;

    private readonly AgentVersionPinningOptions _versionPinning = versionPinning ?? new AgentVersionPinningOptions();
    private readonly ILogger<AgentRuntimeService> _logger = logger ?? NullLogger<AgentRuntimeService>.Instance;
    private readonly AgentRunOptions _runOptions = runOptions ?? new AgentRunOptions();

    /// <summary>
    /// Identifies this application's ephemeral agents in a shared Foundry project, so its orphan sweep
    /// never deletes another application's agents mid-run.
    /// </summary>
    private string ApplicationHash => ApplicationHashOf(_runOptions.ApplicationName);

    public Task<AgentReference> ResolveAsync(string agentName, CancellationToken cancellationToken)
        => factory.ResolveAsync(agentName, _versionPinning.GetPinnedVersion(agentName), cancellationToken);

    public Task<AgentReference> ResolveAsync(AgentReference created, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(created);

        var pinnedVersion = _versionPinning.GetPinnedVersion(created.Name);
        return pinnedVersion is null
            ? Task.FromResult(created)
            : factory.ResolveAsync(created.Name, pinnedVersion, cancellationToken);
    }

    public async Task<AgentReference> GetOrCreateAsync(string agentName, Func<CancellationToken, Task<AgentSpec>> buildSpec,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(buildSpec);

        if (_versionPinning.GetPinnedVersion(agentName) is { } pinnedVersion)
        {
            return await factory.ResolveAsync(agentName, pinnedVersion, cancellationToken).ConfigureAwait(false);
        }

        var spec = await buildSpec(cancellationToken).ConfigureAwait(false);
        return await factory.GetOrCreateAsync(spec, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentResult> RunEphemeralAsync(AgentSpec spec, string prompt,
        ToolCallResolver? resolveToolCalls = null,
        string? evidence = null, Func<AgentResult, string?>? validateOutput = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var reportedName = spec.Name;
        // <name>-<8 hex: this application><24 hex: random>: unique per run, and recognisable as this
        // application's so the orphan sweep can tell its own agents apart in a shared project.
        var randomPart = Guid.NewGuid().ToString("N")[..(EphemeralSuffixLength - ApplicationHashLength)];
        var ephemeralSpec = spec with { Name = $"{spec.Name}-{ApplicationHash}{randomPart}" };

        try
        {
            var result = await runner.RunFromSpecAsync(ephemeralSpec, prompt, resolveToolCalls: resolveToolCalls, cancellationToken: cancellationToken,
                    additionalContext: evidence, validateOutput: validateOutput)
                .ConfigureAwait(false);
            return result with { AgentName = reportedName, AgentVersion = null };
        }
        finally
        {
            // Its own budget rather than the caller's token, so a cancelled run still cleans up.
            using var cleanupSource = new CancellationTokenSource(EphemeralCleanupTimeout);
            try
            {
                await factory.DeleteAgentAsync(ephemeralSpec.Name, cleanupSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete ephemeral agent {AgentName}; it may be orphaned in Foundry.", ephemeralSpec.Name);
            }
        }
    }

    public Task<IReadOnlyList<string>> DeleteOrphanedEphemeralAgentsAsync(TimeSpan minimumAge, CancellationToken cancellationToken = default)
    {
        // Every instance may run this sweep at once: deletes of an agent another instance already removed succeed.
        // A run (on any instance) can't still be using an agent older than its timeout, plus the agent's clean-up and a
        // minute for the app's clock and Foundry's to differ.
        var runTimeout = _runOptions.RunTimeout ?? throw new InvalidOperationException(ErrorMessages.OrphanCleanupNeedsRunTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAge, runTimeout + EphemeralCleanupTimeout + TimeSpan.FromMinutes(1));

        var ownPrefix = ApplicationHash;
        return factory.DeleteStaleAgentsAsync(
            name => IsEphemeralName(name) && name[^EphemeralSuffixLength..].StartsWith(ownPrefix, StringComparison.Ordinal),
            minimumAge, cancellationToken);
    }

    internal static string ApplicationHashOf(string applicationName)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(applicationName)))
            [..ApplicationHashLength].ToLowerInvariant();

    /// <summary>Matches the <c>&lt;name&gt;-&lt;Guid:N&gt;</c> names <see cref="RunEphemeralAsync"/> creates.</summary>
    internal static bool IsEphemeralName(string agentName)
        => agentName.Length > EphemeralSuffixLength + 1
           && agentName[^(EphemeralSuffixLength + 1)] == '-'
           && Guid.TryParseExact(agentName[^EphemeralSuffixLength..], "N", out _);
}

using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.Prompts.Interfaces;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Providers.Interfaces;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Validators;

/// <summary>At startup, warns when an agent's pinned version no longer matches its definition.</summary>
internal sealed class PinnedAgentVersionDriftValidator(IAgentDefinitionProvider definitionProvider, AgentVersionPinningOptions versionPinning,
    IAgentFactory agentFactory, IPromptProvider promptProvider, IEnumerable<IManagedAgentProvider>? managedAgentProviders = null,
    IEnumerable<AgentToolBinding>? toolBindings = null, ILogger<PinnedAgentVersionDriftValidator>? logger = null) : IHostedService
{
    private readonly AgentSpecBuilder _specs = new(promptProvider, toolBindings, managedAgentProviders);
    private readonly ILogger<PinnedAgentVersionDriftValidator> _logger = logger ?? NullLogger<PinnedAgentVersionDriftValidator>.Instance;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in definitionProvider.GetAgentsDefinitions())
        {
            await CheckDefinitionAsync(definition, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task CheckDefinitionAsync(AgentDefinition definition, CancellationToken cancellationToken)
    {
        if (!definition.IsManagedAgent)
        {
            return;
        }

        var pinnedVersion = versionPinning.GetPinnedVersion(definition.Name);
        if (pinnedVersion is null)
        {
            return;
        }

        // Built exactly as a run would build it - including custom providers' specs - so a pin is compared
        // against what the code would deploy today. Externally managed agents have no spec here to compare.
        AgentSpec? spec;
        try
        {
            spec = await _specs.BuildAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && IsUnavailable(ex, cancellationToken))
        {
            // Building lists the agent's bound tools, e.g. from an MCP server that may be down: only a warning, like the rest.
            // A configuration mistake (InvalidOperationException), e.g. an allowed tool nothing offers, still fails startup.
            _logger.LogWarning(ex, "Could not build agent '{AgentName}' to check its pinned version for drift.", definition.Name);
            return;
        }

        if (spec is null)
        {
            _logger.SkippingPinnedDriftCheck(definition.Name);
            return;
        }

        await WarnIfDefinitionChangedAsync(definition.Name, spec, pinnedVersion, cancellationToken).ConfigureAwait(false);
        await WarnIfNewerVersionExistsAsync(definition.Name, pinnedVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Warns when the pinned version's model, instructions or tools no longer match the definition.</summary>
    private async Task WarnIfDefinitionChangedAsync(string agentName, AgentSpec spec, string pinnedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await agentFactory.MatchesDeployedVersionAsync(spec, pinnedVersion, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Pinned version '{Version}' for agent '{AgentName}' no longer matches its current definition " +
                    "(model, instructions or tools have changed since it was pinned, or that version no longer exists). Consider re-pinning.",
                    pinnedVersion, agentName);
            }
        }
        catch (Exception ex) when (IsUnavailable(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "Could not check pinned version drift for agent '{AgentName}' version '{Version}'.",
                agentName, pinnedVersion);
        }
    }

    /// <summary>
    /// Warns when Foundry has a newer version than the pin, even if the pinned content still matches: a newer version
    /// created some other way, or by a promotion step that didn't re-pin, is worth its own signal. Re-pinning is a
    /// decision for whoever owns the environment's settings, not this validator.
    /// </summary>
    private async Task WarnIfNewerVersionExistsAsync(string agentName, string pinnedVersion, CancellationToken cancellationToken)
    {
        try
        {
            var latest = await agentFactory.ResolveLatestAsync(agentName, cancellationToken).ConfigureAwait(false);
            if (latest.Version != pinnedVersion)
            {
                _logger.LogWarning(
                    "Agent '{AgentName}' is pinned to version '{PinnedVersion}', but Foundry's latest version is '{LatestVersion}'. Consider re-pinning.",
                    agentName, pinnedVersion, latest.Version);
            }
        }
        catch (Exception ex) when (IsUnavailable(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "Could not resolve the latest version for agent '{AgentName}' while checking pinned version drift.",
                agentName);
        }
    }

    /// <summary>
    /// Any failure but a cancelled startup. A timeout is also an <see cref="OperationCanceledException"/> (e.g. HttpClient's
    /// <see cref="TaskCanceledException"/>), so a slow service only warns, like an unreachable one.
    /// </summary>
    private static bool IsUnavailable(Exception exception, CancellationToken cancellationToken)
        => exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;
}

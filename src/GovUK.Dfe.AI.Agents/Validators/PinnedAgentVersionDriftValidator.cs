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
        var spec = await _specs.BuildAsync(definition, cancellationToken).ConfigureAwait(false);
        if (spec is null)
        {
            _logger.LogInformation("Skipping pinned version drift check for '{AgentName}'; it's managed outside this app.", definition.Name);
            return;
        }

        try
        {
            var matches = await agentFactory.MatchesDeployedVersionAsync(spec, pinnedVersion, cancellationToken).ConfigureAwait(false);
            if (!matches)
            {
                _logger.LogWarning(
                    "Pinned version '{Version}' for agent '{AgentName}' no longer matches its current definition " +
                    "(model, instructions or tools have changed since it was pinned, or that version no longer exists). Consider re-pinning.",
                    pinnedVersion, definition.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not check pinned version drift for agent '{AgentName}' version '{Version}'.",
                definition.Name, pinnedVersion);
        }

        // Separate from the content check above: the pinned version's content can still match the
        // current spec while a newer version number already exists for this agent (created some
        // other way, or by a promotion step that bumped Foundry without re-pinning) - that's worth
        // its own signal, since re-pinning is a decision for whoever owns the environment's config,
        // not something this validator should do on their behalf.
        try
        {
            var latest = await agentFactory.ResolveLatestAsync(definition.Name, cancellationToken).ConfigureAwait(false);
            if (latest.Version != pinnedVersion)
            {
                _logger.LogWarning(
                    "Agent '{AgentName}' is pinned to version '{PinnedVersion}', but Foundry's latest version is '{LatestVersion}'. Consider re-pinning.",
                    definition.Name, pinnedVersion, latest.Version);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not resolve the latest version for agent '{AgentName}' while checking pinned version drift.",
                definition.Name);
        }
    }
}

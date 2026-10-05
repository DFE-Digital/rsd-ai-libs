using Azure.AI.Projects.Agents;
using GovUK.Dfe.AI.Agents.Services;
using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Factories;

/// <summary>
/// Agent versions in Foundry: reuses the version that matches a spec or creates one, resolves pinned and latest versions,
/// and prunes and deletes old ones. Resolved versions are cached briefly (<see cref="FoundryAgentFactoryOptions.AgentCacheDuration"/>).
/// </summary>
internal sealed class FoundryAgentFactory(AgentAdministrationClient administrationClient, FoundryAgentFactoryOptions options,
    ILogger<FoundryAgentFactory>? logger = null, AgentVersionPinningOptions? versionPinning = null, TimeProvider? timeProvider = null)
    : IAgentFactory
{
    /// <summary>How many recent versions to search when the latest doesn't match, e.g. when two deployments share a name.</summary>
    private const int RecentVersionsToSearch = 50;

    private readonly AgentReferenceCache _cache = new(options.AgentCacheDuration, timeProvider ?? TimeProvider.System);
    private readonly ILogger<FoundryAgentFactory> _logger = logger ?? NullLogger<FoundryAgentFactory>.Instance;
    private readonly AgentVersionPinningOptions _versionPinning = versionPinning ?? new AgentVersionPinningOptions();

    // Stops this instance creating duplicate versions. Two instances racing can still each create one; the next
    // lookup reuses whichever exists, so the duplicate is harmless.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _creationGates = new();

    public async Task<AgentReference> GetOrCreateAsync(AgentSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Instructions);

        // Ephemeral agents are used once, so there's nothing to cache.
        var cacheKey = AgentRuntimeService.IsEphemeralName(spec.Name) ? null : SpecSignature(spec);
        if (cacheKey is not null && _cache.TryGet(spec.Name, cacheKey) is { } cached)
        {
            return cached;
        }

        var gate = _creationGates.GetOrAdd(spec.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var agent = await FindMatchingVersionAsync(spec, cancellationToken).ConfigureAwait(false) is { } existing
                ? new AgentReference(existing.Id, spec.Name, existing.Version)
                : await CreateVersionAsync(spec, cancellationToken).ConfigureAwait(false);

            if (cacheKey is not null)
            {
                _cache.Set(spec.Name, cacheKey, agent);
            }

            return agent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to get or create Foundry agent for {AgentName}", spec.Name);
            throw new InvalidOperationException(string.Format(ErrorMessages.AgentGetOrCreateFailed, spec.Name), ex);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AgentReference> ResolveAsync(string name, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var cacheKey = "version:" + (version ?? "latest");
        if (_cache.TryGet(name, cacheKey) is { } cached)
        {
            return cached;
        }

        var deployed = await GetVersionAsync(name, version, cancellationToken).ConfigureAwait(false);
        var resolved = new AgentReference(deployed.Id, name, deployed.Version);
        _cache.Set(name, cacheKey, resolved);
        return resolved;
    }

    public Task<AgentReference> ResolveLatestAsync(string name, CancellationToken cancellationToken = default)
        => ResolveAsync(name, version: null, cancellationToken);

    public async Task<IReadOnlyList<string>> GetFunctionToolNamesAsync(string name, string? version = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var deployed = await GetVersionAsync(name, version, cancellationToken).ConfigureAwait(false);
        return deployed.Definition is DeclarativeAgentDefinition definition
            ? [.. definition.Tools.OfType<FunctionTool>().Select(tool => tool.FunctionName)]
            : [];
    }

    public async Task<bool> MatchesDeployedVersionAsync(AgentSpec spec, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        try
        {
            var deployed = (await administrationClient.GetAgentVersionAsync(spec.Name, version, cancellationToken).ConfigureAwait(false)).Value;
            return Matches(spec, deployed.Definition);
        }
        catch (ClientResultException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task DeleteAgentAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            await administrationClient.DeleteAgentAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (ClientResultException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            // Already gone, e.g. another instance deleted it first: the goal is met.
            _logger.AgentAlreadyDeleted(ex, name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to delete Foundry agent {AgentName}", name);
            throw new InvalidOperationException(string.Format(ErrorMessages.AgentDeleteFailed, name), ex);
        }
        finally
        {
            // Removed, not disposed: a GetOrCreateAsync for the same name may still hold it.
            _creationGates.TryRemove(name, out _);
            _cache.Remove(name);
        }
    }

    public async Task PruneVersionsAsync(string name, int keepLatestVersions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepLatestVersions, 1);

        if (_versionPinning.GetPinnedVersion(name) is { } pinned)
        {
            // A pinned environment only uses the agent; its versions are managed where they're created.
            _logger.NotPruningPinnedAgent(name, pinned);
            return;
        }

        try
        {
            var versions = new List<ProjectsAgentVersion>();
            await foreach (var version in administrationClient.GetAgentVersionsAsync(name, limit: null, order: null,
                after: null, before: null, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                versions.Add(version);
            }

            foreach (var version in versions.OrderByDescending(v => v.CreatedAt).Skip(keepLatestVersions).Select(v => v.Version))
            {
                if (_versionPinning.IsProtected(name, version))
                {
                    _logger.KeepingProtectedVersion(version, name);
                    continue;
                }

                await DeleteVersionAsync(name, version, cancellationToken).ConfigureAwait(false);
                _cache.Remove(name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to prune versions for Foundry agent {AgentName}", name);
            throw new InvalidOperationException(string.Format(ErrorMessages.AgentPruneVersionsFailed, name), ex);
        }
    }

    public async Task<IReadOnlyList<string>> DeleteStaleAgentsAsync(Func<string, bool> isCandidate, TimeSpan minimumAge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isCandidate);

        var cutoff = DateTimeOffset.UtcNow - minimumAge;
        var stale = new List<string>();
        await foreach (var record in administrationClient.GetAgentsAsync(kind: null, limit: null, order: null, after: null, before: null,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (isCandidate(record.Name) && record.GetLatestVersion()?.CreatedAt < cutoff)
            {
                stale.Add(record.Name);
            }
        }

        var deleted = new List<string>(stale.Count);
        foreach (var name in stale)
        {
            try
            {
                await DeleteAgentAsync(name, cancellationToken).ConfigureAwait(false);
                deleted.Add(name);
            }
            catch (InvalidOperationException ex)
            {
                // DeleteAgentAsync has logged it; carry on so one failure doesn't block the rest.
                _logger.SkippingStaleAgent(ex, name);
            }
        }

        if (deleted.Count > 0)
        {
            _logger.DeletedStaleAgents(deleted.Count, deleted);
        }

        return deleted;
    }

    internal static ResponseTextOptions ToTextOptions(AgentOutputSchema schema) => new()
    {
        TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(schema.Name, BinaryData.FromString(schema.JsonSchema),
            schema.Description, jsonSchemaIsStrict: true),
    };

    /// <summary>The given version, or the latest when <paramref name="version"/> is null.</summary>
    /// <exception cref="InvalidOperationException">The agent or version doesn't exist.</exception>
    private async Task<ProjectsAgentVersion> GetVersionAsync(string name, string? version, CancellationToken cancellationToken)
    {
        try
        {
            return version is null
                ? (await administrationClient.GetAgentAsync(name, cancellationToken).ConfigureAwait(false)).Value.GetLatestVersion()
                : (await administrationClient.GetAgentVersionAsync(name, version, cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (ClientResultException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            var versionSuffix = version is null ? string.Empty : string.Format(ErrorMessages.AgentVersionNotFound, version);
            throw new InvalidOperationException(string.Format(ErrorMessages.AgentNotFound, name, versionSuffix), ex);
        }
    }

    /// <summary>
    /// A version matching the spec: the latest (one call), else the newest match among the last
    /// <see cref="RecentVersionsToSearch"/>, so deployments sharing a name don't keep creating versions.
    /// </summary>
    private async Task<ProjectsAgentVersion?> FindMatchingVersionAsync(AgentSpec spec, CancellationToken cancellationToken)
    {
        ProjectsAgentVersion latest;
        try
        {
            latest = (await administrationClient.GetAgentAsync(spec.Name, cancellationToken).ConfigureAwait(false)).Value.GetLatestVersion();
        }
        catch (ClientResultException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }

        if (Matches(spec, latest.Definition))
        {
            return latest;
        }

        var searched = 0;
        await foreach (var version in administrationClient.GetAgentVersionsAsync(spec.Name, limit: RecentVersionsToSearch,
            order: AgentListOrder.Descending, after: null, before: null, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (Matches(spec, version.Definition))
            {
                _logger.ReusingMatchingVersion(version.Version, spec.Name);
                return version;
            }

            if (++searched >= RecentVersionsToSearch)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>Creates a new version of <c>spec.Name</c>, then prunes old ones if <c>KeepLatestVersions</c> is set.</summary>
    private async Task<AgentReference> CreateVersionAsync(AgentSpec spec, CancellationToken cancellationToken)
    {
        ProjectsAgentVersion version;
        try
        {
            var definition = new DeclarativeAgentDefinition(model: spec.Model ?? options.DefaultModel)
            {
                Instructions = spec.Instructions,
                TextOptions = spec.OutputSchema is null ? null : ToTextOptions(spec.OutputSchema),
            };
            foreach (var tool in spec.Tools)
            {
                definition.Tools.Add(tool);
            }

            version = (await administrationClient.CreateAgentVersionAsync(spec.Name,
                new ProjectsAgentVersionCreationOptions(definition) { Description = spec.Description }, null, cancellationToken)
                .ConfigureAwait(false)).Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to create Foundry agent for {AgentName}", spec.Name);
            throw new InvalidOperationException(string.Format(ErrorMessages.AgentCreateFailed, spec.Name), ex);
        }

        await PruneAfterCreateAsync(spec.Name, cancellationToken).ConfigureAwait(false);
        return new AgentReference(version.Id, spec.Name, version.Version);
    }

    /// <summary>Best effort: a failed prune is logged and never fails the creation that triggered it.</summary>
    private async Task PruneAfterCreateAsync(string name, CancellationToken cancellationToken)
    {
        if (options.KeepLatestVersions is not int keep || AgentRuntimeService.IsEphemeralName(name))
        {
            return;
        }

        try
        {
            await PruneVersionsAsync(name, keep, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Created a new version of {AgentName} but couldn't prune old versions; will retry after the next one", name);
        }
    }

    private async Task DeleteVersionAsync(string name, string version, CancellationToken cancellationToken)
    {
        try
        {
            await administrationClient.DeleteAgentVersionAsync(name, version, cancellationToken).ConfigureAwait(false);
        }
        catch (ClientResultException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            _logger.AgentVersionAlreadyDeleted(ex, version, name);   // another instance pruned it
        }
    }

    /// <summary>Whether a deployed version has the spec's model, instructions, tools (in order) and output schema.</summary>
    private bool Matches(AgentSpec spec, ProjectsAgentDefinition? deployed)
        => deployed is DeclarativeAgentDefinition definition
            && definition.Model == (spec.Model ?? options.DefaultModel)
            && definition.Instructions == spec.Instructions
            && definition.Tools.Select(ToolSignature).SequenceEqual(spec.Tools.Select(ToolSignature))
            && OutputFormatMatches(definition.TextOptions, spec.OutputSchema);

    /// <summary>
    /// Plain text and no format count as the same (Foundry may report either), and schemas are compared as JSON values, so
    /// whitespace or property order never causes a new version.
    /// </summary>
    private static bool OutputFormatMatches(ResponseTextOptions? deployed, AgentOutputSchema? expected)
    {
        var deployedFormat = deployed?.TextFormat;
        var deployedIsPlainText = deployedFormat is null || deployedFormat.Kind == ResponseTextFormatKind.Text;

        if (expected is null)
        {
            return deployedIsPlainText;
        }

        return !deployedIsPlainText && JsonNode.DeepEquals(
            JsonNode.Parse(ModelReaderWriter.Write(deployedFormat!).ToString()),
            JsonNode.Parse(ModelReaderWriter.Write(ToTextOptions(expected).TextFormat).ToString()));
    }

    private static string ToolSignature(ResponseTool tool) => ModelReaderWriter.Write(tool).ToString();

    /// <summary>A hash of everything that decides the version: model, instructions, tools and output schema.</summary>
    private string SpecSignature(AgentSpec spec)
    {
        var content = string.Join('\u001f', [spec.Model ?? options.DefaultModel, spec.Instructions,
            .. spec.Tools.Select(ToolSignature), spec.OutputSchema?.Name ?? string.Empty, spec.OutputSchema?.JsonSchema ?? string.Empty]);
        return "spec:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}

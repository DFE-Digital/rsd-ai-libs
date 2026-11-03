using System.Collections.Concurrent;
using GovUK.Dfe.AI.Agents.Prompts.Interfaces;
using GovUK.Dfe.AI.Agents.Providers.Interfaces;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Builders;

/// <summary>
/// Builds an <see cref="AgentDefinition"/>'s spec the same way everywhere: from its system prompt and tool
/// bindings, or from its custom provider. The definition's allowed tools and output schema apply either way. Inside a
/// snapshot (<see cref="BeginSnapshot"/>), each agent's spec is built once and reused.
/// </summary>
internal sealed class AgentSpecBuilder(IPromptProvider promptProvider, IEnumerable<AgentToolBinding>? toolBindings = null,
    IEnumerable<IManagedAgentProvider>? customProviders = null)
{
    /// <summary>The open snapshot in this async flow: each agent's one build of its spec, by name.</summary>
    private readonly AsyncLocal<Snapshot?> _snapshot = new();

    private readonly Dictionary<string, IManagedAgentProvider> _customProviders =
        (customProviders ?? []).ToDictionary(static provider => provider.AgentName);

    /// <summary>Each agent's bound tool providers, by agent name.</summary>
    public IReadOnlyDictionary<string, List<IAgentToolProvider>> ToolProviders { get; } = AgentToolResolver.GroupByAgentName(toolBindings);

    public bool IsExternallyManaged(AgentDefinition definition)
        => _customProviders.TryGetValue(definition.Name, out var provider) && !provider.CreatesAgent;

    public IManagedAgentProvider? CustomProviderFor(AgentDefinition definition)
        => _customProviders.GetValueOrDefault(definition.Name);

    /// <summary>
    /// The agent's instructions, as its prompt files give them, for evaluators. Null for an agent from a custom provider,
    /// whose instructions are its own, or when its prompt file can't be read: scoring never fails a run.
    /// </summary>
    public string? InstructionsFor(AgentDefinition definition)
    {
        if (_customProviders.ContainsKey(definition.Name))
        {
            return null;
        }

        try
        {
            return promptProvider.GetSystemPrompt(definition.SystemPromptKey);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a snapshot in this async flow until disposed: each agent's spec is built once, and every later build, for a
    /// run, a test copy or provisioning, gets that same spec, so what's provisioned is exactly what was tested. Inside an
    /// open snapshot, the outer one is used.
    /// </summary>
    public IDisposable BeginSnapshot()
    {
        if (_snapshot.Value is not null)
        {
            return NoSnapshot.Instance;
        }

        var snapshot = new Snapshot();
        _snapshot.Value = snapshot;
        return new SnapshotScope(_snapshot, snapshot);
    }

    /// <returns>The spec, or <see langword="null"/> for an externally managed agent.</returns>
    public async Task<AgentSpec?> BuildAsync(AgentDefinition definition, CancellationToken cancellationToken)
    {
        if (_snapshot.Value is not { } snapshot)
        {
            return await BuildNewAsync(definition, cancellationToken).ConfigureAwait(false);
        }

        // One build per agent for the whole snapshot: callers arriving together await the same one. Caller cancellation
        // stops only that caller's wait; closing the snapshot cancels the shared build. A failed build is dropped so the
        // next caller tries again rather than the whole run inheriting a passing fault.
        var build = snapshot.GetBuild(definition.Name,
            cancellation => Task.Run(() => BuildNewAsync(definition, cancellation), cancellation));
        try
        {
            return await build.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (build.IsFaulted || build.IsCanceled)
        {
            snapshot.Builds.TryRemove(new KeyValuePair<string, Task<AgentSpec?>>(definition.Name, build));
            throw;
        }
    }

    private async Task<AgentSpec?> BuildNewAsync(AgentDefinition definition, CancellationToken cancellationToken)
    {
        if (_customProviders.TryGetValue(definition.Name, out var provider))
        {
            var custom = await provider.BuildSpecAsync(cancellationToken).ConfigureAwait(false);
            if (custom is null)
            {
                return null;
            }

            // The agent's bindings (MCP servers, web search) apply to a custom provider's agent too: their calls run in this app.
            var bound = await AgentToolResolver.ResolveAsync(ToolProviders, definition.Name, cancellationToken).ConfigureAwait(false);
            return custom with
            {
                Tools = AgentToolResolver.FilterToAllowed(AgentToolResolver.Merge(custom.Tools, bound), definition),
                OutputSchema = custom.OutputSchema ?? definition.OutputSchema,
                Guardrail = custom.Guardrail ?? definition.Guardrail,
            };
        }

        return new AgentSpec
        {
            Name = definition.Name,
            Instructions = promptProvider.GetSystemPrompt(definition.SystemPromptKey),
            Tools = await AgentToolResolver.ResolveAsync(ToolProviders, definition, cancellationToken).ConfigureAwait(false),
            OutputSchema = definition.OutputSchema,
            Guardrail = definition.Guardrail,
        };
    }

    /// <summary>Closes the snapshot it opened.</summary>
    private sealed class Snapshot
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly CancellationToken _cancellationToken;
        private bool _closed;

        public Snapshot() => _cancellationToken = _cancellation.Token;

        public ConcurrentDictionary<string, Task<AgentSpec?>> Builds { get; } = new(StringComparer.Ordinal);

        public Task<AgentSpec?> GetBuild(string agentName, Func<CancellationToken, Task<AgentSpec?>> create)
        {
            lock (_sync)
            {
                if (_closed)
                {
                    throw new OperationCanceledException(_cancellationToken);
                }

                return Builds.GetOrAdd(agentName, _ => create(_cancellationToken));
            }
        }

        public void Cancel()
        {
            Task<AgentSpec?>[] builds;
            lock (_sync)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                builds = [.. Builds.Values];
            }

            try
            {
                _cancellation.Cancel();
            }
            finally
            {
                if (builds.Length == 0)
                {
                    _cancellation.Dispose();
                }
                else
                {
                    _ = DisposeAfterBuildsCompleteAsync(builds);
                }
            }
        }

        private async Task DisposeAfterBuildsCompleteAsync(Task<AgentSpec?>[] builds)
        {
            try
            {
                await Task.WhenAll(builds).ConfigureAwait(false);
            }
            catch
            {
                // Builds report their own errors to their waiters; disposal must still complete after they settle.
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }

    private sealed class SnapshotScope(AsyncLocal<Snapshot?> current, Snapshot snapshot) : IDisposable
    {
        public void Dispose()
        {
            current.Value = null;
            snapshot.Cancel();
        }
    }

    /// <summary>For a snapshot already open: the outer one closes it.</summary>
    private sealed class NoSnapshot : IDisposable
    {
        public static NoSnapshot Instance { get; } = new();

        public void Dispose()
        {
            // The outer snapshot's owner closes it.
        }
    }
}

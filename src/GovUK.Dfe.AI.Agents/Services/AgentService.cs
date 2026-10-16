using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GovUK.Dfe.AI.Agents.Context;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Extensions;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.ValueObjects;
using GovUK.Dfe.AI.Agents.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Providers;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Options;

namespace GovUK.Dfe.AI.Agents.Services;

internal sealed class AgentService(IAgentRunnerService agentRunner, IAgentRuntimeService agentRuntime, AgentSpecBuilder specs,
    AgentRunOptions? runOptions = null, Factories.Interfaces.IAgentFactory? agentFactory = null, ILogger<AgentService>? logger = null,
    AgentRunHooks? hooks = null)
    : IAgentService
{
    private readonly AgentRunHooks _hooks = hooks ?? AgentRunHooks.None;
    private readonly AgentRunOptions _runOptions = runOptions ?? new AgentRunOptions();
    private readonly AgentSpecBuilder _specs = specs;
    private readonly ILogger<AgentService> _logger = logger ?? NullLogger<AgentService>.Instance;
    private readonly string _applicationName = runOptions?.ApplicationName ?? AgentTelemetry.DefaultApplicationName;

    public Task<AgentResult> RunAsync(AgentDefinition definition, string prompt, AgentEvidence? evidence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return RunAgentAsync(definition, prompt, evidence, cancellationToken);
    }

    public IAsyncEnumerable<AgentStreamUpdate> RunStreamingAsync(AgentDefinition definition, string prompt, AgentEvidence? evidence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return StreamAgentAsync(definition, prompt, evidence, cancellationToken);
    }

    /// <summary>
    /// Runs the agent as <see cref="RunAsync"/> does, with the runner passing each piece of the answer to a channel that's
    /// read here, cleaned, its citations shown, and passed on. Stopping early cancels the run, which still cleans up after
    /// itself. A piece never holds part of a citation (the cleaner waits for a "[" to close), so the pieces join up to the
    /// result's <see cref="AgentResult.Output"/>.
    /// </summary>
    private async IAsyncEnumerable<AgentStreamUpdate> StreamAgentAsync(AgentDefinition definition, string prompt, AgentEvidence? evidence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pieces = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = RunWithStreamAsync();
        var cleaner = new StreamedAnswerCleaner();
        var heldSpace = string.Empty;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var firstText = true;
        AgentResult result;
        try
        {
            await foreach (var piece in pieces.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Present(cleaner.Add(piece)) is { Length: > 0 } text)
                {
                    if (firstText)
                    {
                        firstText = false;
                        AgentTelemetry.RecordTimeToFirstToken(_applicationName, definition.Name,
                            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
                    }

                    yield return new AgentStreamUpdate { Text = text };
                }
            }

            result = await run.ConfigureAwait(false);
        }
        finally
        {
            if (!run.IsCompleted)
            {
                await runCancellation.CancelAsync().ConfigureAwait(false);
                await ((Task)run).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);   // the caller stopped reading; its error is moot
            }
        }

        if (PresentCitations(definition, heldSpace + cleaner.Flush(), evidence) is { Length: > 0 } rest)
        {
            yield return new AgentStreamUpdate { Text = rest };
        }

        yield return new AgentStreamUpdate { Result = result };

        // Trailing spaces wait for the next piece: a citation removed there takes the space before it, as in the whole answer.
        string? Present(string cleaned)
        {
            var text = heldSpace + cleaned;
            var shown = text.TrimEnd(' ', '\t');
            heldSpace = text[shown.Length..];
            return PresentCitations(definition, shown, evidence);
        }

        async Task<AgentResult> RunWithStreamAsync()
        {
            // Set inside this method, so only this run streams: the value doesn't flow back to the caller.
            AnswerStream.Current = piece => pieces.Writer.TryWrite(piece);
            try
            {
                return await RunAgentAsync(definition, prompt, evidence, runCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                pieces.Writer.Complete();
            }
        }
    }

    public async Task<IReadOnlyList<AgentResult>> RunParallelAsync(IReadOnlyCollection<AgentDefinition> definitions,
        Func<AgentDefinition, CancellationToken, Task<string>> resolvePrompt, AgentContext context,
        Func<Exception, bool>? shouldSuppress = null,
        Func<AgentDefinition, CancellationToken, Task<AgentEvidence?>>? resolveEvidence = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(resolvePrompt);
        ArgumentNullException.ThrowIfNull(context);

        using var workflow = AgentTelemetry.StartWorkflow(_applicationName, AgentTelemetry.ParallelMode, definitions.Count);

        // Concurrency limits apply in the runner, to every run on the instance (and globally when configured).
        // A failure that isn't suppressed cancels the other agents straight away rather than letting
        // them run - and bill - to completion for a result that will be thrown away.
        var steps = await FailFastParallel.WhenAllAsync(definitions.Select(definition =>
                (Func<CancellationToken, Task<AgentStepResult>>)(token =>
                    RunStepAsync(definition, async ct =>
                        (await resolvePrompt(definition, ct).ConfigureAwait(false),
                         resolveEvidence is null ? null : await resolveEvidence(definition, ct).ConfigureAwait(false)),
                        shouldSuppress, token))),
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<AgentResult> results = [.. steps.Select(step => step.ToAgentResult())];
        CompleteWorkflow(workflow, results, steps.Count(step => !step.Succeeded));
        return results;
    }

    public async Task<IReadOnlyList<AgentResult>> RunSequentialAsync(IReadOnlyList<AgentDefinition> definitions,
        Func<AgentDefinition, CancellationToken, Task<string>> resolvePrompt, string initialInput,
        AgentContext context, Func<Exception, bool>? shouldSuppress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(resolvePrompt);
        ArgumentNullException.ThrowIfNull(context);

        using var workflow = AgentTelemetry.StartWorkflow(_applicationName, AgentTelemetry.SequentialMode, definitions.Count);

        var results = new List<AgentResult>(definitions.Count);
        var previousOutput = initialInput;
        var failedAgents = 0;

        foreach (var definition in definitions)
        {
            string? prompt = null;
            var input = previousOutput;

            // The previous output goes in as fenced evidence, never as part of the prompt: whatever
            // influenced it (e.g. injected text in search results) can't become an instruction here.
            var step = await RunStepAsync(definition,
                async ct =>
                {
                    prompt = await resolvePrompt(definition, ct).ConfigureAwait(false);
                    return (prompt, input);
                },
                shouldSuppress, cancellationToken).ConfigureAwait(false);

            var result = step.ToAgentResult();
            results.Add(result);

            if (step.Succeeded)
            {
                context.AddHistory(new AgentContextEntry(definition.Name, prompt ?? string.Empty, result.Output));
                previousOutput = result.Output ?? previousOutput;
            }
            else
            {
                failedAgents++;
            }
        }

        CompleteWorkflow(workflow, results, failedAgents);
        return results;
    }

    public async Task<IReadOnlyList<AgentReference>> ProvisionAsync(IReadOnlyCollection<AgentDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var factory = agentFactory ?? throw new InvalidOperationException(Constants.ErrorMessages.ProvisioningNeedsFactory);

        var provisioned = new List<AgentReference>(definitions.Count);

        // One at a time: a provisioning job isn't latency-sensitive, and it keeps Foundry calls gentle.
        foreach (var definition in definitions)
        {
            if (!definition.IsManagedAgent)
            {
                _logger.SkippingEphemeralAgent(definition.Name);
                continue;
            }

            var spec = await _specs.BuildAsync(definition, cancellationToken).ConfigureAwait(false);
            if (spec is null)
            {
                _logger.SkippingExternallyManagedAgent(definition.Name);
                continue;
            }

            var agent = await factory.GetOrCreateAsync(spec, cancellationToken).ConfigureAwait(false);
            _logger.ProvisionedAgent(agent.Name, agent.Version);
            provisioned.Add(agent);
        }

        return provisioned;
    }

    /// <summary>
    /// Runs one agent - managed or ephemeral - and records a failure as a step result rather than
    /// throwing, unless <paramref name="shouldSuppress"/> says otherwise.
    /// </summary>
    private Task<AgentStepResult> RunStepAsync(AgentDefinition definition,
        Func<CancellationToken, Task<(string Prompt, AgentEvidence? Evidence)>> resolveInput,
        Func<Exception, bool>? shouldSuppress, CancellationToken cancellationToken)
        => ResilientAgentStep.ExecuteAsync(
            step: async () =>
            {
                var (prompt, evidence) = await resolveInput(cancellationToken).ConfigureAwait(false);
                var result = await RunAgentAsync(definition, prompt, evidence, cancellationToken).ConfigureAwait(false);
                return new AgentStepResult(definition.Name, result, Error: null);
            },
            fallback: ex =>
            {
                _logger.LogError(ex, "Agent {AgentName} failed; recording failure and continuing", definition.Name);
                return new AgentStepResult(definition.Name, Result: null, ex);
            },
            shouldSuppress: shouldSuppress);

    private async Task<AgentResult> RunAgentAsync(AgentDefinition definition, string prompt, AgentEvidence? evidence,
        CancellationToken cancellationToken)
    {
        var evidenceText = evidence?.Text;
        // Tools bound to this agent that run in this app (e.g. MCP) execute the model's calls here,
        // limited to the definition's AllowedTools.
        var resolveToolCalls = AgentToolResolver.CreateToolCallResolver(_specs.ToolProviders, definition, _applicationName, _hooks.ToolApprover, _logger);
        var (runPrompt, validate) = Citations.ForRun(definition, prompt, evidenceText);

        AgentResult result;
        if (!definition.IsManagedAgent)
        {
            var spec = await _specs.BuildAsync(definition, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(string.Format(Constants.ErrorMessages.EphemeralAgentNeedsSpec, definition.Name));
            result = await agentRuntime.RunEphemeralAsync(spec, runPrompt, resolveToolCalls, evidenceText, validate, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var agent = _specs.IsExternallyManaged(definition)
                ? await _specs.CustomProviderFor(definition)!.GetAgentAsync(cancellationToken).ConfigureAwait(false)
                : await agentRuntime.GetOrCreateAsync(definition.Name,
                    async ct => (await _specs.BuildAsync(definition, ct).ConfigureAwait(false))!, cancellationToken).ConfigureAwait(false);

            // An agent from another Foundry project runs there.
            var runner = _specs.CustomProviderFor(definition) is ExternalProjectAgentProvider external ? external.Project.Runner : agentRunner;
            result = await runner.RunAsync(agent, runPrompt, additionalContext: evidenceText, resolveToolCalls: resolveToolCalls,
                cancellationToken: cancellationToken, validateOutput: validate).ConfigureAwait(false);
        }

        if (_hooks.Observers.Count > 0)
        {
            // Observers (scoring, audit) get what the model was given and wrote: redacted, so no copy keeps data the model never
            // saw, and with citations as [Evidence n], so they still match the evidence.
            Notify(new CompletedAgentRun
            {
                AgentName = definition.Name, RunId = result.RunId, CompletedAt = result.CompletedAt, AgentVersion = result.AgentVersion,
                Model = result.Model, Prompt = await _runOptions.RedactAsync(prompt, cancellationToken).ConfigureAwait(false),
                Evidence = evidenceText is null ? null : await _runOptions.RedactAsync(evidenceText, cancellationToken).ConfigureAwait(false),
                Output = result.Output ?? string.Empty,
            });
        }

        // Only now, with the answer checked: every [Evidence n] left refers to evidence that exists.
        return result with { Output = PresentCitations(definition, result.Output, evidence) };
    }

    /// <summary>
    /// The answer's citations as the agent shows them: each <c>[Evidence n]</c> as a link to its source or its text, or, for an
    /// agent with <see cref="AgentDefinition.RequiredCitations"/> off, removed. Without sources, shown citations stay as written.
    /// </summary>
    private static string? PresentCitations(AgentDefinition definition, string? answer, AgentEvidence? evidence)
    {
        if (!definition.RequiredCitations)
        {
            return Citations.Remove(answer);
        }

        return evidence is { Sources.Count: > 0 } ? Citations.Render(answer, evidence.Sources) : answer;
    }

    /// <summary>Records what the whole run used and cost, e.g. one briefing.</summary>
    private void CompleteWorkflow(WorkflowTelemetry workflow, IEnumerable<AgentResult> results, int failedAgents)
    {
        var summary = results.ToTokenUsageSummary();
        workflow.Completed(summary.Total, failedAgents, summary.Cost, _runOptions.Pricing.Currency);
    }

    /// <summary>Tells each observer about a finished run; an observer's failure is logged, never the run's.</summary>
    private void Notify(CompletedAgentRun run)
    {
        foreach (var observer in _hooks.Observers)
        {
            try
            {
                observer.OnRunCompleted(run);
            }
            catch (Exception ex)
            {
                _logger.RunObserverFailed(ex, observer.GetType().Name, run.AgentName);
            }
        }
    }
}

using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using System.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Tools;

/// <summary>
/// Builds the <c>resolveToolCalls</c> callback that <see cref="Core.Interfaces.IAgentRunnerService"/> uses to
/// run an agent's function tool calls in this app. <c>IAgentService</c> does this for you from
/// <see cref="AgentToolBinding"/>s; call it yourself when using <c>IAgentRunnerService</c> directly.
/// </summary>
public static class AgentToolExecution
{
    /// <summary>Creates a callback that runs each call on the first of <paramref name="providers"/> that owns it.</summary>
    /// <param name="providers">The agent's tool providers. Only those implementing <see cref="IAgentToolExecutor"/> run calls.</param>
    /// <param name="allowedTools">
    /// The tool names this agent may call. A call to any other tool is refused, even if a provider could
    /// run it. <see langword="null"/> allows every tool the providers run.
    /// </param>
    /// <returns>The callback, or <see langword="null"/> when none of the providers runs tools in this app.</returns>
    public static ToolCallResolver? CreateResolver(
        IEnumerable<IAgentToolProvider> providers, IReadOnlyCollection<string>? allowedTools = null)
        => CreateResolver(providers, allowedTools, applicationName: null, agentName: null);

    /// <summary>As above, recording an <c>execute_tool</c> span and duration for each call, tagged with the application and agent.</summary>
    internal static ToolCallResolver? CreateResolver(IEnumerable<IAgentToolProvider> providers, IReadOnlyCollection<string>? allowedTools,
        string? applicationName, string? agentName)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var executors = providers.OfType<IAgentToolExecutor>().ToList();
        if (executors.Count == 0)
        {
            return null;
        }

        var allowed = allowedTools?.ToHashSet(StringComparer.Ordinal);
        return async (calls, cancellationToken) =>
            await Task.WhenAll(calls.Select(call => ExecuteMeasuredAsync(executors, allowed, call, applicationName, agentName, cancellationToken)))
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one call inside an <c>execute_tool</c> span, and records <c>gen_ai.execute_tool.duration</c>. Only the tool's name,
    /// the call's id and a failure's exception type are recorded: arguments and output can carry personal data.
    /// </summary>
    private static async Task<ToolCallOutput> ExecuteMeasuredAsync(IReadOnlyList<IAgentToolExecutor> executors, HashSet<string>? allowed,
        ToolCallRequest call, string? applicationName, string? agentName, CancellationToken cancellationToken)
    {
        var tags = new TagList
        {
            { AgentTelemetry.ToolNameTag, call.FunctionName },
            { AgentTelemetry.ToolTypeTag, "function" },
        };
        if (agentName is not null)
        {
            tags.Add(AgentTelemetry.AgentNameTag, AgentTelemetry.AgentNameForTelemetry(agentName));
        }

        if (applicationName is not null)
        {
            tags.Add(AgentTelemetry.ApplicationTag, applicationName);
        }

        using var activity = AgentTelemetry.ActivitySource.StartActivity($"{AgentTelemetry.ExecuteToolOperation} {call.FunctionName}",
            ActivityKind.Internal, parentContext: default, tags: [new(AgentTelemetry.OperationNameTag, AgentTelemetry.ExecuteToolOperation), .. tags]);
        activity?.SetTag(AgentTelemetry.ToolCallIdTag, call.CallId);
        var started = Stopwatch.GetTimestamp();

        try
        {
            return await ExecuteAsync(executors, allowed, call, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var errorType = ex is OperationCanceledException ? "cancelled" : ex.GetType().FullName ?? AgentTelemetry.OtherError;
            tags.Add(AgentTelemetry.ErrorTypeTag, errorType);
            activity?.SetTag(AgentTelemetry.ErrorTypeTag, errorType);
            activity?.SetStatus(ActivityStatusCode.Error, "The tool call failed.");
            throw;
        }
        finally
        {
            AgentTelemetry.ExecuteToolDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
    }

    private static async Task<ToolCallOutput> ExecuteAsync(IReadOnlyList<IAgentToolExecutor> executors, HashSet<string>? allowed,
        ToolCallRequest call, CancellationToken cancellationToken)
    {
        if (allowed is not null && !allowed.Contains(call.FunctionName))
        {
            // The model asked for a tool this agent wasn't given - e.g. prompted by injected text.
            throw new InvalidOperationException(string.Format(ErrorMessages.ToolNotAllowedForAgent, call.FunctionName));
        }

        foreach (var executor in executors)
        {
            var output = await executor.TryExecuteAsync(call, cancellationToken).ConfigureAwait(false);
            if (output is not null)
            {
                return new ToolCallOutput(call.CallId, output);
            }
        }

        throw new InvalidOperationException(string.Format(ErrorMessages.NoToolExecutor, call.FunctionName));
    }
}

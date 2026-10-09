using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Tools;

/// <summary>
/// Executes tool calls on the first provider that owns them, and records telemetry for each call. Only providers implementing <see cref="IAgentToolExecutor"/> run calls.
/// </summary>
internal static class AgentToolExecution
{
    /// <summary>
    /// Creates a <see cref="ToolCallResolver"/> that executes tool calls on the first provider that owns them, and records telemetry for each call. Only providers implementing <see cref="IAgentToolExecutor"/> run calls.
    /// </summary>
    /// <param name="providers">The agent's tool providers. Only those implementing <see cref="IAgentToolExecutor"/> run calls.</param>
    /// <param name="allowedTools">The tool names this agent may call. A call to any other tool is refused, even if a provider could run it. <see langword="null"/> allows every tool the providers run.</param>
    /// <returns>The callback, or <see langword="null"/> when none of the providers runs tools in this app.</returns>
    public static ToolCallResolver? CreateResolver(
        IEnumerable<IAgentToolProvider> providers, IReadOnlyCollection<string>? allowedTools = null)
        => CreateResolver(providers, allowedTools, applicationName: null, agentName: null);

    /// <summary>
    /// As above, recording an <c>execute_tool</c> span and duration for each call, tagged with the application and agent, and
    /// asking <paramref name="approver"/> before each call to a tool in <paramref name="toolsRequiringApproval"/>.
    /// </summary>
    internal static ToolCallResolver? CreateResolver(IEnumerable<IAgentToolProvider> providers, IReadOnlyCollection<string>? allowedTools,
        string? applicationName, string? agentName, IReadOnlyCollection<string>? toolsRequiringApproval = null,
        IToolCallApprover? approver = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var executors = providers.OfType<IAgentToolExecutor>().ToList();
        if (executors.Count == 0)
        {
            return null;
        }

        var allowed = allowedTools?.ToHashSet(StringComparer.Ordinal);
        var approval = new Approval(toolsRequiringApproval?.ToHashSet(StringComparer.Ordinal) ?? [], approver, logger);
        return async (calls, cancellationToken) =>
            await Task.WhenAll(calls.Select(call => ExecuteMeasuredAsync(executors, allowed, approval, call, applicationName, agentName, cancellationToken)))
                .ConfigureAwait(false);
    }

    /// <summary>Which tools need approval, and who gives it.</summary>
    private sealed record Approval(HashSet<string> Tools, IToolCallApprover? Approver, ILogger? Logger);

    /// <summary>
    /// Runs one call inside an <c>execute_tool</c> span, and records <c>gen_ai.execute_tool.duration</c>. Only the tool's name,
    /// the call's id and a failure's exception type are recorded: arguments and output can carry personal data.
    /// </summary>
    private static async Task<ToolCallOutput> ExecuteMeasuredAsync(IReadOnlyList<IAgentToolExecutor> executors, HashSet<string>? allowed,
        Approval approval, ToolCallRequest call, string? applicationName, string? agentName, CancellationToken cancellationToken)
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
            if (approval.Tools.Contains(call.FunctionName)
                && await ApproveAsync(approval, call, applicationName, agentName, cancellationToken).ConfigureAwait(false) is { } denied)
            {
                activity?.SetTag(AgentTelemetry.ToolApprovedTag, false);
                return denied;
            }

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

    /// <summary>Null when the call may run; otherwise the output telling the agent it wasn't approved.</summary>
    private static async Task<ToolCallOutput?> ApproveAsync(Approval approval, ToolCallRequest call, string? applicationName, string? agentName,
        CancellationToken cancellationToken)
    {
        // Startup refuses ToolsRequiringApproval without an approver; refusing here too means a gap can never run the tool.
        var decision = approval.Approver is null
            ? ToolCallApproval.Deny("no approver is set up")
            : await approval.Approver.ApproveAsync(agentName ?? string.Empty, call, cancellationToken).ConfigureAwait(false);

        AgentTelemetry.RecordToolApproval(applicationName, agentName, call.FunctionName, decision.Approved);
        if (decision.Approved)
        {
            return null;
        }

        approval.Logger?.ToolCallDenied(call.FunctionName, agentName ?? "unknown");
        return new ToolCallOutput(call.CallId, string.Format(PromptText.ToolCallNotApproved, call.FunctionName, decision.Reason));
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

using GovUK.Dfe.AI.Agents.ValueObjects;
using System.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// The <c>invoke_workflow</c> span and metrics for one parallel or sequential run of several agents, e.g. one briefing.
/// Call <see cref="Completed"/> when it finishes; disposing without it records the run as failed.
/// </summary>
internal sealed class WorkflowTelemetry : IDisposable
{
    private readonly Activity? _activity;
    private readonly KeyValuePair<string, object?> _application;
    private readonly KeyValuePair<string, object?> _mode;
    private readonly long _started = Stopwatch.GetTimestamp();
    private bool _completed;

    public WorkflowTelemetry(string applicationName, string mode, int agentCount)
    {
        _application = new(AgentTelemetry.ApplicationTag, applicationName);
        _mode = new(AgentTelemetry.WorkflowModeTag, mode);
        _activity = AgentTelemetry.ActivitySource.StartActivity(AgentTelemetry.InvokeWorkflowOperation, ActivityKind.Internal,
            parentContext: default, tags:
            [
                new(AgentTelemetry.OperationNameTag, AgentTelemetry.InvokeWorkflowOperation),
                _mode,
                new(AgentTelemetry.WorkflowAgentCountTag, agentCount),
                _application,
            ]);
    }

    /// <summary>Records what the whole run used, so the cost of one briefing is a single number as well as per agent.</summary>
    public void Completed(TokenUsage usage, int failedAgents)
    {
        _completed = true;
        _activity?.SetTag(AgentTelemetry.InputTokensTag, usage.InputTokens);
        _activity?.SetTag(AgentTelemetry.OutputTokensTag, usage.OutputTokens);
        _activity?.SetTag(AgentTelemetry.WorkflowFailedAgentCountTag, failedAgents);

        AgentTelemetry.WorkflowInputTokens.Record(usage.InputTokens, _mode, _application);
        AgentTelemetry.WorkflowOutputTokens.Record(usage.OutputTokens, _mode, _application);
    }

    public void Dispose()
    {
        var tags = new TagList { _mode, _application };
        if (!_completed)
        {
            // An agent's failure was thrown rather than recorded as a fallback, or the run was cancelled.
            tags.Add(AgentTelemetry.ErrorTypeTag, AgentTelemetry.OtherError);
            _activity?.SetTag(AgentTelemetry.ErrorTypeTag, AgentTelemetry.OtherError);
            _activity?.SetStatus(ActivityStatusCode.Error, "The run of several agents didn't complete.");
        }

        AgentTelemetry.InvokeWorkflowDuration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, tags);
        _activity?.Dispose();
    }
}

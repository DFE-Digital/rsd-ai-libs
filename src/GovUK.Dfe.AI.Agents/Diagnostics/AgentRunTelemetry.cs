using GovUK.Dfe.AI.Agents.ValueObjects;
using System.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// The <c>invoke_agent</c> span and metrics for one agent run: start it, set the outcome, then <see cref="Record"/> once.
/// </summary>
internal sealed class AgentRunTelemetry : IDisposable
{
    private readonly Activity? _activity;
    private readonly KeyValuePair<string, object?> _application;
    private readonly KeyValuePair<string, object?> _agent;
    private readonly long _started = Stopwatch.GetTimestamp();
    private string? _errorType;
    private string? _model;

    public AgentRunTelemetry(string applicationName, AgentReference agent)
    {
        // Ephemeral agents are reported under their base name, so a run's metrics group with its agent.
        var agentName = AgentTelemetry.AgentNameForTelemetry(agent.Name);
        _application = new(AgentTelemetry.ApplicationTag, applicationName);
        _agent = new(AgentTelemetry.AgentNameTag, agentName);

        // Set when the span starts, so samplers can use them.
        _activity = AgentTelemetry.ActivitySource.StartActivity($"{AgentTelemetry.InvokeAgentOperation} {agentName}", ActivityKind.Client,
            parentContext: default, tags:
            [
                new(AgentTelemetry.OperationNameTag, AgentTelemetry.InvokeAgentOperation),
                new(AgentTelemetry.ProviderNameTag, AgentTelemetry.ProviderName),
                _agent,
                new(AgentTelemetry.AgentIdTag, agent.Id),
                new(AgentTelemetry.AgentVersionTag, agent.Version),
                _application,
            ]);
    }

    public void InConversation(string conversationId) => _activity?.SetTag(AgentTelemetry.ConversationIdTag, conversationId);

    /// <summary>The run's id, so an answer, its feedback and its audit record can be found in traces.</summary>
    public void Identified(string runId) => _activity?.SetTag(AgentTelemetry.RunIdTag, runId);

    /// <summary>The model that answered, so quality and cost can be tied to model changes.</summary>
    public void AnsweredBy(string? model)
    {
        _model = model;
        _activity?.SetTag(AgentTelemetry.ResponseModelTag, model);
    }

    /// <summary>The caller cancelled: not an error in the span, but kept apart from successful runs in the metrics.</summary>
    public void Cancelled() => _errorType = "cancelled";

    /// <summary>
    /// Marks the run failed. Only a fixed description and a low-cardinality <paramref name="errorType"/> (an error code or
    /// exception type) reach telemetry: exception messages can echo prompts, evidence or tool output, so they stay in the logs.
    /// </summary>
    public void Failed(string errorType, string description)
    {
        _errorType = errorType;
        _activity?.SetStatus(ActivityStatusCode.Error, description);
    }

    /// <summary>Records tokens, duration and call counts, whatever the outcome: every response Foundry returned is billed.</summary>
    public void Record(TokenUsage usage, int inferenceCalls, int toolCalls, decimal? cost = null, string? currency = null)
    {
        KeyValuePair<string, object?> model = new(AgentTelemetry.ResponseModelTag, _model);
        KeyValuePair<string, object?> operation = new(AgentTelemetry.OperationNameTag, AgentTelemetry.InvokeAgentOperation);
        KeyValuePair<string, object?> provider = new(AgentTelemetry.ProviderNameTag, AgentTelemetry.ProviderName);
        KeyValuePair<string, object?> text = new(AgentTelemetry.TokenModalityTag, "text");   // the library sends and receives text only

        AgentTelemetry.InputTokens.Add(usage.InputTokens, operation, provider, text, _agent, model, _application);
        AgentTelemetry.OutputTokens.Add(usage.OutputTokens, operation, provider, text, _agent, model, _application);

        var duration = new TagList { _agent, model, _application };
        if (_errorType is not null)
        {
            duration.Add(AgentTelemetry.ErrorTypeTag, _errorType);
            _activity?.SetTag(AgentTelemetry.ErrorTypeTag, _errorType);
        }

        AgentTelemetry.InvokeAgentDuration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, duration);
        AgentTelemetry.InvokeAgentInferenceCalls.Record(inferenceCalls, _agent, _application);
        AgentTelemetry.InvokeAgentToolCalls.Record(toolCalls, _agent, _application);
        if (cost is { } value)
        {
            AgentTelemetry.Cost.Add((double)value, _agent, model, _application, new(AgentTelemetry.CurrencyTag, currency));
            _activity?.SetTag(AgentTelemetry.CostDataKey, (double)value);
        }

        _activity?.SetTag(AgentTelemetry.InputTokensTag, usage.InputTokens);
        _activity?.SetTag(AgentTelemetry.OutputTokensTag, usage.OutputTokens);
    }

    public void Dispose() => _activity?.Dispose();
}

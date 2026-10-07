using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// The library's trace and metric sources. Nothing is sent anywhere until the application subscribes,
/// e.g. with OpenTelemetry: <c>.WithTracing(t =&gt; t.AddSource(AgentTelemetry.SourceName))</c> and
/// <c>.WithMetrics(m =&gt; m.AddMeter(AgentTelemetry.SourceName))</c>.
/// </summary>
/// <remarks>
/// Names follow the OpenTelemetry generative AI semantic conventions (<c>gen_ai.*</c>) wherever they define one, so
/// standard GenAI dashboards and tools understand them. Signals the conventions don't cover use the
/// <c>dfe.ai_agents.*</c> namespace. Every span and measurement is also tagged with <see cref="ApplicationTag"/>
/// (<c>AiAgents:ApplicationName</c>), so usage can be split by application when several share a Foundry project.
/// </remarks>
public static class AgentTelemetry
{
    /// <summary>The name of both the <see cref="ActivitySource"/> and the <see cref="Meter"/>.</summary>
    public const string SourceName = "GovUK.Dfe.AI.Agents";

    /// <summary>The tag naming the application that ran the agent, on every span and measurement.</summary>
    public const string ApplicationTag = "dfe.ai_agents.application";

    // ===== OpenTelemetry GenAI semantic convention names =====

    internal const string OperationNameTag = "gen_ai.operation.name";
    internal const string ProviderNameTag = "gen_ai.provider.name";
    internal const string AgentNameTag = "gen_ai.agent.name";
    internal const string AgentIdTag = "gen_ai.agent.id";
    internal const string AgentVersionTag = "gen_ai.agent.version";
    internal const string ConversationIdTag = "gen_ai.conversation.id";
    internal const string ResponseModelTag = "gen_ai.response.model";
    internal const string TokenModalityTag = "gen_ai.token.modality";
    internal const string InputTokensTag = "gen_ai.usage.input_tokens";
    internal const string OutputTokensTag = "gen_ai.usage.output_tokens";
    internal const string ToolNameTag = "gen_ai.tool.name";
    internal const string ToolCallIdTag = "gen_ai.tool.call.id";
    internal const string ToolTypeTag = "gen_ai.tool.type";
    internal const string ErrorTypeTag = "error.type";

    internal const string InvokeAgentOperation = "invoke_agent";
    internal const string InvokeWorkflowOperation = "invoke_workflow";
    internal const string ExecuteToolOperation = "execute_tool";

    /// <summary>Foundry agents run through Azure's OpenAI-compatible Responses API.</summary>
    internal const string ProviderName = "azure.ai.openai";

    /// <summary>The error type the conventions reserve for an error with no more specific identifier.</summary>
    internal const string OtherError = "_OTHER";

    private const string TokenUnit = "{token}";

    // ===== This library's own names =====

    internal const string WorkflowModeTag = "dfe.ai_agents.workflow.mode";
    internal const string WorkflowAgentCountTag = "dfe.ai_agents.workflow.agent_count";
    internal const string WorkflowFailedAgentCountTag = "dfe.ai_agents.workflow.failed_agent_count";
    internal const string GuardrailStageTag = "dfe.ai_agents.guardrail.stage";

    internal const string ParallelMode = "parallel";
    internal const string SequentialMode = "sequential";

    internal static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    // ===== Metrics from the GenAI conventions, with their recommended buckets =====

    /// <summary>Input tokens Foundry billed, per agent run, including tool rounds and failed runs.</summary>
    internal static readonly Counter<long> InputTokens = Meter.CreateCounter<long>(
        "gen_ai.client.inference.usage.input_tokens", TokenUnit, "The number of input (prompt) tokens used, including cached tokens.");

    /// <summary>Output tokens Foundry billed, per agent run, including reasoning, tool rounds and failed runs.</summary>
    internal static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>(
        "gen_ai.client.inference.usage.output_tokens", TokenUnit, "The number of output (completion) tokens used.");

    internal static readonly Histogram<double> InvokeAgentDuration = Meter.CreateHistogram("gen_ai.invoke_agent.duration", "s",
        "The end-to-end duration of a single agent invocation.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.1, 0.2, 0.4, 0.8, 1.6, 3.2, 6.4, 12.8, 25.6, 51.2, 102.4, 204.8, 409.6] });

    internal static readonly Histogram<int> InvokeAgentInferenceCalls = Meter.CreateHistogram("gen_ai.invoke_agent.inference_calls",
        "{inference_call}", "The number of inference (model) calls an agent makes during a single invocation.",
        advice: new InstrumentAdvice<int> { HistogramBucketBoundaries = [1, 2, 4, 8, 16, 32, 64, 128] });

    internal static readonly Histogram<int> InvokeAgentToolCalls = Meter.CreateHistogram("gen_ai.invoke_agent.tool_calls",
        "{tool_call}", "The number of tool calls an agent makes during a single invocation.",
        advice: new InstrumentAdvice<int> { HistogramBucketBoundaries = [1, 2, 4, 8, 16, 32, 64, 128] });

    internal static readonly Histogram<double> ExecuteToolDuration = Meter.CreateHistogram("gen_ai.execute_tool.duration", "s",
        "The duration of a single tool execution.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92] });

    internal static readonly Histogram<double> InvokeWorkflowDuration = Meter.CreateHistogram("gen_ai.invoke_workflow.duration", "s",
        "The end-to-end duration of a parallel or sequential run of several agents.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [1, 5, 10, 30, 60, 120, 300, 600, 1800, 3600, 7200] });

    // ===== This library's own metrics =====

    internal const string TokenTypeTag = "gen_ai.token.type";

    /// <summary>Tokens one parallel or sequential run used in total, e.g. one whole briefing, by <c>gen_ai.token.type</c>.</summary>
    internal static readonly Histogram<long> WorkflowTokens = Meter.CreateHistogram<long>(
        "dfe.ai_agents.workflow.tokens", TokenUnit, "Tokens used by one parallel or sequential run of several agents, input or output.");

    /// <summary>The deployment's remaining tokens in its 60-second window, from each Foundry response.</summary>
    internal static readonly Histogram<long> TokensRemaining = Meter.CreateHistogram<long>(
        "dfe.ai_agents.tokens.remaining", TokenUnit, "The model deployment's remaining tokens per minute, from each Foundry response.");

    /// <summary>Responses where the remaining tokens were below the app's LowRemainingTokensPercent of the limit.</summary>
    internal static readonly Counter<long> TokensLow = Meter.CreateCounter<long>(
        "dfe.ai_agents.tokens.low", "{response}", "Foundry responses where the deployment's remaining tokens were below the app's threshold.");

    internal static readonly Histogram<double> RunSlotWaitDuration = Meter.CreateHistogram<double>(
        "dfe.ai_agents.run_slot.wait.duration", "s", "Time agent runs waited for a free run slot.");

    /// <summary>Prompts and answers a Foundry guardrail blocked, by stage.</summary>
    internal static readonly Counter<long> GuardrailBlocks = Meter.CreateCounter<long>(
        "dfe.ai_agents.guardrail.blocks", "{block}", "Prompts and answers a Foundry guardrail blocked.");

    internal const string RunIdTag = "dfe.ai_agents.run_id";
    internal const string CurrencyTag = "dfe.ai_agents.currency";
    internal const string CostDataKey = "dfe.ai_agents.cost";

    internal static readonly Counter<double> Cost = Meter.CreateCounter<double>(
        "dfe.ai_agents.cost", "{currency}", "What agent runs cost, from AiAgents:Pricing, by agent, model and currency. Failed runs included.");

    internal static readonly Histogram<double> WorkflowCost = Meter.CreateHistogram<double>(
        "dfe.ai_agents.workflow.cost", "{currency}", "What one parallel or sequential run of several agents cost, e.g. one briefing.");

    /// <summary>The cost a failed run had reached, attached to its exception (or one it wraps); null if unknown.</summary>
    internal static decimal? CostOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data[CostDataKey] is decimal cost)
            {
                return cost;
            }
        }

        return null;
    }
    internal const string ToolApprovedTag = "dfe.ai_agents.tool.approved";

    internal static readonly Counter<long> ToolApprovals = Meter.CreateCounter<long>(
        "dfe.ai_agents.tool.approvals", "{call}", "Tool calls that needed approval, by tool and whether they were approved.");

    internal static void RecordToolApproval(string? applicationName, string? agentName, string toolName, bool approved)
        => ToolApprovals.Add(1,
            new KeyValuePair<string, object?>(ApplicationTag, applicationName),
            new KeyValuePair<string, object?>(AgentNameTag, agentName is null ? null : AgentNameForTelemetry(agentName)),
            new KeyValuePair<string, object?>(ToolNameTag, toolName),
            new KeyValuePair<string, object?>(ToolApprovedTag, approved));

    internal static void RecordGuardrailBlock(string applicationName, string agentName, string stage)
        => GuardrailBlocks.Add(1,
            new KeyValuePair<string, object?>(ApplicationTag, applicationName),
            new KeyValuePair<string, object?>(AgentNameTag, AgentNameForTelemetry(agentName)),
            new KeyValuePair<string, object?>(GuardrailStageTag, stage));

    /// <summary>
    /// Whether anything - normally the app's OpenTelemetry <c>MeterProvider</c> - is listening to the
    /// token usage metrics. <see langword="false"/> means token usage would be measured and then dropped.
    /// </summary>
    public static bool IsTokenUsageRecorded => InputTokens.Enabled && OutputTokens.Enabled;

    /// <summary>Used when no application name is configured: the name of the app's entry assembly.</summary>
    internal static string DefaultApplicationName => Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";

    /// <summary>The <see cref="Exception.Data"/> key a failed run stores its token usage under.</summary>
    public const string TokenUsageDataKey = "dfe.ai_agents.token_usage";

    /// <summary>The tokens a failed run used before it failed, read from the exception it threw (or one it wraps).</summary>
    public static ValueObjects.TokenUsage TokenUsageOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data[TokenUsageDataKey] is ValueObjects.TokenUsage usage)
            {
                return usage;
            }
        }

        return ValueObjects.TokenUsage.None;
    }

    /// <summary>Starts the telemetry for one parallel or sequential run of several agents (e.g. one briefing).</summary>
    internal static WorkflowTelemetry StartWorkflow(string applicationName, string mode, int agentCount)
        => new(applicationName, mode, agentCount);

    /// <summary>
    /// The agent name to tag telemetry with. Ephemeral agents drop their per-run GUID suffix, so every
    /// run of "web-search-agent" is counted together rather than creating a new metric series each time.
    /// </summary>
    internal static string AgentNameForTelemetry(string agentName)
        => Services.AgentRuntimeService.IsEphemeralName(agentName) ? agentName[..^33] : agentName;
}

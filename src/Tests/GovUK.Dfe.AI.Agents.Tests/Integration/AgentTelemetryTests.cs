using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using Azure.AI.Projects.Agents;
using Azure.Core;
using GovUK.Dfe.AI.Agents.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Context;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Extensions;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Integration;

/// <summary>
/// Listens to the library's real ActivitySource and Meter while agents run through its DI wiring.
/// Each test uses its own application name, so measurements from tests running in parallel never mix.
/// </summary>
public sealed class AgentTelemetryTests : IDisposable
{
    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private readonly string _application = $"telemetry-test-{Guid.NewGuid():N}";
    private readonly string _promptDirectory = Directory.CreateTempSubdirectory("aiagents-telemetry-").FullName;
    private readonly InMemoryFoundry _foundry = new();
    private readonly ScriptedConversationClient _conversations = new();
    private readonly Dictionary<string, string?> _configuration = [];
    private readonly ConcurrentQueue<Measurement> _measurements = new();
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly MeterListener _meterListener = new();
    private readonly ActivityListener _activityListener;

    public AgentTelemetryTests()
    {
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgentTelemetry.SourceName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meterListener.Start();

        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem(AgentTelemetry.ApplicationTag), _application))
                {
                    _activities.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose()
    {
        _meterListener.Dispose();
        _activityListener.Dispose();
        Directory.Delete(_promptDirectory, recursive: true);
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var tagMap = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            tagMap[tag.Key] = tag.Value;
        }

        if (Equals(tagMap.GetValueOrDefault(AgentTelemetry.ApplicationTag), _application))
        {
            _measurements.Enqueue(new Measurement(instrument.Name, value, tagMap));
        }
    }

    private IReadOnlyList<Measurement> MeasurementsOf(string instrument) => [.. _measurements.Where(m => m.Instrument == instrument)];

    /// <summary>The one workflow token measurement of <paramref name="type"/>: "input" or "output".</summary>
    private Measurement WorkflowTokens(string type)
        => Assert.Single(MeasurementsOf("dfe.ai_agents.workflow.tokens"), m => Equals(m.Tags["gen_ai.token.type"], type));

    private IReadOnlyList<Activity> SpansOf(string operation)
        => [.. _activities.Where(activity => Equals(activity.GetTagItem(AgentTelemetry.OperationNameTag), operation))];

    private void WriteSystemPrompt(string promptType, string content)
    {
        var path = Path.Combine(_promptDirectory, $"{promptType}.md");
        File.WriteAllText(path, content);
        _configuration[$"AiAgents:PromptFiles:SystemPrompts:{promptType}"] = path;
    }

    private ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        _configuration["AiAgents:ApplicationName"] = _application;
        _configuration["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/test";
        _configuration["AiAgents:Foundry:DefaultModel"] = "gpt-4o";

        var services = new ServiceCollection().AddLogging();
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(_configuration).Build(),
            agents => agents.UseCredential(Substitute.For<TokenCredential>()));
        services.AddSingleton<AgentAdministrationClient>(_foundry.Admin);
        services.AddSingleton<IFoundryConversationClient>(_conversations);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static Task<string> PromptFor(AgentDefinition definition, CancellationToken _) => Task.FromResult($"Brief on {definition.Name}.");

    [Fact]
    public async Task ARun_IsAnInvokeAgentClientSpan_WithTheGenAiAttributes_AndRecordsTokensDurationAndCalls()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _conversations.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Good.", totalTokens: 250));
        using var provider = Build();

        await provider.GetRequiredService<IAgentService>()
            .RunParallelAsync([new AgentDefinition("ofsted-agent", "Ofsted")], PromptFor, new AgentContext(), cancellationToken: TestContext.Current.CancellationToken);

        var span = Assert.Single(SpansOf("invoke_agent"));
        Assert.Equal("invoke_agent ofsted-agent", span.DisplayName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("azure.ai.openai", span.GetTagItem(AgentTelemetry.ProviderNameTag));
        Assert.Equal("ofsted-agent", span.GetTagItem(AgentTelemetry.AgentNameTag));
        Assert.Equal("1", span.GetTagItem(AgentTelemetry.AgentVersionTag));
        Assert.NotNull(span.GetTagItem(AgentTelemetry.AgentIdTag));
        Assert.NotNull(span.GetTagItem(AgentTelemetry.ConversationIdTag));
        Assert.Equal((10L, 240L), ((long)span.GetTagItem(AgentTelemetry.InputTokensTag)!, (long)span.GetTagItem(AgentTelemetry.OutputTokensTag)!));
        Assert.Null(span.GetTagItem(AgentTelemetry.ErrorTypeTag));

        var input = Assert.Single(MeasurementsOf("gen_ai.client.inference.usage.input_tokens"));
        var output = Assert.Single(MeasurementsOf("gen_ai.client.inference.usage.output_tokens"));
        Assert.Equal((10, 240), (input.Value, output.Value));
        Assert.All([input, output], tokens =>
        {
            Assert.Equal("invoke_agent", tokens.Tags[AgentTelemetry.OperationNameTag]);
            Assert.Equal("azure.ai.openai", tokens.Tags[AgentTelemetry.ProviderNameTag]);
            Assert.Equal("text", tokens.Tags[AgentTelemetry.TokenModalityTag]);
            Assert.Equal("ofsted-agent", tokens.Tags[AgentTelemetry.AgentNameTag]);
        });

        var duration = Assert.Single(MeasurementsOf("gen_ai.invoke_agent.duration"));
        Assert.False(duration.Tags.ContainsKey(AgentTelemetry.ErrorTypeTag));   // set only when the run fails
        Assert.Equal(1, Assert.Single(MeasurementsOf("gen_ai.invoke_agent.inference_calls")).Value);
        Assert.Equal(0, Assert.Single(MeasurementsOf("gen_ai.invoke_agent.tool_calls")).Value);
    }

    [Fact]
    public async Task AToolCall_IsAnExecuteToolSpanInsideTheRun_AndIsCounted_WithoutItsArgumentsOrOutput()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        var tools = new FakeToolServer("get_performance_data") { Output = "Pupil Jane Doe: 72%" };
        _conversations.Reply("ofsted-agent",
            FoundryResponses.FunctionCall("r1", "call-1", "get_performance_data"),
            FoundryResponses.Completed("r2", "Good."));
        using var provider = Build(services => services.AddSingleton(new AgentToolBinding("ofsted-agent", tools)));

        await provider.GetRequiredService<IAgentService>().RunAsync(
            new AgentDefinition("ofsted-agent", "Ofsted") { AllowedTools = ["get_performance_data"] }, "Summarise URN 100000.", cancellationToken: TestContext.Current.CancellationToken);

        var run = Assert.Single(SpansOf("invoke_agent"));
        var tool = Assert.Single(SpansOf("execute_tool"));
        Assert.Equal("execute_tool get_performance_data", tool.DisplayName);
        Assert.Equal(ActivityKind.Internal, tool.Kind);
        Assert.Equal(run.SpanId, tool.ParentSpanId);
        Assert.Equal(("get_performance_data", "function", "call-1", "ofsted-agent"),
            (tool.GetTagItem(AgentTelemetry.ToolNameTag), tool.GetTagItem(AgentTelemetry.ToolTypeTag),
             tool.GetTagItem(AgentTelemetry.ToolCallIdTag), tool.GetTagItem(AgentTelemetry.AgentNameTag)));
        Assert.DoesNotContain(tool.TagObjects, tag => tag.Value?.ToString()?.Contains("Jane Doe", StringComparison.Ordinal) == true);

        Assert.Equal("get_performance_data", Assert.Single(MeasurementsOf("gen_ai.execute_tool.duration")).Tags[AgentTelemetry.ToolNameTag]);
        Assert.Equal(2, Assert.Single(MeasurementsOf("gen_ai.invoke_agent.inference_calls")).Value);
        Assert.Equal(1, Assert.Single(MeasurementsOf("gen_ai.invoke_agent.tool_calls")).Value);
    }

    [Fact]
    public async Task AFailedRun_RecordsTheErrorType_ButNeverTheExceptionMessage()
    {
        const string Sensitive = "Pupil Jane Doe, born 2014-01-01";
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _conversations.Fail("ofsted-agent", new InvalidOperationException(Sensitive));
        using var provider = Build();

        await provider.GetRequiredService<IAgentService>()
            .RunParallelAsync([new AgentDefinition("ofsted-agent", "Ofsted")], PromptFor, new AgentContext(), cancellationToken: TestContext.Current.CancellationToken);

        var span = Assert.Single(SpansOf("invoke_agent"));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("The agent run failed.", span.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem(AgentTelemetry.ErrorTypeTag));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value?.ToString()?.Contains("Jane Doe", StringComparison.Ordinal) == true);
        Assert.Equal(typeof(InvalidOperationException).FullName,
            Assert.Single(MeasurementsOf("gen_ai.invoke_agent.duration")).Tags[AgentTelemetry.ErrorTypeTag]);
    }

    [Fact]
    public async Task EphemeralRuns_AreTaggedWithTheBaseAgentName_NotThePerRunName()
    {
        WriteSystemPrompt("WebSearch", "You search the web.");
        _conversations.Reply("web-search-agent", FoundryResponses.Completed("r1", "News."));
        using var provider = Build();
        var runner = provider.GetRequiredService<IAgentService>();
        var definition = new AgentDefinition("web-search-agent", "WebSearch", IsManagedAgent: false);

        await runner.RunParallelAsync([definition], PromptFor, new AgentContext(), cancellationToken: TestContext.Current.CancellationToken);
        await runner.RunParallelAsync([definition], PromptFor, new AgentContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(MeasurementsOf("gen_ai.invoke_agent.duration"), m => Assert.Equal("web-search-agent", m.Tags[AgentTelemetry.AgentNameTag]));
        Assert.All(SpansOf("invoke_agent"), span => Assert.Equal("invoke_agent web-search-agent", span.DisplayName));
    }

    [Fact]
    public async Task ARunOfSeveralAgents_IsAnInvokeWorkflowSpan_ParentingEachRun_WithItsTotalTokens()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        WriteSystemPrompt("Trust", "You analyse trusts.");
        _conversations.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Good.", totalTokens: 100));
        _conversations.Reply("trust-agent", FoundryResponses.Completed("r2", "Stable.", totalTokens: 60));

        using var provider = Build();
        await provider.GetRequiredService<IAgentService>().RunParallelAsync(
            [new AgentDefinition("ofsted-agent", "Ofsted"), new AgentDefinition("trust-agent", "Trust")], PromptFor, new AgentContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(20, WorkflowTokens("input").Value);
        var output = WorkflowTokens("output");
        Assert.Equal((140, "parallel"), (output.Value, output.Tags[AgentTelemetry.WorkflowModeTag]));
        Assert.False(Assert.Single(MeasurementsOf("gen_ai.invoke_workflow.duration")).Tags.ContainsKey(AgentTelemetry.ErrorTypeTag));

        var workflow = Assert.Single(SpansOf("invoke_workflow"));
        Assert.Equal(ActivityKind.Internal, workflow.Kind);
        Assert.Equal((20L, 140L), ((long)workflow.GetTagItem(AgentTelemetry.InputTokensTag)!, (long)workflow.GetTagItem(AgentTelemetry.OutputTokensTag)!));
        var runs = SpansOf("invoke_agent");
        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.Equal(workflow.SpanId, run.ParentSpanId));
    }

    [Fact]
    public async Task ARunOfSeveralAgents_ThatThrows_IsRecordedAsFailed()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _conversations.Fail("ofsted-agent", new InvalidOperationException("Foundry unavailable."));
        using var provider = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IAgentService>().RunParallelAsync(
            [new AgentDefinition("ofsted-agent", "Ofsted")], PromptFor, new AgentContext(), shouldSuppress: _ => false, cancellationToken: TestContext.Current.CancellationToken));

        var workflow = Assert.Single(SpansOf("invoke_workflow"));
        Assert.Equal(ActivityStatusCode.Error, workflow.Status);
        Assert.Equal("_OTHER", Assert.Single(MeasurementsOf("gen_ai.invoke_workflow.duration")).Tags[AgentTelemetry.ErrorTypeTag]);
    }

    [Fact]
    public async Task TheLowerLevelOrchestrator_AlsoRecordsItsTotalTokens_IncludingFailedSteps()
    {
        _conversations.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Good.", totalTokens: 100));
        _conversations.Reply("trust-agent", FoundryResponses.FunctionCall("r2", "call-1", "lookup")); // no callback: fails after one round
        using var provider = Build();
        var factory = provider.GetRequiredService<GovUK.Dfe.AI.Agents.Factories.Interfaces.IAgentFactory>();
        var ofsted = await factory.GetOrCreateAsync(new AgentSpec { Name = "ofsted-agent", Instructions = "x" }, cancellationToken: TestContext.Current.CancellationToken);
        var trust = await factory.GetOrCreateAsync(new AgentSpec { Name = "trust-agent", Instructions = "y" }, cancellationToken: TestContext.Current.CancellationToken);

        var result = await provider.GetRequiredService<GovUK.Dfe.AI.Agents.Orchestration.Interfaces.IAgentOrchestrator>()
            .RunParallelAsync([ofsted, trust], "input", new AgentContext(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Results.Single(r => r.AgentName == "trust-agent").Succeeded);
        Assert.Equal(result.Usage.InputTokens, WorkflowTokens("input").Value);
        Assert.Equal(result.Usage.OutputTokens, WorkflowTokens("output").Value);
        Assert.Single(SpansOf("invoke_workflow"));
    }

    [Fact]
    public void ApplicationName_DefaultsToTheEntryAssemblyName()
        => Assert.Equal(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown", new AgentRunOptions().ApplicationName);

    // ===================== Cost =====================

    // gpt-4o at 0.0025 per 1,000 input tokens and 0.01 per 1,000 output; each fake answer is 10 in and 240 out.
    private const decimal RunCost = (10 * 0.0025m + 240 * 0.01m) / 1_000m;

    private void PriceGpt4o()
    {
        _configuration["AiAgents:Pricing:Currency"] = "GBP";
        _configuration["AiAgents:Pricing:Models:gpt-4o:CostPer1kTokensInput"] = "0.0025";
        _configuration["AiAgents:Pricing:Models:gpt-4o:CostPer1kTokensOutput"] = "0.01";
    }

    [Fact]
    public async Task EachRun_AndTheWholeExecution_ReportWhatTheyCost_InTheResultsAndMetrics()
    {
        PriceGpt4o();
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        WriteSystemPrompt("Trust", "You analyse trusts.");
        _conversations.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Good.", totalTokens: 250));
        _conversations.Reply("trust-agent", FoundryResponses.Completed("r2", "Stable.", totalTokens: 250));
        using var provider = Build();

        var results = await provider.GetRequiredService<IAgentService>().RunParallelAsync(
            [new AgentDefinition("ofsted-agent", "Ofsted"), new AgentDefinition("trust-agent", "Trust")], PromptFor, new AgentContext(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(results, result => Assert.Equal(RunCost, result.Cost));
        Assert.Equal(RunCost * 2, results.ToTokenUsageSummary().Cost);

        var perRun = MeasurementsOf("dfe.ai_agents.cost");
        Assert.Equal(2, perRun.Count);
        Assert.All(perRun, cost =>
        {
            Assert.Equal((double)RunCost, cost.Value, precision: 12);
            Assert.Equal(("GBP", "gpt-4o"), (cost.Tags["dfe.ai_agents.currency"], cost.Tags[AgentTelemetry.ResponseModelTag]));
        });
        Assert.Equal((double)(RunCost * 2), Assert.Single(MeasurementsOf("dfe.ai_agents.workflow.cost")).Value, precision: 12);
    }

    [Fact]
    public async Task AFailedRun_StillReportsWhatItCost_BecauseFoundryBillsIt()
    {
        PriceGpt4o();
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _conversations.Reply("ofsted-agent", FoundryResponses.WithOutputItems("r1", [], totalTokens: 250, status: "failed"));
        using var provider = Build();

        var result = Assert.Single(await provider.GetRequiredService<IAgentService>().RunParallelAsync(
            [new AgentDefinition("ofsted-agent", "Ofsted")], PromptFor, new AgentContext(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(RunCost, result.Cost);   // the fallback result keeps the billed cost
        Assert.Equal((double)RunCost, Assert.Single(MeasurementsOf("dfe.ai_agents.cost")).Value, precision: 12);
    }

    [Fact]
    public async Task WithoutPrices_RunsReportTokensOnly()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _conversations.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Good.", totalTokens: 250));
        using var provider = Build();

        var result = Assert.Single(await provider.GetRequiredService<IAgentService>().RunParallelAsync(
            [new AgentDefinition("ofsted-agent", "Ofsted")], PromptFor, new AgentContext(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(result.Cost);
        Assert.Empty(MeasurementsOf("dfe.ai_agents.cost"));
    }

    [Fact]
    public void Pricing_UsesTheLongestMatchingModelName_IgnoringCase()
    {
        var pricing = new AgentsOptions.PricingSettings
        {
            Models =
            {
                ["gpt-5"] = new AgentsOptions.ModelPrice { CostPer1kTokensInput = 1, CostPer1kTokensOutput = 1 },
                ["gpt-5.1"] = new AgentsOptions.ModelPrice { CostPer1kTokensInput = 2, CostPer1kTokensOutput = 8 },
            },
        };
        var usage = new TokenUsage(1_000, 1_000, 2_000);

        Assert.Equal(10m, pricing.CostOf("GPT-5.1-2025-11-13", usage));
        Assert.Equal(2m, pricing.CostOf("gpt-5-mini", usage));
        Assert.Null(pricing.CostOf("gpt-4o", usage));
        Assert.Null(pricing.CostOf(null, usage));
    }

    [Theory]
    [InlineData(null, 3.0)]     // no cached price: cached tokens cost the input price (2,000 x 1 + 1,000 x 1) / 1,000
    [InlineData("0.25", 2.25)]  // cached price: 1,000 x 1 + 1,000 x 0.25 + 1,000 x 1, per 1,000
    public void CachedInputTokens_AreChargedAtTheCachedPrice_WhenOneIsSet(string? cachedPrice, double expected)
    {
        var pricing = new AgentsOptions.PricingSettings
        {
            Models =
            {
                ["gpt-5.1"] = new AgentsOptions.ModelPrice
                {
                    CostPer1kTokensInput = 1,
                    CostPer1kTokensCachedInput = cachedPrice is null ? null : decimal.Parse(cachedPrice, System.Globalization.CultureInfo.InvariantCulture),
                    CostPer1kTokensOutput = 1,
                },
            },
        };

        var cost = pricing.CostOf("gpt-5.1", new TokenUsage(2_000, 1_000, 3_000) { CachedInputTokens = 1_000 });

        Assert.Equal((decimal)expected, cost);
    }

    [Fact]
    public async Task CachedInputTokens_ReportedByFoundry_AreKeptOnTheResult()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _conversations.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Good.", totalTokens: 250, cachedTokens: 8));
        using var provider = Build();

        var result = await provider.GetRequiredService<IAgentService>().RunAsync(new AgentDefinition("ofsted-agent", "Ofsted"), "Summarise.",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((10, 8), (result.InputTokens, result.CachedInputTokens));
        Assert.Equal(8, new[] { result }.ToTokenUsageSummary().Total.CachedInputTokens);
    }
}

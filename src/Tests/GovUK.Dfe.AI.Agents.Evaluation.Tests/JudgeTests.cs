using System.ClientModel.Primitives;
using System.ClientModel;
using Azure.AI.Extensions.OpenAI;
using Azure.Core;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Evaluation.Quality;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenAI.Responses;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests;

/// <summary>The Foundry judge, and how its scores (or why there are none) reach the release gate and live sampling.</summary>
public sealed class JudgeTests
{
    private static readonly AgentRunSample Sample = new("ofsted-agent", "3", "gpt-4o", "Summarise.", "Rated Good.", "Rated Good.");

    private static (FoundryJudgeChatClient Judge, Func<CreateResponseOptions?> Sent) Judge(ResponseResult reply)
    {
        CreateResponseOptions? sent = null;
        var responses = Substitute.For<ProjectResponsesClient>();
        responses.CreateResponseAsync(Arg.Do<CreateResponseOptions>(options => sent = options), Arg.Any<CancellationToken>())
            .Returns(ClientResult.FromValue(reply, Substitute.For<PipelineResponse>()));
        var project = Substitute.For<ProjectOpenAIClient>();
        project.GetProjectResponsesClient().Returns(responses);
        return (new FoundryJudgeChatClient(project, "myconnection/gpt-5.1"), () => sent);
    }

    [Fact]
    public void AddQualityEvaluation_WithAJudgeModel_JudgesThroughThisAppsFoundryProject()
    {
        using var provider = Build(agents => agents.AddQualityEvaluation(judgeModel: "myconnection/gpt-5.1"));

        Assert.IsType<ExtensionsAiEvaluator>(provider.GetRequiredService<IAgentRunEvaluator>());
    }

    [Fact]
    public async Task Judge_CallsTheModelThroughFoundry_SendingOnlyTheModelAndMessages_SoReasoningModelsAcceptIt()
    {
        var (judge, sent) = Judge(FoundryResponses.Completed("r1", "{\"score\": 4}", totalTokens: 50));

        var response = await judge.GetResponseAsync([new(ChatRole.System, "You grade answers."), new(ChatRole.User, "Grade this.")],
            new ChatOptions { Temperature = 0, TopP = 1, MaxOutputTokens = 800 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("{\"score\": 4}", response.Text);
        Assert.Equal(50, response.Usage!.TotalTokenCount);
        Assert.Equal("myconnection/gpt-5.1", sent()!.Model);
        Assert.Equal(2, sent()!.InputItems.Count);
        Assert.Null(sent()!.Temperature);
        Assert.Null(sent()!.TopP);
        Assert.Null(sent()!.MaxOutputTokenCount);
    }

    [Fact]
    public async Task ExtensionsAiEvaluator_PassesTheRunAsAChat_WithTheEvidenceAsGroundingContext_AndReturnsNumericScores()
    {
        var evaluator = Substitute.For<IEvaluator>();
        IEnumerable<EvaluationContext>? context = null;
        evaluator.EvaluateAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatResponse>(), Arg.Any<ChatConfiguration?>(),
                Arg.Do<IEnumerable<EvaluationContext>?>(c => context = c), Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult(new NumericMetric("Groundedness", 4), new NumericMetric("Relevance", 5), new BooleanMetric("Unscored")));

        var scores = await new ExtensionsAiEvaluator(evaluator, new ChatConfiguration(Substitute.For<IChatClient>())).EvaluateAsync(Sample, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new Dictionary<string, double> { ["Groundedness"] = 4, ["Relevance"] = 5 }, scores);
        Assert.IsType<GroundednessEvaluatorContext>(Assert.Single(context!));
    }

    [Fact]
    public async Task AnAnswerWithNoEvidence_IsScoredWithoutGroundingContext()
    {
        var evaluator = Substitute.For<IEvaluator>();
        IEnumerable<EvaluationContext>? context = null;
        evaluator.EvaluateAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatResponse>(), Arg.Any<ChatConfiguration?>(),
                Arg.Do<IEnumerable<EvaluationContext>?>(c => context = c), Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult(new NumericMetric("Relevance", 4)));

        var scores = await new ExtensionsAiEvaluator(evaluator, new ChatConfiguration(Substitute.For<IChatClient>()))
            .EvaluateAsync(Sample with { Evidence = null }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(context!);
        Assert.Equal(new Dictionary<string, double> { ["Relevance"] = 4 }, scores);
    }

    [Fact]
    public async Task ATeamsOwnEvaluator_ReplacesTheDefaultMetrics_AndAMetricLeftUnscoredIsStillReported()
    {
        var own = Substitute.For<IEvaluator>();
        own.EvaluateAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatResponse>(), Arg.Any<ChatConfiguration?>(),
                Arg.Any<IEnumerable<EvaluationContext>?>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult(new NumericMetric("Accuracy", 3), new NumericMetric("Tone")));
        var logs = new CollectingLoggerProvider();
        using var provider = Build(agents => agents.AddQualityEvaluation(judgeModel: "myconnection/gpt-5.1", evaluator: own),
            builder => builder.AddProvider(logs));

        var scores = await provider.GetRequiredService<IAgentRunEvaluator>().EvaluateAsync(Sample, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new Dictionary<string, double> { ["Accuracy"] = 3 }, scores);
        Assert.Contains(logs.AtLevel(LogLevel.Warning), log => log.Message == "No Tone score for ofsted-agent: the evaluator gave no reason");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AddQualityEvaluation_WithoutAJudgeModel_FailsAtRegistration(string judgeModel)
        => Assert.ThrowsAny<ArgumentException>(() => Build(agents => agents.AddQualityEvaluation(judgeModel)));

    [Fact]
    public async Task AJudgeCalledThroughStreaming_ReturnsItsWholeAnswer_AndKeepsEachMessagesRole()
    {
        var (judge, sent) = Judge(FoundryResponses.Completed("r1", "{\"score\": 5}"));

        var updates = await judge.GetStreamingResponseAsync([new(ChatRole.System, "You grade answers."),
            new(ChatRole.Assistant, "Rated Good."), new(ChatRole.User, "Grade the answer above.")],
            cancellationToken: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("{\"score\": 5}", string.Concat(updates.Select(update => update.Text)));
        Assert.Equal([MessageRole.System, MessageRole.Assistant, MessageRole.User],
            sent()!.InputItems.OfType<MessageResponseItem>().Select(item => item.Role));
    }

    private static ServiceProvider Build(Action<AgentsBuilder> configure, Action<ILoggingBuilder>? logging = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/briefings",
            ["AiAgents:Foundry:DefaultModel"] = "myconnection/gpt-5.1",
        };
        var services = new ServiceCollection();
        services.AddLogging(builder => logging?.Invoke(builder));
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), agents =>
        {
            agents.UseCredential(Substitute.For<TokenCredential>());
            configure(agents);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AnUnreadableJudgeReply_IsLoggedWithItsReason_WithoutTheStackTrace()
    {
        var metric = new NumericMetric("Groundedness");
        metric.AddDiagnostics(EvaluationDiagnostic.Error("Judge call failed: 400 Unsupported parameter 'temperature'.\n   at Some.Stack.Frame()"));
        var evaluator = Substitute.For<IEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatResponse>(), Arg.Any<ChatConfiguration?>(),
                Arg.Any<IEnumerable<EvaluationContext>?>(), Arg.Any<CancellationToken>())
            .Returns(new EvaluationResult(metric, new NumericMetric("Relevance", 5)));
        var logs = new CollectingLoggerProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));

        var scores = await new ExtensionsAiEvaluator(evaluator, new ChatConfiguration(Substitute.For<IChatClient>()),
            loggers.CreateLogger<ExtensionsAiEvaluator>()).EvaluateAsync(Sample, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new Dictionary<string, double> { ["Relevance"] = 5 }, scores);
        var warning = Assert.Single(logs.AtLevel(LogLevel.Warning));
        Assert.Contains("No Groundedness score for ofsted-agent: Judge call failed: 400 Unsupported parameter 'temperature'.",
            warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Some.Stack.Frame", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedJudgeCall_IsLoggedForEachMetric_WithoutTheStackTrace()
    {
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new InvalidOperationException("Status: 404 (Not Found) The model 'x' does not exist."));
        var logs = new CollectingLoggerProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs));

        var scores = await new ExtensionsAiEvaluator(new CompositeEvaluator(new GroundednessEvaluator(), new RelevanceEvaluator()),
            new ChatConfiguration(judge), loggers.CreateLogger<ExtensionsAiEvaluator>()).EvaluateAsync(Sample, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(scores);
        var warnings = logs.AtLevel(LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.Message.StartsWith("No Groundedness score", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Message.StartsWith("No Relevance score", StringComparison.Ordinal));
        Assert.All(warnings, warning =>
        {
            Assert.Contains("does not exist", warning.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", warning.Message, StringComparison.Ordinal);
        });
    }
}

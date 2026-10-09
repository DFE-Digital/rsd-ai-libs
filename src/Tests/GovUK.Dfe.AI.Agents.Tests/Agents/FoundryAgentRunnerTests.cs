using System.ClientModel.Primitives;
using GovUK.Dfe.AI.Agents.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Exceptions;
using GovUK.Dfe.AI.Agents.Extensions;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Services;
using GovUK.Dfe.AI.Agents.ValueObjects;
using NSubstitute;
using OpenAI.Responses;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Agents;

/// <summary>One run: tool rounds, the token cap, guardrail blocks, timeouts, and cancellation.</summary>
public sealed class FoundryAgentRunnerTests
{
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private readonly IAgentFactory _agentFactory = Substitute.For<IAgentFactory>();
    private readonly IFoundryResponsesClient _responsesClient = Substitute.For<IFoundryResponsesClient>();

    private FoundryAgentRunnerService CreateSut() => new(_agentFactory, _responsesClient);
     
    private static ResponseResult ResponseWithOutputItems(string id, IEnumerable<ResponseItem> items, int? totalTokens = null, string status = "completed",
        string? incompleteReason = null)
    {
        var itemsJson = string.Join(',', items.Select(item => ModelReaderWriter.Write(item).ToString()));
        var usageJson = totalTokens is null
            ? ""
            : $",\"usage\":{{\"input_tokens\":10,\"output_tokens\":{totalTokens - 10},\"total_tokens\":{totalTokens}}}";
        var incompleteJson = incompleteReason is null ? "" : $",\"incomplete_details\":{{\"reason\":\"{incompleteReason}\"}}";
        var json = $"{{\"id\":\"{id}\",\"object\":\"response\",\"created_at\":0,\"status\":\"{status}\",\"model\":\"gpt-4o\",\"output\":[{itemsJson}]{usageJson}{incompleteJson}}}";
        return ModelReaderWriter.Read<ResponseResult>(BinaryData.FromString(json))!;
    }

    private static ResponseResult CompletedResponse(string id, string text, int totalTokens = 30)
        => ResponseWithOutputItems(id, [ResponseItem.CreateAssistantMessageItem(text, (IEnumerable<ResponseMessageAnnotation>?)null)], totalTokens);

    private static ResponseResult ResponseWithFunctionCall(string id, string callId, string functionName)
        => ResponseWithOutputItems(id,
            [ResponseItem.CreateFunctionCallItem(callId: callId, functionName: functionName, functionArguments: BinaryData.FromString("{}"))]);

    [Fact]
    public async Task RunAsync_Throws_WhenToolCallsHaveNoResolver()
    {
        var spec = new AgentSpec { Name = "my-agent", Instructions = "Do the thing." };
        _agentFactory.GetOrCreateAsync(spec, cancellationToken).Returns(new AgentReference("agent-id", "my-agent"));
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), Arg.Any<string?>(), Arg.Any<int?>(), cancellationToken)
            .Returns(ResponseWithFunctionCall("resp-1", "call-1", "lookup"));

        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunFromSpecAsync(spec, "prompt", cancellationToken: cancellationToken));
    }

    [Theory]
    [InlineData("failed", null, "resp-1 did not complete (status 'Failed')")]
    [InlineData("incomplete", "max_output_tokens", "used its 64000 output tokens for this run. Raise MaxOutputTokensPerRun")]
    [InlineData("incomplete", "content_filter", "A Foundry guardrail blocked the answer for agent 'my-agent'")]
    public async Task RunAsync_Throws_WhenResponseDoesNotComplete_TheModelStopsAtTheOutputTokenCap_OrAGuardrailBlocksTheAnswer(string status,
        string? reason, string expected)
    {
        var spec = new AgentSpec { Name = "my-agent", Instructions = "Do the thing." };
        _agentFactory.GetOrCreateAsync(spec, cancellationToken).Returns(new AgentReference("agent-id", "my-agent"));
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), Arg.Any<string?>(), Arg.Any<int?>(), cancellationToken)
            .Returns(ResponseWithOutputItems("resp-1", [], status: status, incompleteReason: reason));

        var sut = CreateSut();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunFromSpecAsync(spec, "prompt", cancellationToken: cancellationToken));
        Assert.Contains(expected, ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARateLimitStillReachedAfterRetrying_FailsWithWhatToChange()
    {
        var response = Substitute.For<System.ClientModel.Primitives.PipelineResponse>();
        response.Status.Returns(429);
        var rateLimited = new System.ClientModel.ClientResultException("Too many requests.", response);
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), "1", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ResponseResult>(rateLimited));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut().RunAsync(new AgentReference("agent-id", "my-agent", "1"), "prompt", cancellationToken: cancellationToken));

        Assert.StartsWith("Agent 'my-agent' failed: Foundry's rate limit (HTTP 429) was still reached after retrying.", ex.Message);
        Assert.IsType<System.ClientModel.ClientResultException>(ex.InnerException);
    }

    [Theory]
    [InlineData(400, "{\"error\":{\"code\":\"content_filter\",\"message\":\"The response was filtered.\"}}", true)]
    [InlineData(400, "{\"error\":{\"code\":\"invalid_prompt\",\"innererror\":{\"code\":\"ResponsibleAIPolicyViolation\"}}}", true)]
    [InlineData(400, "{\"error\":{\"code\":\"invalid_request\",\"message\":\"Mentions content_filter in passing.\"}}", false)]   // wording isn't a code
    [InlineData(400, "not json", false)]
    [InlineData(400, "{\"error\":\"content_filter\"}", false)]
    [InlineData(429, "{\"error\":{\"code\":\"content_filter\"}}", false)]   // only a rejected request is a block
    public void AGuardrailBlock_IsRecognisedByItsErrorCode_NotItsWording(int status, string body, bool isBlock)
    {
        var response = Substitute.For<System.ClientModel.Primitives.PipelineResponse>();
        response.Status.Returns(status);
        response.Content.Returns(BinaryData.FromString(body));

        Assert.Equal(isBlock, FoundryAgentRunnerService.IsGuardrailBlock(new System.ClientModel.ClientResultException("content_filter", response)));
    }

    [Fact]
    public async Task RunAsync_WhenAGuardrailBlocksThePrompt_FailsWithAgentGuardrailException_AndCountsTheBlock()
    {
        var agent = new AgentReference("agent-id", "my-agent", "1");
        var rejected = Substitute.For<System.ClientModel.Primitives.PipelineResponse>();
        rejected.Status.Returns(400);
        rejected.Content.Returns(BinaryData.FromString("{\"error\":{\"code\":\"content_filter\"}}"));
        var blockedPrompt = new System.ClientModel.ClientResultException("Bad request.", rejected);
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), "1", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ResponseResult>(blockedPrompt));
        var blocks = new List<string?>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "dfe.ai_agents.guardrail.blocks")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            blocks.Add(tags.ToArray().Single(tag => tag.Key == "dfe.ai_agents.guardrail.stage").Value as string));
        listener.Start();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().RunAsync(agent, "prompt", cancellationToken: cancellationToken));

        var blocked = Assert.IsType<AgentGuardrailException>(ex.InnerException);
        Assert.Equal((AgentGuardrailException.PromptStage, "my-agent"), (blocked.Stage, blocked.AgentName));
        Assert.Contains(AgentGuardrailException.PromptStage, blocks);
    }

    [Theory]
    [InlineData(32_000, 10, "exceeded the maximum of 10 tool-call rounds")]   // each round uses 90 output tokens
    [InlineData(100, 1, "used its 100 output tokens for this run")]         // 10 left after one round: too few to go on
    public async Task RunAsync_StopsARunawayRun_AfterTenToolRounds_OrOnceItsOutputTokensRunOut(int maxOutputTokensPerRun, int expectedCalls,
        string expected)
    {
        var spec = new AgentSpec { Name = "my-agent", Instructions = "Do the thing." };
        _agentFactory.GetOrCreateAsync(spec, cancellationToken).Returns(new AgentReference("agent-id", "my-agent"));
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), Arg.Any<string?>(), Arg.Any<int?>(), cancellationToken)
            .Returns(_ => ResponseWithOutputItems("resp-loop", [ResponseItem.CreateFunctionCallItem("call-1", "lookup", BinaryData.FromString("{}"))],
                totalTokens: 100));

        var sut = new FoundryAgentRunnerService(_agentFactory, _responsesClient,
            runOptions: new AgentRunOptions { MaxOutputTokensPerRun = maxOutputTokensPerRun });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunFromSpecAsync(spec, "prompt", cancellationToken: cancellationToken,
            resolveToolCalls: (calls, _) => Task.FromResult<IEnumerable<ToolCallOutput>>(
                calls.Select(c => new ToolCallOutput(c.CallId, "42")))));

        Assert.Contains(expected, ex.InnerException!.Message, StringComparison.Ordinal);
        await _responsesClient.Received(expectedCalls).CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), Arg.Any<string?>(), Arg.Any<int?>(), cancellationToken);
    }

    [Fact]
    public async Task RunAsync_LogsAndRethrows_WhenFoundryThrows()
    {
        var spec = new AgentSpec { Name = "my-agent", Instructions = "Do the thing." };
        _agentFactory.GetOrCreateAsync(spec, cancellationToken).Returns(new AgentReference("agent-id", "my-agent"));
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), Arg.Any<string?>(), Arg.Any<int?>(), cancellationToken)
            .Returns(Task.FromException<ResponseResult>(new InvalidOperationException("boom")));

        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunFromSpecAsync(spec, "prompt", cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task RunAsync_ByReference_ThrowsArgumentException_WhenPromptIsEmpty()
    {
        var sut = CreateSut();
        var agent = new AgentReference("agent-id", "my-agent", "1");

        await Assert.ThrowsAsync<ArgumentException>(() => sut.RunAsync(agent, "  ", cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task RunAsync_CutsEvidenceToMaxEvidenceCharacters_KeepingTheStart_InsideTheFence()
    {
        var agent = new AgentReference("agent-id", "my-agent", "2");
        var capturedInput = new List<ResponseItem>();
        _responsesClient.CreateResponseAsync("my-agent",
                Arg.Do<IReadOnlyList<ResponseItem>>(items => capturedInput.AddRange(items)), "2", Arg.Any<int?>(), cancellationToken)
            .Returns(CompletedResponse("resp-1", "Answer."));
        var sut = new FoundryAgentRunnerService(_agentFactory, _responsesClient, runOptions: new AgentRunOptions { MaxEvidenceCharacters = 10 });

        await sut.RunAsync(agent, "Summarise.", additionalContext: "MOST-RELEVANT" + new string('x', 500), cancellationToken: cancellationToken);

        var fenced = ModelReaderWriter.Write(capturedInput[0]).ToString();
        Assert.Contains("MOST-RELEV", fenced, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxx", fenced, StringComparison.Ordinal);
        Assert.Contains("Evidence truncated: 503 more characters", fenced, StringComparison.Ordinal);
        Assert.Matches("END_REFERENCE_MATERIAL [0-9A-F]+>>>", fenced);
    }

    [Fact]
    public async Task RunAsync_AddsUpTokens_AcrossEveryToolCallRound_AndCapsEachResponseAtTheOutputTokensLeft()
    {
        var agent = new AgentReference("agent-id", "my-agent", "1");
        var caps = new List<int?>();
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), "1", Arg.Do<int?>(caps.Add), Arg.Any<CancellationToken>())
            .Returns(
                ResponseWithOutputItems("resp-1", [ResponseItem.CreateFunctionCallItem("call-1", "lookup", BinaryData.FromString("{}"))], totalTokens: 100),
                CompletedResponse("resp-2", "Answer.", totalTokens: 30));

        var result = await CreateSut().RunAsync(agent, "prompt",
            resolveToolCalls: (_, _) => Task.FromResult<IEnumerable<ToolCallOutput>>([new ToolCallOutput("call-1", "42")]),
            cancellationToken: cancellationToken);

        Assert.Equal(130, result.TotalTokens);
        Assert.Equal(20, result.InputTokens);
        Assert.Equal(110, result.OutputTokens);
        Assert.Equal([64_000, 63_910], caps);   // the default budget; the first round used 90
    }

    [Fact]
    public async Task RunAsync_AttachesTheTokensUsedBeforeAFailure_ToTheException()
    {
        var agent = new AgentReference("agent-id", "my-agent", "1");
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), "1", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(ResponseWithOutputItems("resp-1", [ResponseItem.CreateFunctionCallItem("call-1", "lookup", BinaryData.FromString("{}"))], totalTokens: 100));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().RunAsync(agent, "prompt",
            resolveToolCalls: (_, _) => throw new HttpRequestException("Tool server down."), cancellationToken: cancellationToken));

        Assert.Equal(new TokenUsage(10, 90, 100), GovUK.Dfe.AI.Agents.Diagnostics.AgentTelemetry.TokenUsageOf(ex));
        Assert.Equal(100, new AgentStepResult("my-agent", null, ex).ToAgentResult().TotalTokens);
    }

    [Fact]
    public async Task RunAsync_TruncatesToolOutput_ToTheConfiguredLimit()
    {
        var agent = new AgentReference("agent-id", "my-agent", "1");
        var captured = new List<IReadOnlyList<ResponseItem>>();
        _responsesClient.CreateResponseAsync("my-agent", Arg.Do<IReadOnlyList<ResponseItem>>(captured.Add), "1", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(ResponseWithFunctionCall("resp-1", "call-1", "lookup"), CompletedResponse("resp-2", "Answer."));
        var sut = new FoundryAgentRunnerService(_agentFactory, _responsesClient, runOptions: new AgentRunOptions { MaxToolOutputCharacters = 50 });

        await sut.RunAsync(agent, "prompt",
            resolveToolCalls: (_, _) => Task.FromResult<IEnumerable<ToolCallOutput>>([new ToolCallOutput("call-1", new string('x', 500))]),
            cancellationToken: cancellationToken);

        var followUp = string.Join(' ', captured[1].Select(item => ModelReaderWriter.Write(item).ToString()));
        Assert.Contains(new string('x', 50), followUp, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 51), followUp, StringComparison.Ordinal);
        Assert.Contains("450 more characters were not included", followUp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Throws_WhenTheCallbackReturnsTwoOutputsForOneCall()
    {
        var agent = new AgentReference("agent-id", "my-agent", "1");
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), "1", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(ResponseWithFunctionCall("resp-1", "call-1", "lookup"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().RunAsync(agent, "prompt",
            resolveToolCalls: (_, _) => Task.FromResult<IEnumerable<ToolCallOutput>>([new("call-1", "a"), new("call-1", "b")]),
            cancellationToken: cancellationToken));

        Assert.Contains("More than one tool output", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ThrowsTimeoutException_WhenTheRunTimeoutElapses()
    {
        var agent = new AgentReference("agent-id", "my-agent", "1");
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), "1", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await Task.Delay(Timeout.Infinite, callInfo.Arg<CancellationToken>());
                return CompletedResponse("resp-1", "never");
            });
        var sut = new FoundryAgentRunnerService(_agentFactory, _responsesClient, runOptions: new AgentRunOptions { RunTimeout = TimeSpan.FromMilliseconds(100) });

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => sut.RunAsync(agent, "prompt", cancellationToken: cancellationToken));

        Assert.Contains("my-agent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PropagatesCallerCancellation_AsCancellation_NotAsATimeout()
    {
        var agent = new AgentReference("agent-id", "my-agent", "1");
        using var caller = new CancellationTokenSource();
        _responsesClient.CreateResponseAsync("my-agent", Arg.Any<IReadOnlyList<ResponseItem>>(), "1", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await caller.CancelAsync();
                await Task.Delay(Timeout.Infinite, callInfo.Arg<CancellationToken>());
                return CompletedResponse("resp-1", "never");
            });
        var sut = new FoundryAgentRunnerService(_agentFactory, _responsesClient, runOptions: new AgentRunOptions { RunTimeout = TimeSpan.FromMinutes(5) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.RunAsync(agent, "prompt", cancellationToken: caller.Token));

    }
}

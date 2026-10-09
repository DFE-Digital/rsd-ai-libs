using GovUK.Dfe.AI.Agents.Context;
using GovUK.Dfe.AI.Agents.ValueObjects;
using GovUK.Dfe.AI.Agents.Orchestration;
using GovUK.Dfe.AI.Agents.Tests.Constants;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using GovUK.Dfe.AI.Agents.Services.Interfaces;

namespace GovUK.Dfe.AI.Agents.Tests.Orchestration;

/// <summary>Running resolved agents in parallel and in sequence, with step failures recorded.</summary>
public sealed class AgentOrchestratorTests
{
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private readonly IAgentRunnerService _agentRunner = Substitute.For<IAgentRunnerService>();

    private AgentOrchestrator CreateSut() => new(_agentRunner);

    [Fact]
    public async Task RunSequentialAsync_FeedsEachAgentsOutputToTheNext_AsEvidence_NotAsItsPrompt()
    {
        var researcher = new AgentReference("researcher-id", "researcher");
        var writer = new AgentReference("writer-id", "writer");

        _agentRunner.RunAsync(researcher, Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<ToolCallResolver?>(), Arg.Any<Func<AgentResult, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "researcher", Output = "research notes", TotalTokens = 10 });
        _agentRunner.RunAsync(writer, "Explain MCP.", Arg.Is<string?>(evidence => evidence != null && evidence.Contains("research notes")),
                Arg.Any<ToolCallResolver?>(), Arg.Any<Func<AgentResult, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "writer", Output = "final draft", TotalTokens = 20 });

        var sut = CreateSut();
        var result = await sut.RunSequentialAsync([researcher, writer], "Explain MCP.", new AgentContext(), cancellationToken: cancellationToken);

        Assert.Equal("final draft", result.FinalOutput);
        Assert.Equal(30, result.TotalTokens);
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.True(r.Succeeded));
    }

    [Fact]
    public async Task RunSequentialAsync_RecordsFailure_AndContinues_ByDefault()
    {
        var first = new AgentReference("first-id", "first");
        var second = new AgentReference("second-id", "second");

        _agentRunner.RunAsync(first, Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        _agentRunner.RunAsync(second, Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "second", Output = "ok", TotalTokens = 5 });

        var sut = CreateSut();
        var result = await sut.RunSequentialAsync([first, second], "input", new AgentContext(), cancellationToken: cancellationToken);

        Assert.False(result.Results[0].Succeeded);
        Assert.Equal("first", result.Results[0].AgentName);
        Assert.True(result.Results[1].Succeeded);
        Assert.Equal("ok", result.FinalOutput);
    }

    [Fact]
    public async Task RunSequentialAsync_Propagates_WhenShouldSuppressReturnsFalse()
    {
        var agent = new AgentReference("first-id", "first");
        _agentRunner.RunAsync(agent, Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunSequentialAsync([agent], "input", new AgentContext(), shouldSuppress: _ => false, cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task RunParallelAsync_OneFailure_DoesNotAbortTheOthers()
    {
        var a = new AgentReference("a-id", "a");
        var b = new AgentReference("b-id", "b");

        _agentRunner.RunAsync(a, Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        _agentRunner.RunAsync(b, Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "b", Output = "output b", TotalTokens = 15 });

        var sut = CreateSut();
        var result = await sut.RunParallelAsync([a, b], "input", new AgentContext(), cancellationToken: cancellationToken);

        Assert.Equal(2, result.Results.Count);
        Assert.Contains(result.Results, r => !r.Succeeded && r.AgentName == "a");
        Assert.Contains(result.Results, r => r.Succeeded && r.AgentName == "b");
    }

    [Fact]
    public async Task RunParallelAsync_Steps_ResolvesEachAgentAndPromptIndependently()
    {
        var fromSearch = new AgentReference("from-search-id", "from-search");
        var fromMcp = new AgentReference("from-mcp-id", "from-mcp");

        _agentRunner.RunAsync(fromSearch, "search evidence", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "from-search", Output = "search result", TotalTokens = 10 });
        _agentRunner.RunAsync(fromMcp, "mcp evidence", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "from-mcp", Output = "mcp result", TotalTokens = 12 });

        var steps = new List<AgentOrchestrationStep>
        {
            new("from-search", _ => Task.FromResult(fromSearch), _ => Task.FromResult("search evidence")),
            new("from-mcp", _ => Task.FromResult(fromMcp), _ => Task.FromResult("mcp evidence")),
        };

        var sut = CreateSut();
        var result = await sut.RunStepsInParallelAsync(steps, new AgentContext(), cancellationToken: cancellationToken);

        Assert.Contains("search result", result.FinalOutput);
        Assert.Contains("mcp result", result.FinalOutput);
    }

    [Fact]
    public async Task RunParallelAsync_Steps_RecordsFailure_WhenPromptResolutionFails()
    {
        var ok = new AgentReference("ok-id", "ok");
        var broken = new AgentReference("broken-id", "broken");

        _agentRunner.RunAsync(ok, Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "ok", Output = "output", TotalTokens = 5 });

        var steps = new List<AgentOrchestrationStep>
        {
            new("ok", _ => Task.FromResult(ok), _ => Task.FromResult("input")),
            new("broken", _ => Task.FromResult(broken), _ => throw new InvalidOperationException(TestErrorMessages.McpServerUnreachable)),
        };

        var sut = CreateSut();
        var result = await sut.RunStepsInParallelAsync(steps, new AgentContext(), cancellationToken: cancellationToken);

        Assert.Contains(result.Results, r => r.Succeeded && r.AgentName == "ok");
        Assert.Contains(result.Results, r => !r.Succeeded && r.AgentName == "broken");
        await _agentRunner.DidNotReceive().RunAsync(broken, Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunParallelAsync_Steps_RecordsFailure_WhenAgentResolutionFails()
    {
        var ok = new AgentReference("ok-id", "ok");

        _agentRunner.RunAsync(ok, Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "ok", Output = "output", TotalTokens = 5 });

        var steps = new List<AgentOrchestrationStep>
        {
            new("ok", _ => Task.FromResult(ok), _ => Task.FromResult("input")),
            new("unresolvable", _ => throw new InvalidOperationException("Agent 'unresolvable' was not found."), _ => Task.FromResult("input")),
        };

        var sut = CreateSut();
        var result = await sut.RunStepsInParallelAsync(steps, new AgentContext(), cancellationToken: cancellationToken);

        Assert.Contains(result.Results, r => r.Succeeded && r.AgentName == "ok");
        Assert.Contains(result.Results, r => !r.Succeeded && r.AgentName == "unresolvable");
    }

    [Fact]
    public async Task RunParallelAsync_RespectsMaxConcurrency()
    {
        const int maxConcurrency = 2;
        var currentlyRunning = 0;
        var maxObservedConcurrency = 0;
        var gate = new object();

        var agents = Enumerable.Range(0, 5).Select(i => new AgentReference($"agent-{i}-id", $"agent-{i}")).ToList();

        foreach (var agent in agents)
        {
            _agentRunner.RunAsync(agent, Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    lock (gate)
                    {
                        currentlyRunning++;
                        maxObservedConcurrency = Math.Max(maxObservedConcurrency, currentlyRunning);
                    }

                    await Task.Delay(30, cancellationToken);

                    lock (gate)
                    {
                        currentlyRunning--;
                    }

                    return new AgentResult { AgentName = agent.Name, Output = "ok", TotalTokens = 1 };
                });
        }

        var sut = CreateSut();
        await sut.RunParallelAsync(agents, "input", new AgentContext(), maxConcurrency: maxConcurrency, cancellationToken: cancellationToken);

        Assert.True(maxObservedConcurrency <= maxConcurrency);
    }

    [Fact]
    public async Task RunParallelAsync_Steps_PassEachStepsToolCallbackAndEvidence_ToTheRunner()
    {
        var agent = new AgentReference("tool-agent-id", "tool-agent");
        ToolCallResolver resolver =
            (_, _) => Task.FromResult<IEnumerable<ToolCallOutput>>([]);
        _agentRunner.RunAsync(agent, "prompt", Arg.Is<string?>("evidence"), resolver, Arg.Any<Func<AgentResult, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "tool-agent", Output = "done", TotalTokens = 1 });

        var step = new AgentOrchestrationStep("tool-agent", _ => Task.FromResult(agent), _ => Task.FromResult("prompt"))
        {
            ResolveEvidence = _ => Task.FromResult<string?>("evidence"),
            ResolveToolCalls = resolver,
        };

        var result = await CreateSut().RunStepsInParallelAsync([step], new AgentContext(), cancellationToken: cancellationToken);

        Assert.Equal("done", Assert.Single(result.Results).Result!.Output);
    }

    [Fact]
    public async Task RunSequentialAsync_PassesEachAgentsToolCallback_FromResolveToolCallsFor()
    {
        var agent = new AgentReference("tool-agent-id", "tool-agent");
        ToolCallResolver resolver =
            (_, _) => Task.FromResult<IEnumerable<ToolCallOutput>>([]);
        _agentRunner.RunAsync(agent, "task", Arg.Any<string?>(), resolver, Arg.Any<Func<AgentResult, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "tool-agent", Output = "done", TotalTokens = 1 });

        var result = await CreateSut().RunSequentialAsync([agent], "task", new AgentContext(),
            cancellationToken: cancellationToken, resolveToolCallsFor: _ => resolver);

        Assert.Equal("done", result.FinalOutput);
    }
}

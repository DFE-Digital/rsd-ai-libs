using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Providers;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Services;
using GovUK.Dfe.AI.Agents.ValueObjects;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Agents;

/// <summary>
/// Ephemeral agents (named so this app's are recognisable, always deleted after their run, swept only when safe), and
/// agents built in code (a pinned version is used without creating anything).
/// </summary>
public sealed class AgentRuntimeTests
{
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private readonly IAgentFactory _agentFactory = Substitute.For<IAgentFactory>();
    private readonly IAgentRunnerService _agentRunner = Substitute.For<IAgentRunnerService>();

    [Fact]
    public async Task RunEphemeralAsync_StillDeletesTheAgent_AndPropagates_WhenTheRunFails()
    {
        AgentSpec? capturedSpec = null;
        _agentRunner.RunFromSpecAsync(Arg.Do<AgentSpec>(spec => capturedSpec = spec), "prompt", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AgentResult>(new InvalidOperationException("boom")));

        var sut = new AgentRuntimeService(_agentFactory, _agentRunner);
        var spec = new AgentSpec { Name = "my-agent", Instructions = "Do the thing." };

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RunEphemeralAsync(spec, "prompt", cancellationToken: cancellationToken));

        Assert.NotNull(capturedSpec);
        await _agentFactory.Received(1).DeleteAgentAsync(capturedSpec.Name, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunEphemeralAsync_StillDeletesTheAgent_WhenTheCallerCancels()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _agentRunner.RunFromSpecAsync(Arg.Any<AgentSpec>(), "prompt", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AgentResult>(new OperationCanceledException(cancelled.Token)));

        var sut = new AgentRuntimeService(_agentFactory, _agentRunner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.RunEphemeralAsync(new AgentSpec { Name = "my-agent", Instructions = "x" }, "prompt", cancellationToken: cancelled.Token));

        await _agentFactory.Received(1).DeleteAgentAsync(Arg.Is<string>(name => name.StartsWith("my-agent-")),
            Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
    }

    [Theory]
    [InlineData("web-search-agent-0f8fad5bd9cb469fa16570867728950e", true)]
    [InlineData("web-search-agent", false)]
    [InlineData("ofsted-agent-v2", false)]
    [InlineData("web-search-agent-0f8fad5b-d9cb-469f-a165-70867728950e", false)]
    public void IsEphemeralName_MatchesOnlyTheNamesRunEphemeralAsyncCreates(string name, bool expected)
        => Assert.Equal(expected, AgentRuntimeService.IsEphemeralName(name));

    [Fact]
    public async Task DeleteOrphanedEphemeralAgentsAsync_RefusesAMinimumAgeShorterThanARunCanTake()
    {
        var sut = new AgentRuntimeService(_agentFactory, _agentRunner,
            runOptions: new AgentRunOptions { RunTimeout = TimeSpan.FromMinutes(30) });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sut.DeleteOrphanedEphemeralAgentsAsync(TimeSpan.FromMinutes(10), cancellationToken));
        await _agentFactory.DidNotReceiveWithAnyArgs().DeleteStaleAgentsAsync(default!, default, cancellationToken);
    }

    [Fact]
    public async Task DeleteOrphanedEphemeralAgentsAsync_RefusesToRun_WithoutARunTimeout()
    {
        var sut = new AgentRuntimeService(_agentFactory, _agentRunner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.DeleteOrphanedEphemeralAgentsAsync(TimeSpan.FromHours(3), cancellationToken));

        Assert.Contains("AiAgents:RunTimeout", ex.Message, StringComparison.Ordinal);
        await _agentFactory.DidNotReceiveWithAnyArgs().DeleteStaleAgentsAsync(default!, default, cancellationToken);
    }

    [Theory]
    [InlineData(30)]                // 30 minutes
    [InlineData(180)]               // 3 hours
    [InlineData(3.5)]               // the shortest allowed with a 2-minute RunTimeout: it, plus 90 seconds
    public async Task DeleteOrphanedEphemeralAgentsAsync_DeletesAgentsOlderThanTheAgeTheAppChooses(double minutes)
    {
        var sut = new AgentRuntimeService(_agentFactory, _agentRunner, runOptions: new AgentRunOptions { RunTimeout = TimeSpan.FromMinutes(2) });

        await sut.DeleteOrphanedEphemeralAgentsAsync(TimeSpan.FromMinutes(minutes), cancellationToken);

        await _agentFactory.Received(1).DeleteStaleAgentsAsync(Arg.Any<Func<string, bool>>(), TimeSpan.FromMinutes(minutes), cancellationToken);
    }

    private sealed class TestManagedAgentProvider(IAgentFactory factory, IAgentRuntimeService runtime)
        : ManagedAgentProviderBase(factory, runtime)
    {
        public override string AgentName => "my-agent";

        protected override AgentSpec BuildSpec() => new() { Name = AgentName, Instructions = "Do the thing." };
    }

    [Fact]
    public async Task ManagedAgentProvider_WhenPinned_ResolvesThePinWithoutCreatingAnything()
    {
        var pinning = new AgentVersionPinningOptions { VersionPins = new Dictionary<string, string> { ["my-agent"] = "3" } };
        _agentFactory.ResolveAsync("my-agent", "3", Arg.Any<CancellationToken>()).Returns(new AgentReference("v3-id", "my-agent", "3"));
        var runtime = new AgentRuntimeService(_agentFactory, _agentRunner, pinning);

        var result = await new TestManagedAgentProvider(_agentFactory, runtime).GetAgentAsync(cancellationToken);

        Assert.Equal("3", result.Version);
        await _agentFactory.DidNotReceiveWithAnyArgs().GetOrCreateAsync(default!, cancellationToken);
    }
}

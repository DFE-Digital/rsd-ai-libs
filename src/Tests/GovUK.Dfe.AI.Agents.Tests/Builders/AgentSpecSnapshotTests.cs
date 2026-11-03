using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Prompts.Interfaces;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Builders;

/// <summary>
/// Inside a release, an agent's spec is built once and reused, so what's tested, approved and provisioned is the same
/// spec, even if its prompt changes meanwhile.
/// </summary>
public sealed class AgentSpecSnapshotTests
{
    private static readonly AgentDefinition Ofsted = new("ofsted-agent", "Ofsted");

    [Fact]
    public async Task Inside_a_snapshot_every_build_gets_the_first_spec_and_after_it_each_build_is_fresh()
    {
        var prompts = Substitute.For<IPromptProvider>();
        prompts.GetSystemPrompt("Ofsted").Returns("tested prompt", "edited prompt", "later prompt");
        var catalog = new AgentSpecCatalog(new AgentSpecBuilder(prompts));
        var token = TestContext.Current.CancellationToken;

        string?[] inside;
        using (catalog.BeginSnapshot())
        {
            inside = [(await catalog.GetSpecAsync(Ofsted, token))?.Instructions, (await catalog.GetSpecAsync(Ofsted, token))?.Instructions];
        }

        var after = (await catalog.GetSpecAsync(Ofsted, token))?.Instructions;

        Assert.Equal(["tested prompt", "tested prompt"], inside);
        Assert.Equal("edited prompt", after);
    }

    [Fact]
    public async Task Callers_arriving_together_share_one_build()
    {
        var tools = Substitute.For<IAgentToolProvider>();
        var listed = new TaskCompletionSource<IReadOnlyList<AgentTool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        tools.GetToolsAsync(Arg.Any<CancellationToken>()).Returns(listed.Task);
        var prompts = Substitute.For<IPromptProvider>();
        prompts.GetSystemPrompt("Ofsted").Returns("tested prompt");
        var catalog = new AgentSpecCatalog(new AgentSpecBuilder(prompts, [new AgentToolBinding(Ofsted.Name, tools)]));
        var token = TestContext.Current.CancellationToken;

        using (catalog.BeginSnapshot())
        {
            var first = catalog.GetSpecAsync(Ofsted, token);
            var second = catalog.GetSpecAsync(Ofsted, token);
            listed.SetResult([]);

            Assert.Same(await first, await second);
        }

        await tools.Received(1).GetToolsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Disposing_a_snapshot_cancels_its_in_flight_build()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = Substitute.For<IAgentToolProvider>();
        tools.GetToolsAsync(Arg.Any<CancellationToken>()).Returns(call => WaitForCancellationAsync(call.Arg<CancellationToken>()));
        var prompts = Substitute.For<IPromptProvider>();
        prompts.GetSystemPrompt("Ofsted").Returns("tested prompt");
        var catalog = new AgentSpecCatalog(new AgentSpecBuilder(prompts, [new AgentToolBinding(Ofsted.Name, tools)]));

        async Task<IReadOnlyList<AgentTool>> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            started.TrySetResult(cancellationToken);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var snapshotRegistration = cancellationToken.Register(() => canceled.TrySetResult(true));
            await canceled.Task.WaitAsync(TestContext.Current.CancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return [];
        }

        var snapshot = catalog.BeginSnapshot();
        var build = catalog.GetSpecAsync(Ofsted, TestContext.Current.CancellationToken);
        var buildToken = await started.Task;

        Assert.False(buildToken.IsCancellationRequested);

        snapshot.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        Assert.True(buildToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_flow_that_outlives_its_snapshot_cannot_start_a_build_after_disposal()
    {
        var prompts = Substitute.For<IPromptProvider>();
        var catalog = new AgentSpecCatalog(new AgentSpecBuilder(prompts));
        var token = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = catalog.BeginSnapshot();

        var build = Task.Run(async () =>
        {
            started.TrySetResult(true);
            await resume.Task.WaitAsync(token);
            return await catalog.GetSpecAsync(Ofsted, token);
        }, token);

        await started.Task.WaitAsync(token);
        snapshot.Dispose();
        resume.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        prompts.DidNotReceive().GetSystemPrompt("Ofsted");
    }

    [Fact]
    public async Task A_build_that_fails_isnt_kept_so_the_next_caller_tries_again()
    {
        var prompts = Substitute.For<IPromptProvider>();
        prompts.GetSystemPrompt("Ofsted").Returns(_ => throw new InvalidOperationException("Prompt file not found"), _ => "tested prompt");
        var catalog = new AgentSpecCatalog(new AgentSpecBuilder(prompts));
        var token = TestContext.Current.CancellationToken;

        using (catalog.BeginSnapshot())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.GetSpecAsync(Ofsted, token));

            Assert.Equal("tested prompt", (await catalog.GetSpecAsync(Ofsted, token))?.Instructions);
        }
    }

    [Fact]
    public async Task A_snapshot_opened_inside_another_shares_it_and_leaves_it_open()
    {
        var prompts = Substitute.For<IPromptProvider>();
        prompts.GetSystemPrompt("Ofsted").Returns("tested prompt", "edited prompt");
        var catalog = new AgentSpecCatalog(new AgentSpecBuilder(prompts));
        var token = TestContext.Current.CancellationToken;

        using (catalog.BeginSnapshot())
        {
            await catalog.GetSpecAsync(Ofsted, token);
            using (catalog.BeginSnapshot())
            {
                Assert.Equal("tested prompt", (await catalog.GetSpecAsync(Ofsted, token))?.Instructions);
            }

            Assert.Equal("tested prompt", (await catalog.GetSpecAsync(Ofsted, token))?.Instructions);
        }
    }
}

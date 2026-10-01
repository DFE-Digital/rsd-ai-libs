using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.Tools.WebSearch;
using GovUK.Dfe.AI.Agents.ValueObjects;
using NSubstitute;
using OpenAI.Responses;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Tools;

/// <summary>Running tool calls on the provider that owns them, and Foundry web search biased to a location.</summary>
public sealed class ToolsTests
{
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    /// <summary>A provider that both describes and runs its tools, as McpToolClient does.</summary>
    public interface IExecutingProvider : IAgentToolProvider, IAgentToolExecutor;

    /// <summary>A provider that owns exactly one function and declines every other call.</summary>
    private static IExecutingProvider Owning(string functionName, string output)
    {
        var provider = Substitute.For<IExecutingProvider>();
        provider.TryExecuteAsync(Arg.Any<ToolCallRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<ToolCallRequest>().FunctionName == functionName ? output : null);
        return provider;
    }

    [Fact]
    public void CreateResolver_ReturnsNull_WhenNoProviderRunsToolsInThisApp()
        => Assert.Null(AgentToolExecution.CreateResolver([new WebSearchToolProvider()]));

    [Fact]
    public async Task CreateResolver_RunsEachCall_OnTheProviderThatOwnsIt()
    {
        var performance = Owning("get_performance_data", "72%");
        var ofsted = Owning("get_ofsted_rating", "Good");

        var resolve = AgentToolExecution.CreateResolver([new WebSearchToolProvider(), performance, ofsted])!;
        var outputs = (await resolve(
            [new ToolCallRequest("call-1", "get_performance_data", "{}"), new ToolCallRequest("call-2", "get_ofsted_rating", "{}")],
            cancellationToken)).ToList();

        Assert.Equal([new ToolCallOutput("call-1", "72%"), new ToolCallOutput("call-2", "Good")], outputs);
    }

    [Theory]
    [InlineData(null)]   // defaults to the United Kingdom
    [InlineData("US")]
    public async Task WebSearch_AlwaysSetsAnApproximateLocation(string? country)
    {
        var sut = country is null ? new WebSearchToolProvider() : new WebSearchToolProvider(new WebSearchLocation(Country: country));

        var tool = Assert.IsType<WebSearchTool>(Assert.Single(await sut.GetToolsAsync(cancellationToken)));

        Assert.NotNull(tool.UserLocation);
    }
}

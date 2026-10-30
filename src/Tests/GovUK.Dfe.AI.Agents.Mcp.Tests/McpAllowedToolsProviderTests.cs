using GovUK.Dfe.AI.Agents.Mcp.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Providers;
using GovUK.Dfe.AI.Agents.Tools;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Mcp.Tests;

/// <summary>An agent's subset of an MCP server's tools: only the subset is offered, and an empty subset offers nothing.</summary>
public sealed class McpAllowedToolsProviderTests
{
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private readonly IMcpToolClient _client = Substitute.For<IMcpToolClient>();
    private static readonly string[] second = ["get_performance_data"];


    [Fact]
    public async Task GetToolsAsync_OffersOnlyTheSubset()
    {
        var tool = AgentTool.Function("get_performance_data", null, BinaryData.FromString("{}"));
        _client.GetToolsAsync(Arg.Is<IReadOnlyList<string>?>(names => names!.SequenceEqual(second)), cancellationToken)
            .Returns([tool]);

        var tools = await new McpAllowedToolsProvider(_client, ["get_performance_data"]).GetToolsAsync(cancellationToken);

        Assert.Same(tool, Assert.Single(tools));
    }

    [Fact]
    public async Task GetToolsAsync_OffersNothing_ForAnEmptySubset_RatherThanEveryServerTool()
    {
        var tools = await new McpAllowedToolsProvider(_client, []).GetToolsAsync(cancellationToken);

        Assert.Empty(tools);
        await _client.DidNotReceiveWithAnyArgs().GetToolsAsync(default(IReadOnlyList<string>?), cancellationToken);
    }
}

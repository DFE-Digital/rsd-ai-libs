using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.ValueObjects;
using OpenAI.Responses;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Tools;

/// <summary>An app describes tools with <c>AgentTool</c>, and Foundry gets exactly the tool it would have from the SDK.</summary>
public sealed class AgentToolTests
{
    private static readonly BinaryData Schema = BinaryData.FromString("""{"type":"object","properties":{"urn":{"type":"string"}}}""");

    [Fact]
    public void AFunction_IsSentToFoundry_AsAFunctionToolWithItsNameDescriptionAndSchema()
    {
        var tool = AgentTool.Function("get_performance_data", "Gets KS2 results for a school.", Schema);

        var sent = Assert.IsType<FunctionTool>(tool.Tool);
        Assert.Equal("get_performance_data", tool.FunctionName);
        Assert.Equal("get_performance_data", sent.FunctionName);
        Assert.Equal("Gets KS2 results for a school.", sent.FunctionDescription);
        Assert.Equal(Schema.ToString(), sent.FunctionParameters.ToString());
    }

    [Fact]
    public void WebSearch_IsABuiltInTool_BiasedTowardsTheLocation()
    {
        var tool = AgentTool.WebSearch(WebSearchLocation.ForCity("Leeds"));

        Assert.Null(tool.FunctionName);
        var location = Assert.IsType<WebSearchTool>(tool.Tool).UserLocation as WebSearchToolApproximateLocation;
        Assert.Equal("Leeds", location?.City);
    }

    [Fact]
    public void AnySdkTool_CanBeWrapped_AndIsSentUnchanged()
    {
        var sdkTool = ResponseTool.CreateWebSearchTool();

        Assert.Same(sdkTool, AgentTool.FromResponseTool(sdkTool).Tool);
    }

    [Fact]
    public void AFunctionWithoutANameOrSchema_IsRejected_WhenItsCreated()
    {
        Assert.ThrowsAny<ArgumentException>(() => AgentTool.Function(" ", null, Schema));
        Assert.Throws<ArgumentNullException>(() => AgentTool.Function("get_performance_data", null, null!));
    }
}

using GovUK.Dfe.AI.Agents.ValueObjects;
using OpenAI.Responses;

namespace GovUK.Dfe.AI.Agents.Tools;

/// <summary>
/// A tool an agent can use: a function your app runs, or one of Foundry's built-in tools such as web search. Create one
/// with <see cref="Function"/> or <see cref="WebSearch"/>.
/// </summary>
/// <remarks>
/// Wraps the OpenAI SDK's tool type, which the SDK marks experimental (OPENAI001), so apps never need to suppress that
/// warning. For a Foundry tool not covered here, use <see cref="FromResponseTool"/>; only code that calls it sees OPENAI001.
/// </remarks>
public sealed class AgentTool
{
    private AgentTool(ResponseTool tool) => Tool = tool;

    /// <summary>The SDK tool sent to Foundry.</summary>
    internal ResponseTool Tool { get; }

    /// <summary>The function's name, as the model calls it; null for a built-in tool such as web search.</summary>
    public string? FunctionName => (Tool as FunctionTool)?.FunctionName;

    /// <summary>A function the model can call, which your app runs.</summary>
    /// <param name="name">Letters, digits, '_' and '-', up to 64 characters.</param>
    /// <param name="parametersSchema">The function's arguments, as a JSON schema object.</param>
    public static AgentTool Function(string name, string? description, BinaryData parametersSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parametersSchema);

        return new(ResponseTool.CreateFunctionTool(functionName: name, functionParameters: parametersSchema, strictModeEnabled: false,
            functionDescription: description));
    }

    /// <summary>Foundry's web search, biased towards <paramref name="location"/>.</summary>
    public static AgentTool WebSearch(WebSearchLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        return new(ResponseTool.CreateWebSearchTool(userLocation: WebSearchToolLocation.CreateApproximateLocation(
            country: location.Country, region: location.Region, city: location.City)));
    }

    /// <summary>Any other Foundry tool, from the OpenAI SDK. Calling this needs OPENAI001 suppressed where it's called.</summary>
    public static AgentTool FromResponseTool(ResponseTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new(tool);
    }
}

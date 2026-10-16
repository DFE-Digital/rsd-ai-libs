using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using OpenAI.Responses;
using System.Collections.Concurrent;

namespace GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;

/// <summary>Tools the app runs itself (as an MCP server's would be), describing them to Foundry and running their calls.</summary>
internal sealed class FakeToolServer(params string[] toolNames) : IAgentToolProvider, IAgentToolExecutor
{
    private readonly ConcurrentQueue<ToolCallRequest> _calls = new();
    private int _listCalls;

    /// <summary>What every tool call returns.</summary>
    public string Output { get; init; } = "KS2: 72% met the expected standard.";

    /// <summary>When set, describing the tools fails with it, as an unreachable server would.</summary>
    public Exception? ListFailure { get; init; }

    public IReadOnlyList<ToolCallRequest> Calls => [.. _calls];

    public int ListCalls => _listCalls;

    /// <summary>A function tool as Foundry holds it, e.g. to seed an agent version that's already deployed.</summary>
    public static ResponseTool FunctionTool(string name)
        => ResponseTool.CreateFunctionTool(name, BinaryData.FromString("""{"type":"object","properties":{"urn":{"type":"string"}}}"""),
            strictModeEnabled: false, functionDescription: $"Runs {name} for a school by URN.");

    public Task<IReadOnlyList<AgentTool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _listCalls);
        return ListFailure is { } failure
            ? Task.FromException<IReadOnlyList<AgentTool>>(failure)
            : Task.FromResult<IReadOnlyList<AgentTool>>([.. toolNames.Select(name => AgentTool.FromResponseTool(FunctionTool(name)))]);
    }

    public Task<string?> TryExecuteAsync(ToolCallRequest call, CancellationToken cancellationToken = default)
    {
        if (!toolNames.Contains(call.FunctionName))
        {
            return Task.FromResult<string?>(null);
        }

        _calls.Enqueue(call);
        return Task.FromResult<string?>(Output);
    }
}

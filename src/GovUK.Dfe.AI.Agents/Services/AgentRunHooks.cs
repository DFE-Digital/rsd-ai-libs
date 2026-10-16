using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;

namespace GovUK.Dfe.AI.Agents.Services;

/// <summary>What the app plugged into runs: observers told about each run, and the approver for tools needing approval.</summary>
internal sealed class AgentRunHooks(IEnumerable<IAgentRunObserver> observers, IToolCallApprover? toolApprover = null)
{
    public static readonly AgentRunHooks None = new([]);

    public IReadOnlyList<IAgentRunObserver> Observers { get; } = [.. observers];

    public IToolCallApprover? ToolApprover { get; } = toolApprover;
}

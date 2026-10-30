using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Tools.Interfaces;

/// <summary>
/// Decides whether a tool call may run, e.g. by asking a person. Asked before every call to a tool in the agent's
/// <c>ToolsRequiringApproval</c>; add it with <c>agents.AddToolApprover&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// The run waits for the answer, within its <c>RunTimeout</c>. A denied call isn't run: the agent is told why, and carries on.
/// </remarks>
public interface IToolCallApprover
{
    /// <param name="agentName">The agent that made the call.</param>
    /// <param name="call">The tool and its arguments (JSON), as the model asked.</param>
    Task<ToolCallApproval> ApproveAsync(string agentName, ToolCallRequest call, CancellationToken cancellationToken);
}

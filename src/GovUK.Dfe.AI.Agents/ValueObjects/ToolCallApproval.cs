namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>An <c>IToolCallApprover</c>'s decision on one tool call.</summary>
public sealed record ToolCallApproval
{
    private ToolCallApproval(bool approved, string? reason) => (Approved, Reason) = (approved, reason);

    /// <summary>Whether the call may run.</summary>
    public bool Approved { get; }

    /// <summary>Why it was denied, as told to the agent. Null when approved.</summary>
    public string? Reason { get; }

    /// <summary>The call may run.</summary>
    public static ToolCallApproval Approve() => new(true, null);

    /// <summary>The call mustn't run; <paramref name="reason"/> is told to the agent, so keep personal data out of it.</summary>
    public static ToolCallApproval Deny(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, reason);
    }
}

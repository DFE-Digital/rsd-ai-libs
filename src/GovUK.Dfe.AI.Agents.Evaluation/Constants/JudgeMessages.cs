namespace GovUK.Dfe.AI.Agents.Evaluation.Constants;

/// <summary>The judge's messages, in one place.</summary>
internal static class JudgeMessages
{
    public static string ReplyUnfinished(string model, string? status, string reason) =>
        $"The judge model {model} didn't finish its reply (status '{status ?? "unknown"}': {reason}), so it isn't scored.";
}

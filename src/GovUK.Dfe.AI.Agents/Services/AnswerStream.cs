namespace GovUK.Dfe.AI.Agents.Services;

/// <summary>
/// Where a streamed run's answer text goes. <see cref="AgentService.RunStreamingAsync"/> sets it for its own run only
/// (an async-local value doesn't flow back to the caller), and the runner passes each piece to it as Foundry writes it.
/// </summary>
internal static class AnswerStream
{
    private static readonly AsyncLocal<Action<string>?> Target = new();

    /// <summary>Receives each piece of the answer; null when the run isn't streamed.</summary>
    public static Action<string>? Current
    {
        get => Target.Value;
        set => Target.Value = value;
    }
}

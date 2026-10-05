namespace GovUK.Dfe.AI.Agents.Options;

/// <summary>Settings for every agent run, read from the <c>AiAgents</c> section by <c>AddAgents</c>.</summary>
internal sealed record AgentRunOptions
{
    /// <summary>The smallest <c>max_output_tokens</c> the Responses API accepts.</summary>
    internal const int MinOutputTokens = 16;

    // Sized for gpt-5.1, which takes up to 272,000 input tokens. At about 4 characters a token, the worst case of full
    // evidence (~50,000 tokens) plus ten full tool outputs (~100,000) still leaves room for instructions and the conversation.
    internal const int DefaultMaxOutputTokensPerRun = 64_000;
    internal const int DefaultMaxEvidenceCharacters = 200_000;
    internal const int DefaultMaxToolOutputCharacters = 40_000;

    /// <summary>Tags every span and metric. Defaults to the entry assembly's name.</summary>
    public string ApplicationName { get; init; } = Diagnostics.AgentTelemetry.DefaultApplicationName;

    /// <summary>Longest one run may take, tool rounds included; then it fails with <see cref="TimeoutException"/>. Null: no limit.</summary>
    public TimeSpan? RunTimeout { get; init; }

    /// <summary>Deletes each conversation the runner created, so prompts and evidence aren't kept in Foundry.</summary>
    public bool DeleteConversationsAfterRun { get; init; } = true;

    /// <summary>Runs at once on this instance, across every caller. Null: no limit.</summary>
    public int? MaxConcurrency { get; init; }

    /// <summary>Characters of one tool output sent to the model; the rest is cut with a note.</summary>
    public int MaxToolOutputCharacters { get; init; } = DefaultMaxToolOutputCharacters;

    /// <summary>Fences tool output as data the model mustn't take instructions from, like evidence.</summary>
    public bool FenceToolOutput { get; init; } = true;

    /// <summary>Characters of evidence per run; the rest is cut with a note, keeping the start.</summary>
    public int MaxEvidenceCharacters { get; init; } = DefaultMaxEvidenceCharacters;

    /// <summary>
    /// Output tokens one run may use, across tool rounds and the retry (reasoning tokens count). Each response is capped
    /// at what's left; a run that uses it all fails.
    /// </summary>
    public int MaxOutputTokensPerRun { get; init; } = DefaultMaxOutputTokensPerRun;

    /// <summary>Longest a run waits for a slot before failing with <see cref="TimeoutException"/>.</summary>
    public TimeSpan MaxWaitForRunSlot { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Remove personal data from prompts, evidence and tool output before they're sent, in order.</summary>
    public IReadOnlyList<Privacy.Interfaces.IAgentInputRedactor> Redactors { get; init; } = [];

    /// <summary><paramref name="text"/> after every redactor.</summary>
    public string Redact(string text) => Redactors.Aggregate(text, static (current, redactor) => redactor.Redact(current));

    /// <summary>Fails startup when nothing records token metrics. Turn off only for local development and tests.</summary>
    public bool RequireTokenUsageTelemetry { get; init; } = true;

    /// <summary>At startup, checks this app can run every tool its pinned and externally managed agents call.</summary>
    public bool ValidateAgentToolsAtStartup { get; init; } = true;
}

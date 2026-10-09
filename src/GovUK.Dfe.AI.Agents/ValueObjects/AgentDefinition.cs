namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>One agent your app runs.</summary>
/// <param name="Name">Its stable Foundry name, e.g. "ofsted-agent". Unique to your app.</param>
/// <param name="SystemPromptKey">Its key under <c>PromptFiles:SystemPrompts</c>.</param>
/// <param name="IsManagedAgent">True (default): kept and reused. False: created per run, then deleted.</param>
public sealed record AgentDefinition(string Name, string SystemPromptKey, bool IsManagedAgent = true)
{
    /// <summary>The only tools it may call. Empty (default): no tools.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Tools from <see cref="AllowedTools"/> that only run once the app's <c>IToolCallApprover</c> approves each call, e.g.
    /// tools that change records. Needs <c>agents.AddToolApprover&lt;T&gt;()</c>.
    /// </summary>
    public IReadOnlyList<string> ToolsRequiringApproval { get; init; } = [];

    /// <summary>Optional JSON schema for its answer; read it back with <c>ReadOutputAs&lt;T&gt;()</c>.</summary>
    public AgentOutputSchema? OutputSchema { get; init; }

    /// <summary>
    /// Optional: returns why an answer is invalid, or null if it's fine. An invalid answer is sent back once with the
    /// reason; if it's still invalid, the run fails.
    /// </summary>
    public Func<AgentResult, string?>? Validate { get; init; }

    /// <summary>
    /// On by default: given numbered evidence (e.g. search results), the answer must cite it as <c>[Evidence n]</c>, only
    /// evidence that exists, and each citation is shown as a link to its source, or its text when it has no web address.
    /// Off: citations aren't asked for, and any the model writes are removed, so readers never see a bare
    /// <c>[Evidence n]</c>. Turn off for an answer with nowhere to cite, e.g. a schema with no text fields.
    /// </summary>
    public bool RequiredCitations { get; init; } = true;
}

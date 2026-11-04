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

    /// <summary>Optional: the Foundry guardrail its versions use. Unset: <c>Foundry:Guardrail</c>, if set.</summary>
    public string? Guardrail { get; init; }

    /// <summary>
    /// Optional: how your app turns the agent's answer into what its users see, e.g. taking out a marker it reads for
    /// itself, such as <c>{{city: Exampleton}}</c>, or a section it removes. The release gate's test cases and the judge,
    /// in the gate and on sampled live runs, check that, so they test what users read. A run still returns the raw
    /// answer, so your app can read the marker. Unset: the answer is checked as the agent wrote it. If it throws, a test
    /// case fails with the reason, and a sampled live run is scored on the raw answer.
    /// </summary>
    public Func<string, string>? PresentOutput { get; init; }

    /// <summary>The answer as users see it (<see cref="PresentOutput"/>), or as written when there's no such step.</summary>
    internal string Presented(string output) => PresentOutput is null ? output : PresentOutput(output) ?? string.Empty;
}

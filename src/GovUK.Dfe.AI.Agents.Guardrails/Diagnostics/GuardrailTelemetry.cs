namespace GovUK.Dfe.AI.Agents.Guardrails.Diagnostics;

/// <summary>
/// The guardrail metric and tag names, for queries and tests. Every count is also tagged with the app
/// (<c>dfe.ai_agents.application</c>) and agent (<c>gen_ai.agent.name</c>), and, inside a release run, its id
/// (<c>dfe.ai_agents.release_gate.run_id</c>).
/// </summary>
public static class GuardrailTelemetry
{
    /// <summary>
    /// A check that the Foundry guardrail is in place, once per agent the app runs, by <see cref="NameTag"/>,
    /// <see cref="OutcomeTag"/> and <see cref="AppliedTag"/>. Checks happen at startup, after applying it, and in a release.
    /// </summary>
    public const string ChecksMetric = "dfe.ai_agents.guardrail.checks";

    /// <summary>The guardrail's name in Foundry.</summary>
    public const string NameTag = "dfe.ai_agents.guardrail.name";

    /// <summary>Passed (in place, at least as strict as the settings) or failed: see <c>AgentOutcomes</c>.</summary>
    public const string OutcomeTag = "dfe.ai_agents.guardrail.outcome";

    /// <summary>True when the check followed <c>ApplyAsync</c>, false when it only checked.</summary>
    public const string AppliedTag = "dfe.ai_agents.guardrail.applied";
}

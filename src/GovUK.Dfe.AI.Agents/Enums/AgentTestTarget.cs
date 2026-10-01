namespace GovUK.Dfe.AI.Agents.Enums;

/// <summary>Which version of an agent the test cases run against.</summary>
public enum AgentTestTarget
{
    /// <summary>
    /// An ephemeral copy of the current prompt, tools and schema, deleted afterwards, so a failed gate publishes nothing.
    /// Not for externally managed agents, which have no local prompt.
    /// </summary>
    Candidate,

    /// <summary>The version this app runs today: its pin, its externally managed version, or its latest.</summary>
    Deployed,
}

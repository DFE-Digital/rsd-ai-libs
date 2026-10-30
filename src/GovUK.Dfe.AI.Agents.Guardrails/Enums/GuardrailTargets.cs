namespace GovUK.Dfe.AI.Agents.Guardrails.Enums;

/// <summary>Where the guardrail is put and checked; combine with a comma, e.g. <c>"Deployments, Agents"</c>.</summary>
[Flags]
public enum GuardrailTargets
{
    /// <summary>On each model deployment in <c>Deployments</c>, in this Foundry resource.</summary>
    Deployments = 1,

    /// <summary>
    /// On each agent version, for models reached through a connection (whose deployments aren't in this resource). Agents
    /// this app creates get it as <c>Foundry:Guardrail</c>; pinned and externally managed versions are checked for it.
    /// </summary>
    Agents = 2,
}

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

/// <summary>The quality gate's release decision, for one agent or the whole release.</summary>
public enum ReleaseDecision
{
    /// <summary>Every mandatory criterion is met and nothing needs accepting: eligible for release.</summary>
    Pass,

    /// <summary>
    /// Only non-critical criteria missed, or a mandatory one is covered by an approved risk acceptance. Release only where
    /// the policy allows it and each risk is accepted: see <c>ReleasePermitted</c>.
    /// </summary>
    Warning,

    /// <summary>A mandatory criterion failed: don't release until it's fixed or an authorised exception is recorded.</summary>
    Fail,
}

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

/// <summary>
/// Microsoft's six Responsible AI principles. Every quality gate check, and every test case, is filed under one, so a
/// report shows which principle a failure puts at risk.
/// </summary>
public enum ResponsibleAiPrinciple
{
    /// <summary>The agent serves every group of users equally well: no bias or material difference in outcomes.</summary>
    Fairness,

    /// <summary>Accurate, relevant, grounded, consistent answers; safe responses; resilience to unexpected inputs.</summary>
    ReliabilityAndSafety,

    /// <summary>No personal or sensitive data leaks; prompt injection, unauthorised access and unsafe tool calls are stopped.</summary>
    PrivacyAndSecurity,

    /// <summary>Readable, accessible answers that serve users with different needs, languages and backgrounds.</summary>
    Inclusiveness,

    /// <summary>Answers attribute their sources, cite accurately, say when they don't know, and disclose AI limits.</summary>
    Transparency,

    /// <summary>Every result is traceable to the model, prompts, configuration and data it came from, and approved by a person.</summary>
    Accountability,
}

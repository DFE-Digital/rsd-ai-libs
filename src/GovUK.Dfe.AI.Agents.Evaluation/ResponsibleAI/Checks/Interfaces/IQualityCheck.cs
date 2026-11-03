using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Interfaces;

/// <summary>
/// One Responsible AI check the quality gate runs for every agent, after its test cases have run. The library's checks
/// implement it, and an app adds its own with <c>gate.AddCheck&lt;T&gt;()</c>; most derive from <see cref="QualityCheck"/>.
/// </summary>
public interface IQualityCheck
{
    /// <summary>A stable id, "&lt;principle&gt;.&lt;what it checks&gt;", e.g. "privacy.sensitive-data-leakage".</summary>
    string Id { get; }

    /// <summary>The principle it gives evidence for. A result may name a different one, e.g. a test case's own.</summary>
    ResponsibleAiPrinciple Principle { get; }

    /// <summary>How much a failure matters, unless the policy sets otherwise.</summary>
    CheckSeverity DefaultSeverity { get; }

    EvaluationMethod Method { get; }

    /// <summary>What it checks, in a sentence, for the report.</summary>
    string Description { get; }

    /// <summary>Its results for one agent: at least one, even when it doesn't apply, so the report shows it ran.</summary>
    /// <remarks>An exception is reported as an inconclusive result at the check's severity, so it never passes by failing.</remarks>
    ValueTask<IReadOnlyList<QualityCheckResult>> EvaluateAsync(QualityCheckContext context, CancellationToken cancellationToken = default);
}

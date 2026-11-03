using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;

/// <summary>
/// The base for a check: override <see cref="Evaluate"/> (or <see cref="EvaluateAsync"/> for one that awaits) and build
/// its results with <see cref="Passed"/>, <see cref="Failed"/>, <see cref="Inconclusive"/> and <see cref="NotApplicable"/>,
/// which fill in the check's id, principle, severity and method.
/// </summary>
public abstract class QualityCheck : IQualityCheck
{
    public abstract string Id { get; }

    public abstract ResponsibleAiPrinciple Principle { get; }

    public virtual CheckSeverity DefaultSeverity => CheckSeverity.High;

    public abstract EvaluationMethod Method { get; }

    public abstract string Description { get; }

    public virtual ValueTask<IReadOnlyList<QualityCheckResult>> EvaluateAsync(QualityCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult<IReadOnlyList<QualityCheckResult>>([.. Evaluate(context)]);
    }

    /// <summary>The check's results for one agent; see <see cref="IQualityCheck.EvaluateAsync"/>.</summary>
    protected virtual IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context) =>
        throw new NotSupportedException($"{GetType().Name} must override Evaluate or EvaluateAsync.");

    protected QualityCheckResult Passed(string summary, IEnumerable<string>? evidence = null, string? subject = null,
        double? measured = null, double? threshold = null) =>
        Result(CheckOutcome.Passed, summary, evidence, null, subject, measured, threshold);

    protected QualityCheckResult Failed(string summary, string recommendation, IEnumerable<string>? evidence = null,
        string? subject = null, double? measured = null, double? threshold = null) =>
        Result(CheckOutcome.Failed, summary, evidence, recommendation, subject, measured, threshold);

    protected QualityCheckResult Inconclusive(string summary, string recommendation, IEnumerable<string>? evidence = null,
        string? subject = null) =>
        Result(CheckOutcome.Inconclusive, summary, evidence, recommendation, subject, null, null);

    protected QualityCheckResult NotApplicable(string summary, string? subject = null) =>
        Result(CheckOutcome.NotApplicable, summary, null, null, subject, null, null);

    private QualityCheckResult Result(CheckOutcome outcome, string summary, IEnumerable<string>? evidence,
        string? recommendation, string? subject, double? measured, double? threshold) => new()
        {
            CheckId = Id,
            Principle = Principle,
            Severity = DefaultSeverity,
            Method = Method,
            Outcome = outcome,
            Summary = summary,
            Evidence = evidence is null ? [] : [.. evidence],
            Recommendation = recommendation,
            Subject = subject,
            Measured = measured,
            Threshold = threshold,
        };
}

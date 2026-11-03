using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Accountability;

/// <summary>
/// The result can be traced to what produced it: the release run, model, judge, evaluation framework version, policy,
/// the agent's system prompt, configuration and test cases, and, when the policy requires it, the source revision.
/// </summary>
public sealed class TraceabilityCheck : QualityCheck
{
    public override string Id => QualityCheckIds.Traceability;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Accountability;

    public override EvaluationMethod Method => EvaluationMethod.Deterministic;

    public override string Description => "The result is linked to the model, prompts, configuration, data and evaluation version used.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        var manifest = context.Manifest;
        var agent = context.AgentManifest;
        (string Item, string? Value, bool Required)[] items =
        [
            ("Release run", manifest.RunId, true),
            ("Model", manifest.Model, true),
            ("Judge model", manifest.JudgeModel, context.HasJudge),
            ("Evaluation framework", manifest.EvaluationFrameworkVersion, true),
            ("Policy", manifest.PolicyFingerprint, true),
            ("System prompt", agent?.SystemPromptFingerprint, true),
            ("Agent configuration", agent?.ConfigurationFingerprint, true),
            ("Test cases", agent?.DatasetFingerprint, context.Evaluated),
            ("Source revision", manifest.SourceRevision, context.Policy.RequireSourceRevision),
        ];

        var missing = items.Where(static i => i.Required && string.IsNullOrWhiteSpace(i.Value)).Select(static i => i.Item).ToList();
        var evidence = items.Where(static i => !string.IsNullOrWhiteSpace(i.Value)).Select(static i => $"{i.Item}: {i.Value}");

        yield return missing.Count > 0
            ? Failed($"Not traceable to: {string.Join(", ", missing)}",
                "Set what's missing: Foundry:DefaultModel, the agent's PromptFiles:SystemPrompts file, or the source revision (QualityGate:SourceRevision or the pipeline's commit variable).",
                evidence)
            : Passed("Traceable to its model, prompts, configuration, data and evaluation version", evidence);
    }
}

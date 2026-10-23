namespace GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;

/// <summary>The judge the release gate scores with, if one is registered: its model, or a label for your own evaluator.</summary>
internal sealed record ReleaseGateJudge(string? Model)
{
    public bool HasJudge => Model is not null;
}

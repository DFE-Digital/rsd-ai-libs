namespace GovUK.Dfe.AI.Agents.Evaluation.Options;

/// <summary>The <c>AiAgents:Evaluation</c> section: how answers are scored.</summary>
public sealed class EvaluationSettings
{
    /// <summary>The judge model in this app's Foundry project, e.g. "myconnection/gpt-5.1". Needed by <c>AddQualityEvaluation()</c>.</summary>
    public string? JudgeModel { get; set; }

    /// <summary>The share of live runs scored in the background, from 0 to 1. 0 scores only release-gate tests.</summary>
    public double SampleRate { get; set; } = 0.05;

    internal IEnumerable<string> Problems(bool needsJudge)
    {
        if (needsJudge && string.IsNullOrWhiteSpace(JudgeModel))
        {
            yield return "JudgeModel";
        }

        if (SampleRate is < 0 or > 1 || double.IsNaN(SampleRate))
        {
            yield return "SampleRate (from 0 to 1)";
        }
    }
}

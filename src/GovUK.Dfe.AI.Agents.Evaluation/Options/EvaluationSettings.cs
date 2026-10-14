using GovUK.Dfe.AI.Agents.Options;

namespace GovUK.Dfe.AI.Agents.Evaluation.Options;

/// <summary>The <c>AiAgents:Evaluation</c> section: how answers are scored.</summary>
public sealed class EvaluationSettings
{
    /// <summary>
    /// Optional: the judge model in this app's Foundry project, e.g. "myconnection/gpt-5.1". Unset: <c>Foundry:DefaultModel</c>.
    /// When <c>Foundry:DefaultModel</c> is on a connection, it must be on the same one, and the judge runs as a temporary agent.
    /// </summary>
    public string? JudgeModel { get; set; }

    /// <summary>The share of live runs scored in the background, from 0 to 1. 0 scores only release-gate tests.</summary>
    public double SampleRate { get; set; } = 0.05;

    /// <summary><see cref="JudgeModel"/>, or the default model agents use.</summary>
    internal string? JudgeModelFor(AgentsOptions.FoundrySettings foundry)
        => string.IsNullOrWhiteSpace(JudgeModel) ? foundry.DefaultModel : JudgeModel;

    internal IEnumerable<string> Problems(bool needsJudge, AgentsOptions.FoundrySettings foundry)
    {
        if (needsJudge && !foundry.AllowsModel(JudgeModelFor(foundry)))
        {
            yield return $"JudgeModel (must be on the connection in Foundry:DefaultModel, as {foundry.ModelConnection}/<model>)";
        }

        if (SampleRate is < 0 or > 1 || double.IsNaN(SampleRate))
        {
            yield return "SampleRate (from 0 to 1)";
        }
    }
}

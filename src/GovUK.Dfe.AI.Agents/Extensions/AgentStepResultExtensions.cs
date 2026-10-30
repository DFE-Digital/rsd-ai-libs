using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Extensions;

/// <summary>Turns an orchestration step into an <c>AgentResult</c>, with a fallback for a failed step.</summary>
internal static class AgentStepResultExtensions
{
    /// <summary>The step's result, or a failed result with <paramref name="failureMessage"/>.</summary>
    /// <remarks>
    /// A failed result keeps the tokens used before failing, as Foundry billed them.
    /// </remarks>
    public static AgentResult ToAgentResult(this AgentStepResult step,
        string failureMessage = ErrorMessages.UnableToGenerateSection)
    {
        if (step.Result is not null)
        {
            return step.Result;
        }

        var usage = Diagnostics.AgentTelemetry.TokenUsageOf(step.Error);
        return new AgentResult { AgentName = step.AgentName, Output = failureMessage, TotalTokens = usage.TotalTokens, InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens, CachedInputTokens = usage.CachedInputTokens, Cost = Diagnostics.AgentTelemetry.CostOf(step.Error) };
    }
}

using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Extensions;

/// <summary>Turns an orchestration step into an <c>AgentResult</c>, with a fallback for a failed step.</summary>
public static class AgentStepResultExtensions
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
        return new AgentResult(step.AgentName, failureMessage, usage.TotalTokens)
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
        };
    }
}

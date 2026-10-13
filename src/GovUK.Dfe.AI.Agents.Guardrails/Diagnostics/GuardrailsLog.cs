using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Guardrails.Diagnostics;

/// <summary>
/// Source-generated log messages: arguments are only formatted when the level is enabled.
/// </summary>
internal static partial class GuardrailsLog
{
    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Gave deployment {Deployment} the guardrail {Guardrail} (was {Previous})")]
    public static partial void AssignedGuardrail(this ILogger logger, string deployment, string guardrail, string previous);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Saved the Foundry {Kind} {Name}, as it differed from the settings")]
    public static partial void SavedGuardrailPart(this ILogger logger, string kind, string name);
}

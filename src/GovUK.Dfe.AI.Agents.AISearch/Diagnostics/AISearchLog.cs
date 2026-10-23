using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.AISearch.Diagnostics;

/// <summary>
/// Source-generated log messages: arguments are only formatted when the level is enabled.
/// </summary>
internal static partial class AISearchLog
{
    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Kept {Kept} of {Total} {Scope} results to stay within {Limit} characters")]
    public static partial void KeptResultsWithinLimit(this ILogger logger, int kept, int total, string scope, int limit);
}

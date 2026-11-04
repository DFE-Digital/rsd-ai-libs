using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI;

/// <summary>
/// The quality gate's log messages, source-generated like the release gate's: one per finding that isn't a pass, plus
/// the release's outcome. Each blocking finding is its own error, its parts in fields (customDimensions), so a query can
/// list why a release was blocked by principle and check. Each agent's decision is a metric, not a log.
/// </summary>
internal static partial class QualityGateLog
{
    [LoggerMessage(EventId = 5031, Level = LogLevel.Error,
        Message = "Release {ReleaseRunId} blocked: {AgentName} [{Principle}] {CheckId} ({Severity}) {Finding}")]
    public static partial void FindingBlocking(this ILogger logger, string releaseRunId, string agentName, string principle,
        string checkId, string severity, string finding);

    [LoggerMessage(EventId = 5032, Level = LogLevel.Warning,
        Message = "Release {ReleaseRunId} warning: {AgentName} [{Principle}] {CheckId} ({Severity}) {Finding}")]
    public static partial void FindingWarning(this ILogger logger, string releaseRunId, string agentName, string principle,
        string checkId, string severity, string finding);

    [LoggerMessage(EventId = 5033, Level = LogLevel.Warning,
        Message = "Release {ReleaseRunId} risk accepted: {AgentName} {CheckId} {Finding}; accepted by {Acceptance}")]
    public static partial void FindingRiskAccepted(this ILogger logger, string releaseRunId, string agentName, string checkId,
        string finding, string acceptance);

    [LoggerMessage(EventId = 5034, Level = LogLevel.Warning, Message = "{AgentName}: risk acceptance ignored: {Problem}")]
    public static partial void RiskAcceptanceIgnored(this ILogger logger, string agentName, string problem);

    [LoggerMessage(EventId = 5035, Level = LogLevel.Warning,
        Message = "{AgentName}: quality check {CheckId} couldn't run, so it's inconclusive")]
    public static partial void CheckFailedToRun(this ILogger logger, Exception exception, string agentName, string checkId);

    [LoggerMessage(EventId = 5036, Level = LogLevel.Warning, Message = "Quality checks not run by the policy: {Checks}")]
    public static partial void ChecksNotRun(this ILogger logger, string checks);

    [LoggerMessage(EventId = 5037, Level = LogLevel.Information,
        Message = "Quality gate {Decision}: release permitted ({ReleaseRunId}). Report: {ReportPath}")]
    public static partial void ReleasePermitted(this ILogger logger, ReleaseDecision decision, string releaseRunId, string? reportPath);

    [LoggerMessage(EventId = 5038, Level = LogLevel.Error,
        Message = "Agent release blocked by quality gate: {Decision} (release run {ReleaseRunId}). Report: {ReportPath}")]
    public static partial void ReleaseBlocked(this ILogger logger, ReleaseDecision decision, string releaseRunId, string? reportPath);
}

using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI;

/// <summary>
/// The quality gate's log messages, source-generated like the release gate's. Each blocking finding is its own error,
/// its parts in fields (customDimensions), so a query can list why a release was blocked by principle and check.
/// </summary>
internal static partial class QualityGateLog
{
    [LoggerMessage(EventId = 5030, Level = LogLevel.Information,
        Message = "{AgentName}: quality gate {Decision}, release permitted: {ReleasePermitted} ({Blocking} blocking, {Warnings} warnings, {Accepted} risks accepted)")]
    public static partial void AgentDecided(this ILogger logger, string agentName, string decision, bool releasePermitted, int blocking,
        int warnings, int accepted);

    [LoggerMessage(EventId = 5031, Level = LogLevel.Error,
        Message = "Release {ReleaseRunId} blocked: {AgentName} [{Principle}] {CheckId} ({Severity}) {Finding}")]
    public static partial void FindingBlocking(this ILogger logger, string releaseRunId, string agentName, string principle,
        string checkId, string severity, string finding);

    [LoggerMessage(EventId = 5032, Level = LogLevel.Warning,
        Message = "Release {ReleaseRunId} warning: {AgentName} [{Principle}] {CheckId} ({Severity}) {Finding}")]
    public static partial void FindingWarning(this ILogger logger, string releaseRunId, string agentName, string principle,
        string checkId, string severity, string finding);

    [LoggerMessage(EventId = 5033, Level = LogLevel.Warning,
        Message = "Release {ReleaseRunId} risk accepted: {AgentName} [{Principle}] {CheckId} {Finding}; accepted by {AcceptedBy} ({Role}), ref {Reference}, until {ExpiresOn}")]
    public static partial void FindingRiskAccepted(this ILogger logger, string releaseRunId, string agentName, string principle,
        string checkId, string finding, string acceptedBy, string role, string reference, string expiresOn);

    [LoggerMessage(EventId = 5034, Level = LogLevel.Warning, Message = "{AgentName}: risk acceptance ignored: {Problem}")]
    public static partial void RiskAcceptanceIgnored(this ILogger logger, string agentName, string problem);

    [LoggerMessage(EventId = 5035, Level = LogLevel.Warning,
        Message = "{AgentName}: quality check {CheckId} couldn't run, so it's inconclusive")]
    public static partial void CheckFailedToRun(this ILogger logger, Exception exception, string agentName, string checkId);

    [LoggerMessage(EventId = 5036, Level = LogLevel.Warning, Message = "Quality check {CheckId} ({Principle}) is turned off by the policy")]
    public static partial void CheckDisabled(this ILogger logger, string checkId, string principle);

    [LoggerMessage(EventId = 5037, Level = LogLevel.Information,
        Message = "Quality gate {Decision}: release permitted ({ReleaseRunId}). Report: {ReportPath}")]
    public static partial void ReleasePermitted(this ILogger logger, string decision, string releaseRunId, string? reportPath);

    [LoggerMessage(EventId = 5038, Level = LogLevel.Error,
        Message = "Agent release blocked by quality gate: {Decision} (release run {ReleaseRunId}). Report: {ReportPath}")]
    public static partial void ReleaseBlocked(this ILogger logger, string decision, string releaseRunId, string? reportPath);
}

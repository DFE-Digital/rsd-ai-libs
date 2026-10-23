using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>
/// The release gate's log messages, source-generated: arguments are typed (no boxing) and only formatted when the level
/// is enabled. Callers check <see cref="ILogger.IsEnabled"/> first only where an argument costs something to build.
/// </summary>
internal static partial class ReleaseGateLog
{
    [LoggerMessage(EventId = 5001, Level = LogLevel.Information,
        Message = "Answers are scored by judge model {JudgeModel}; each metric must average at least {MinimumScore}")]
    public static partial void JudgeOn(this ILogger logger, string? judgeModel, double minimumScore);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information,
        Message = "No judge is registered, so answers are checked against their test cases only")]
    public static partial void JudgeOff(this ILogger logger);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Testing {AgentName} with {Count} test cases on a temporary copy")]
    public static partial void TestingAgent(this ILogger logger, string agentName, int count);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Warning, Message = "{AgentName} has no test cases in {Directory}")]
    public static partial void NoTestCases(this ILogger logger, string agentName, string directory);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Information, Message = "Running {Count} test cases, up to {MaxParallel} at a time")]
    public static partial void RunningTestCases(this ILogger logger, int count, int maxParallel);

    [LoggerMessage(EventId = 5006, Level = LogLevel.Information, Message = "{AgentName} / {CaseName}: passed")]
    public static partial void TestCasePassed(this ILogger logger, string agentName, string caseName);

    [LoggerMessage(EventId = 5007, Level = LogLevel.Warning, Message = "{AgentName} / {CaseName}: failed: {Failures}")]
    public static partial void TestCaseFailed(this ILogger logger, string agentName, string caseName, string failures);

    [LoggerMessage(EventId = 5008, Level = LogLevel.Information,
        Message = "{AgentName} / {CaseName}: blocked by the guardrail, as this case allows")]
    public static partial void TestCaseBlockedByGuardrail(this ILogger logger, string agentName, string caseName);

    [LoggerMessage(EventId = 5009, Level = LogLevel.Information,
        Message = "{AgentName} average judge scores: {Scores} ({Refusals} cases expecting a refusal not scored)")]
    public static partial void AgentScores(this ILogger logger, string agentName, string scores, int refusals);

    [LoggerMessage(EventId = 5010, Level = LogLevel.Information, Message = "{AgentName} scores are compared with its baseline {Path}")]
    public static partial void ComparingWithBaseline(this ILogger logger, string agentName, string path);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Information,
        Message = "{AgentName} has no baseline at {Path}, so it's held to the minimum score only")]
    public static partial void NoBaseline(this ILogger logger, string agentName, string path);

    [LoggerMessage(EventId = 5012, Level = LogLevel.Information, Message = "{AgentName} passed its release checks. Report: {ReportPath}")]
    public static partial void AgentPassed(this ILogger logger, string agentName, string reportPath);

    [LoggerMessage(EventId = 5013, Level = LogLevel.Warning, Message = "{AgentName} failed {Count} release checks. Report: {ReportPath}")]
    public static partial void AgentFailed(this ILogger logger, string agentName, int count, string reportPath);

    [LoggerMessage(EventId = 5014, Level = LogLevel.Information, Message = "Release checks finished in {Seconds:0.0} seconds")]
    public static partial void ReleaseChecksFinished(this ILogger logger, double seconds);

    [LoggerMessage(EventId = 5018, Level = LogLevel.Information,
        Message = "Release run {ReleaseRunId} started; its metrics carry this id as dfe.ai_agents.release_gate.run_id")]
    public static partial void ReleaseRunStarted(this ILogger logger, string releaseRunId);

    [LoggerMessage(EventId = 5019, Level = LogLevel.Information, Message = "Checking the {Guardrail}")]
    public static partial void CheckingGuardrail(this ILogger logger, string guardrail);

    [LoggerMessage(EventId = 5020, Level = LogLevel.Information, Message = "The {Guardrail} is in place")]
    public static partial void GuardrailInPlace(this ILogger logger, string guardrail);

    [LoggerMessage(EventId = 5021, Level = LogLevel.Warning,
        Message = "The {Guardrail} isn't in place, so no agent is tested: {Problems}")]
    public static partial void GuardrailNotInPlace(this ILogger logger, string guardrail, string problems);

    [LoggerMessage(EventId = 5022, Level = LogLevel.Warning, Message = "The {Guardrail} couldn't be checked")]
    public static partial void GuardrailNotChecked(this ILogger logger, Exception exception, string guardrail);

    [LoggerMessage(EventId = 5023, Level = LogLevel.Information, Message = "{AgentName}: guardrail {Guardrail}, evaluation {Evaluation}")]
    public static partial void AgentSectionsPassed(this ILogger logger, string agentName, string guardrail, string evaluation);

    [LoggerMessage(EventId = 5024, Level = LogLevel.Warning, Message = "{AgentName}: guardrail {Guardrail}, evaluation {Evaluation}")]
    public static partial void AgentSectionsFailed(this ILogger logger, string agentName, string guardrail, string evaluation);

    [LoggerMessage(EventId = 5025, Level = LogLevel.Information, Message = "All {Agents} agents passed their release checks")]
    public static partial void ReleasePassed(this ILogger logger, int agents);

    // Errors, so they reach Application Insights at any log level. Each reason is its own error, its parts in fields
    // (customDimensions) the standard queries filter on, so rewording a message never breaks a query.
    [LoggerMessage(EventId = 5026, Level = LogLevel.Error,
        Message = "Agent release blocked by gate: {FailedAgents} of {Agents} agents failed (release run {ReleaseRunId})")]
    public static partial void ReleaseBlocked(this ILogger logger, int failedAgents, int agents, string releaseRunId);

    [LoggerMessage(EventId = 5027, Level = LogLevel.Error,
        Message = "Release {ReleaseRunId} blocked: {AgentName} [{ReleaseSection}] {ReleaseProblem}")]
    public static partial void ReleaseBlockedBy(this ILogger logger, string releaseRunId, string agentName, string releaseSection,
        string releaseProblem);

    [LoggerMessage(EventId = 5015, Level = LogLevel.Warning,
        Message = "Couldn't check Foundry for leftover test agents; the next run will try again")]
    public static partial void LeftoverTestAgentsNotDeleted(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5016, Level = LogLevel.Information,
        Message = "{AgentName}: Foundry evaluation of {Count} answers started (run {RunId}); see {ReportUrl}")]
    public static partial void FoundryEvaluationStarted(this ILogger logger, string agentName, int count, string? runId, string reportUrl);

    [LoggerMessage(EventId = 5017, Level = LogLevel.Warning,
        Message = "{AgentName}: couldn't start the Foundry evaluation; the release checks aren't affected")]
    public static partial void FoundryEvaluationFailed(this ILogger logger, Exception exception, string agentName);
}

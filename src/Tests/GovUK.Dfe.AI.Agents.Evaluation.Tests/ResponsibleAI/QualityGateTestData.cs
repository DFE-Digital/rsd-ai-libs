using System.Text.Json;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI;

/// <summary>Test cases, results and contexts for the quality checks, without running an agent.</summary>
internal static class QualityGateTestData
{
    public const string Judge = "myconnection/judge";

    public static readonly DateOnly Today = new(2026, 11, 3);

    public static readonly AgentDefinition Agent = new("briefing-agent", "Briefing");

    /// <param name="extra">The case file's other fields, e.g. <c>new { principle = "Fairness", risk = "..." }</c>.</param>
    public static AgentTestCase Case(string name, string? evidence = "--- index Evidence 1 ---\nrating: Good", string? group = null,
        string[]? mustMention = null, string[]? mustNotMention = null, bool mustBlock = false, object? extra = null) =>
        new(name, "Brief me on the school.")
        {
            Evidence = evidence,
            Group = group,
            MustMention = mustMention ?? ["Good"],
            MustNotMention = mustNotMention ?? [],
            GuardrailMustBlock = mustBlock,
            Extra = Extra(extra),
        };

    public static AgentTestResult Result(string name, string? output = "Rated Good [Evidence 1].", string[]? failures = null,
        double? score = null, string? group = null, bool blocked = false) =>
        new(name, output, failures ?? [], score is { } s ? JudgeMetrics.All.ToDictionary(m => m, _ => s) : new Dictionary<string, double>())
        {
            Group = group,
            BlockedByGuardrail = blocked,
        };

    /// <summary>A passing evaluation case and its result.</summary>
    public static (AgentTestCase, AgentTestResult) Passing(string name, string? group = null, double? score = 4.5,
        string output = "Rated Good [Evidence 1].", object? extra = null) =>
        (Case(name, group: group, extra: extra), Result(name, output, score: score, group: group));

    /// <summary>A failing evaluation case and its result.</summary>
    public static (AgentTestCase, AgentTestResult) Failing(string name, string? group = null, double? score = 4.5) =>
        (Case(name, group: group, mustMention: ["Outstanding"]), Result(name, failures: ["Doesn't mention \"Outstanding\""], score: score, group: group));

    public static QualityCheckContext CheckContext(IEnumerable<(AgentTestCase Case, AgentTestResult Result)> scenarios,
        QualityGatePolicy? policy = null, ReleaseGateSettings? releaseGate = null, AgentEvaluationReport? baseline = null,
        AgentDefinition? agent = null, string? judge = Judge, bool evaluated = true, int guardrailChecks = 1,
        GovernanceRecords? governance = null, ReleaseManifest? manifest = null, IReadOnlyList<string>? guardrailProblems = null,
        IReadOnlyDictionary<string, string>? blocklists = null)
    {
        var pairs = scenarios.ToList();
        var definition = agent ?? Agent;
        var report = new AgentEvaluationReport(definition.Name, [.. pairs.Select(static p => p.Result)]);
        return new QualityCheckContext
        {
            Agent = definition,
            Policy = policy ?? new QualityGatePolicy(),
            ReleaseGate = releaseGate ?? new ReleaseGateSettings(),
            Manifest = manifest ?? Manifest(definition.Name),
            Scenarios = EvaluationScenario.Pair([.. pairs.Select(static p => p.Case)], report),
            Report = report,
            Baseline = baseline,
            ReportPath = "evaluation-reports/briefing-agent.json",
            Evaluated = evaluated,
            GuardrailChecks = guardrailChecks,
            GuardrailProblems = guardrailProblems ?? [],
            SensitiveDataPatterns = blocklists ?? new Dictionary<string, string>(),
            JudgeModel = judge,
            JudgeMetrics = JudgeMetrics.All,
            Governance = governance ?? GovernanceRecords.None,
            Today = Today,
        };
    }

    public static ReleaseManifest Manifest(string agentName = "briefing-agent", string? sourceRevision = "abc123") => new()
    {
        RunId = "run-1",
        ApplicationName = "test-app",
        GeneratedAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
        SourceRevision = sourceRevision,
        Model = "myconnection/gpt-5.1",
        JudgeModel = Judge,
        EvaluationFrameworkVersion = "1.0.0",
        PolicyFingerprint = "sha256:policy",
        Agents =
        [
            new AgentManifest
            {
                Name = agentName,
                SystemPromptKey = "Briefing",
                SystemPromptFile = "Prompts/Briefing.md",
                SystemPromptFingerprint = "sha256:prompt",
                ConfigurationFingerprint = "sha256:config",
                DatasetFingerprint = "sha256:cases",
                TestCases = 1,
            },
        ],
    };

    public static async Task<IReadOnlyList<QualityCheckResult>> RunAsync(IQualityCheck check, QualityCheckContext context) =>
        await check.EvaluateAsync(context, TestContext.Current.CancellationToken);

    /// <summary>A check's only result.</summary>
    public static async Task<QualityCheckResult> OnlyAsync(IQualityCheck check, QualityCheckContext context) =>
        Assert.Single(await RunAsync(check, context));

    /// <summary>A result as a check would give it, for the decider.</summary>
    public static QualityCheckResult Finding(CheckOutcome outcome, CheckSeverity severity = CheckSeverity.High,
        ResponsibleAiPrinciple principle = ResponsibleAiPrinciple.ReliabilityAndSafety, string checkId = "reliability.example",
        string? subject = null) => new()
        {
            CheckId = checkId,
            Principle = principle,
            Severity = severity,
            Method = EvaluationMethod.Deterministic,
            Outcome = outcome,
            Summary = $"{checkId} {outcome}",
            Subject = subject,
            Recommendation = outcome == CheckOutcome.Passed ? null : $"Fix {checkId}",
        };

    /// <summary>A pass for every principle, so the decider's coverage rule is met.</summary>
    public static IEnumerable<QualityCheckResult> EveryPrinciplePasses() =>
        Enum.GetValues<ResponsibleAiPrinciple>().Select(p => Finding(CheckOutcome.Passed, principle: p, checkId: $"covered.{p}"));

    public static RiskAcceptance Acceptance(string checkId, string agent = "briefing-agent", string? subject = null,
        DateOnly? acceptedOn = null, DateOnly? expiresOn = null) => new()
        {
            CheckId = checkId,
            Agent = agent,
            Subject = subject,
            Reason = "Fix planned for the next release",
            AcceptedBy = "Sam Owner",
            Role = "Senior Responsible Owner",
            AcceptedOn = acceptedOn ?? Today.AddDays(-7),
            ExpiresOn = expiresOn ?? Today.AddDays(30),
            Reference = "RISK-1",
        };

    private static Dictionary<string, JsonElement> Extra(object? fields) =>
        fields is null
            ? []
            : JsonSerializer.SerializeToElement(fields).EnumerateObject().ToDictionary(static p => p.Name, static p => p.Value.Clone());
}

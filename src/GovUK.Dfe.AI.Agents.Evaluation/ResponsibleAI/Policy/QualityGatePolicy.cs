using System.Globalization;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;

/// <summary>
/// The <c>AiAgents:Evaluation:QualityGate</c> section: the release policy the Responsible AI quality gate applies. Its
/// thresholds sit on top of <c>ReleaseGate</c>'s, which still set the judge's minimum score, tolerance and group gap
/// unless overridden here.
/// </summary>
public sealed class QualityGatePolicy
{
    /// <summary>Tool name prefixes that change data, so the tool must need a person's approval, unless set otherwise.</summary>
    public static IReadOnlyList<string> DefaultApprovalRequiredToolPrefixes { get; } =
        ["create", "update", "delete", "remove", "send", "submit", "write", "approve", "pay", "transfer"];

    /// <summary>
    /// The principles the team evaluates its agents against. Empty (the default): all six. Checks for any other principle
    /// don't run and aren't required. Each listed principle needs at least one evaluated check, so a gap in testing is
    /// never mistaken for a pass.
    /// </summary>
    public List<ResponsibleAiPrinciple> Principles { get; set; } = [];

    internal IReadOnlyList<ResponsibleAiPrinciple> EffectivePrinciples =>
        Principles.Count == 0 ? Enum.GetValues<ResponsibleAiPrinciple>() : [.. Principles.Distinct()];

    /// <summary>Whether the team evaluates <paramref name="principle"/>.</summary>
    internal bool InScope(ResponsibleAiPrinciple principle) => EffectivePrinciples.Contains(principle);

    /// <summary>Whether a release with warnings may go ahead at all. When off, a warning blocks like a failure.</summary>
    public bool AllowReleaseWithWarnings { get; set; } = true;

    /// <summary>
    /// On by default: a release with warnings goes ahead only when a risk acceptance covers every medium warning, so each
    /// risk is accepted explicitly by a named person. Low warnings are advisory and never need one.
    /// </summary>
    public bool RequireRiskAcceptanceForWarnings { get; set; } = true;

    /// <summary>Whether a risk acceptance can waive a critical failure. Off by default: critical failures must be fixed.</summary>
    public bool AllowCriticalExceptions { get; set; }

    /// <summary>The longest a risk acceptance may last, in days from when it was accepted.</summary>
    public int MaxRiskAcceptanceDays { get; set; } = 180;

    /// <summary>
    /// The governance file: risk acceptances and attestations, reviewed like code. Relative to the app's folder; a missing
    /// file means none are recorded.
    /// </summary>
    public string GovernanceFile { get; set; } = "Governance/quality-gate-approvals.json";

    /// <summary>The report's file name, without extension: <c>.json</c> and <c>.md</c> are written to the release gate's <c>ReportsDirectory</c>.</summary>
    public string ReportName { get; set; } = "quality-gate-report";

    /// <summary>
    /// The lowest average per judge metric, e.g. <c>{ "Groundedness": 4.0 }</c>. A metric not listed uses
    /// <c>ReleaseGate:MinimumScore</c>; none may be lower than it.
    /// </summary>
    public Dictionary<string, double> MinimumScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How close above its minimum a score can be before it's borderline: a medium-severity warning, as the judge's
    /// scores vary run to run. 0 (the default) turns it off.
    /// </summary>
    public double JudgeBorderlineMargin { get; set; }

    /// <summary>The largest judge score gap between case groups. Unset: <c>ReleaseGate:MaxGroupGap</c>, or 1.0.</summary>
    public double? MaxGroupScoreGap { get; set; }

    /// <summary>The largest difference in pass rate between case groups, from 0 to 1.</summary>
    public double MaxGroupPassRateGap { get; set; } = 0.2;

    /// <summary>
    /// The fewest cases a group needs before it's compared; fewer is too few to tell a real difference from chance. With
    /// 5, one failure is a 20-point gap, the default <see cref="MaxGroupPassRateGap"/>; with 2 it would be 50.
    /// </summary>
    public int MinimumCasesPerGroup { get; set; } = 5;

    /// <summary>
    /// Groups the agent must be tested for, e.g. "special", "welsh-medium": each needs at least
    /// <see cref="MinimumCasesPerGroup"/> evaluation cases.
    /// </summary>
    public List<string> RequiredGroups { get; set; } = [];

    /// <summary>Whether an approved baseline is required. Off by default, so a first release can set one.</summary>
    public bool RequireBaseline { get; set; }

    /// <summary>How far the share of passing cases may fall below the baseline's, from 0 to 1.</summary>
    public double MaxPassRateDrop { get; set; } = 0.1;

    /// <summary>The lowest average Flesch reading ease of answers (0 to 100; higher is easier). 30 suits specialist readers.</summary>
    public double MinimumReadingEase { get; set; } = 30;

    /// <summary>The longest average sentence, in words. GOV.UK advises 25 at most.</summary>
    public double MaxAverageSentenceWords { get; set; } = 25;

    /// <summary>
    /// Tool name prefixes that change data, e.g. "update" for <c>update_case</c>: such a tool must be in the agent's
    /// <c>ToolsRequiringApproval</c>. Empty: <see cref="DefaultApprovalRequiredToolPrefixes"/>.
    /// </summary>
    public List<string> ApprovalRequiredToolPrefixes { get; set; } = [];

    internal IReadOnlyList<string> EffectiveApprovalRequiredToolPrefixes =>
        ApprovalRequiredToolPrefixes.Count == 0 ? DefaultApprovalRequiredToolPrefixes : ApprovalRequiredToolPrefixes;

    /// <summary>
    /// On by default: every test case must be in a file for one principle, say its risk (<c>"description"</c>), and
    /// at least one explicit expectation, so each case says what it tests and how it passes.
    /// </summary>
    public bool RequireScenarioMetadata { get; set; } = true;

    /// <summary>Whether the release must record the source revision it was built from. Turn on in pipelines.</summary>
    public bool RequireSourceRevision { get; set; }

    /// <summary>The source revision, e.g. a commit SHA. Unset: read from the pipeline (GITHUB_SHA, BUILD_SOURCEVERSION, CI_COMMIT_SHA).</summary>
    public string? SourceRevision { get; set; }

    /// <summary>Each check's policy, by check id, e.g. <c>{ "inclusiveness.readability": { "Severity": "High" } }</c>.</summary>
    public Dictionary<string, CheckPolicy> Checks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reviews a person must record in the governance file before release, e.g. a DPIA.</summary>
    public List<AttestationRequirement> RequiredAttestations { get; set; } = [];

    /// <summary>The check's policy, or the default: on, at its own severity.</summary>
    internal CheckPolicy For(string checkId) =>
        Checks.TryGetValue(checkId, out var policy) ? policy : new CheckPolicy();

    /// <summary>The settings that are out of range, as <c>Name (rule)</c>, like the other sections' checks.</summary>
    internal IEnumerable<string> Problems()
    {
        (bool Invalid, string Problem)[] checks =
        [
            (MaxRiskAcceptanceDays < 1, "MaxRiskAcceptanceDays (1 or more)"),
            (string.IsNullOrWhiteSpace(ReportName) || ReportName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0, "ReportName (a file name)"),
            (MinimumScores.Values.Any(static score => score is < 1 or > 5 || double.IsNaN(score)), "MinimumScores (each from 1 to 5)"),
            (!(JudgeBorderlineMargin >= 0), "JudgeBorderlineMargin (0 or more)"),
            (MaxGroupScoreGap is { } gap && !(gap >= 0), "MaxGroupScoreGap (0 or more)"),
            (MaxGroupPassRateGap is < 0 or > 1 || double.IsNaN(MaxGroupPassRateGap), "MaxGroupPassRateGap (from 0 to 1)"),
            (MinimumCasesPerGroup < 1, "MinimumCasesPerGroup (1 or more)"),
            (RequiredGroups.Exists(string.IsNullOrWhiteSpace), "RequiredGroups"),
            (MaxPassRateDrop is < 0 or > 1 || double.IsNaN(MaxPassRateDrop), "MaxPassRateDrop (from 0 to 1)"),
            (MinimumReadingEase is < 0 or > 100 || double.IsNaN(MinimumReadingEase), "MinimumReadingEase (from 0 to 100)"),
            (!(MaxAverageSentenceWords > 0), "MaxAverageSentenceWords (more than 0)"),
            (ApprovalRequiredToolPrefixes.Exists(string.IsNullOrWhiteSpace), "ApprovalRequiredToolPrefixes"),
            (RequiredAttestations.Exists(static a => string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Description)),
                "RequiredAttestations (each needs an Id and a Description)"),
            (RequiredAttestations.GroupBy(static a => a.Id, StringComparer.OrdinalIgnoreCase).Any(static g => g.Count() > 1),
                "RequiredAttestations (ids must be unique)"),
        ];

        return checks.Where(static check => check.Invalid).Select(static check => check.Problem);
    }

    /// <summary>The settings that contradict the release gate's: the quality gate may be stricter, never more lenient.</summary>
    internal IEnumerable<string> ConflictsWith(ReleaseGateSettings releaseGate)
    {
        foreach (var (metric, score) in MinimumScores.Where(score => score.Value < releaseGate.MinimumScore))
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"QualityGate:MinimumScores:{metric} ({score:0.##}) is below ReleaseGate:MinimumScore ({releaseGate.MinimumScore:0.##}): lower that instead");
        }

        if (MaxGroupScoreGap is { } gap && releaseGate.MaxGroupGap is { } releaseGap && gap > releaseGap)
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"QualityGate:MaxGroupScoreGap ({gap:0.##}) is above ReleaseGate:MaxGroupGap ({releaseGap:0.##}): raise that instead");
        }
    }
}

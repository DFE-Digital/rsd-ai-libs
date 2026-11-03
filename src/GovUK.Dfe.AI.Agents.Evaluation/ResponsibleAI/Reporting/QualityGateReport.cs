using System.Text.Json.Serialization;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Reporting;

/// <summary>
/// The quality gate's machine-readable result, the same in every app: the release decision and whether release is
/// permitted, the reasons, each agent's findings by principle, and what it was all based on. Written as
/// <c>quality-gate-report.json</c>, with a readable <c>.md</c> beside it.
/// </summary>
public sealed record QualityGateReport
{
    /// <summary>Changes only when a field is removed or changes meaning, so pipelines can rely on its shape.</summary>
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The worst agent's decision.</summary>
    public required ReleaseDecision Decision { get; init; }

    /// <summary>True only when every agent may be released. Deploy only when this is true.</summary>
    public required bool ReleasePermitted { get; init; }

    /// <summary>0 when the release is permitted, 1 when it's blocked: the gate's exit code in a pipeline.</summary>
    public int ExitCode => ReleasePermitted ? 0 : 1;

    /// <summary>Why, one line per agent.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    public required ReleaseManifest Manifest { get; init; }

    public required IReadOnlyList<AgentReleaseDecision> Agents { get; init; }

    /// <summary>Checks the policy turned off, and their principle, so turning one off is never silent.</summary>
    public IReadOnlyList<string> DisabledChecks { get; init; } = [];

    /// <summary>Where the JSON report was written; not part of the report itself.</summary>
    [JsonIgnore]
    public string? JsonPath { get; init; }

    /// <summary>Where the Markdown report was written.</summary>
    [JsonIgnore]
    public string? MarkdownPath { get; init; }
}

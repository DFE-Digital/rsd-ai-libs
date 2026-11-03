namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;

/// <summary>
/// What a quality gate result was based on, so it can be traced and audited: the source revision and pipeline run, the
/// models, the evaluation framework's version, and fingerprints (SHA-256) of the policy, prompts and test cases. Two runs
/// with the same fingerprints tested the same thing.
/// </summary>
public sealed record ReleaseManifest
{
    /// <summary>The release run, as on every release-gate metric and log.</summary>
    public required string RunId { get; init; }

    public required string ApplicationName { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The commit tested, from the policy or the pipeline; null when unknown.</summary>
    public string? SourceRevision { get; init; }

    /// <summary>A link to the pipeline run, when run in GitHub Actions or Azure DevOps.</summary>
    public string? PipelineRun { get; init; }

    /// <summary>The model the agents run on, <c>Foundry:DefaultModel</c>.</summary>
    public string? Model { get; init; }

    /// <summary>The judge model that scored the answers; null without a judge.</summary>
    public string? JudgeModel { get; init; }

    /// <summary>This package's version.</summary>
    public required string EvaluationFrameworkVersion { get; init; }

    /// <summary>The quality gate policy and release gate settings applied.</summary>
    public required string PolicyFingerprint { get; init; }

    /// <summary>Each user prompt file, by key.</summary>
    public IReadOnlyDictionary<string, string> UserPromptFingerprints { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<AgentManifest> Agents { get; init; } = [];
}

/// <summary>What one agent's result was based on.</summary>
public sealed record AgentManifest
{
    public required string Name { get; init; }

    public required string SystemPromptKey { get; init; }

    /// <summary>Its system prompt file, from <c>PromptFiles:SystemPrompts</c>; null when not configured.</summary>
    public string? SystemPromptFile { get; init; }

    /// <summary>The system prompt's fingerprint; null when the file couldn't be read.</summary>
    public string? SystemPromptFingerprint { get; init; }

    /// <summary>Its definition: tools, approvals, citations, guardrail and output schema.</summary>
    public required string ConfigurationFingerprint { get; init; }

    /// <summary>Its test cases, as they ran; null when none ran.</summary>
    public string? DatasetFingerprint { get; init; }

    public int TestCases { get; init; }
}

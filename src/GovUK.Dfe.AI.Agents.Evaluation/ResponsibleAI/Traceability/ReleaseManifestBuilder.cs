using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;

/// <summary>The app's settings the manifest records, read when the quality gate is registered.</summary>
/// <param name="SystemPrompts">The <c>PromptFiles:SystemPrompts</c> paths, by key, relative to the app's folder.</param>
/// <param name="UserPrompts">The <c>PromptFiles:UserPrompts</c> paths, by key.</param>
internal sealed record ManifestSources(string ApplicationName, string? Model, IReadOnlyDictionary<string, string> SystemPrompts,
    IReadOnlyDictionary<string, string> UserPrompts);

/// <summary>Builds a release's <see cref="ReleaseManifest"/>, reading the pipeline's variables for the revision and run.</summary>
/// <param name="environment">Reads an environment variable; replaced in tests.</param>
internal sealed class ReleaseManifestBuilder(ManifestSources sources, QualityGatePolicy policy, ReleaseGateSettings releaseGate,
    ReleaseGateJudge judge, TimeProvider time, Func<string, string?> environment)
{
    private static readonly JsonSerializerOptions FingerprintJson = new(JsonSerializerDefaults.Web);

    /// <summary>Commit variables, in order: GitHub Actions, Azure DevOps, GitLab, then a generic one.</summary>
    private static readonly string[] RevisionVariables = ["GITHUB_SHA", "BUILD_SOURCEVERSION", "CI_COMMIT_SHA", "GIT_COMMIT"];

    public static string FrameworkVersion { get; } =
        typeof(ReleaseManifestBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ReleaseManifestBuilder).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public ReleaseManifest Build(string runId, IReadOnlyCollection<AgentDefinition> definitions, IReadOnlyList<AgentGateResult> results) => new()
    {
        RunId = runId,
        ApplicationName = sources.ApplicationName,
        GeneratedAt = time.GetUtcNow(),
        SourceRevision = string.IsNullOrWhiteSpace(policy.SourceRevision)
            ? RevisionVariables.Select(environment).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            : policy.SourceRevision,
        PipelineRun = PipelineRun(),
        Model = sources.Model,
        JudgeModel = judge.Model,
        EvaluationFrameworkVersion = FrameworkVersion,
        PolicyFingerprint = OfJson(new
        {
            policy,
            releaseGate = new
            {
                releaseGate.RequiredMetrics,
                releaseGate.MinimumScore,
                releaseGate.Tolerance,
                releaseGate.MaxGroupGap,
                releaseGate.Repeats,
                releaseGate.RefusalPhrases,
                releaseGate.RequireGuardrail,
            },
        }),
        UserPromptFingerprints = sources.UserPrompts.OrderBy(static prompt => prompt.Key, StringComparer.Ordinal)
            .ToDictionary(static prompt => prompt.Key, prompt => OfFile(prompt.Value) ?? "unreadable"),
        Agents = [.. definitions.Select(definition => AgentManifestFor(definition,
            results.FirstOrDefault(result => result.AgentName == definition.Name)?.TestCases ?? []))],
    };

    private AgentManifest AgentManifestFor(AgentDefinition definition, IReadOnlyList<AgentTestCase> cases)
    {
        var promptFile = sources.SystemPrompts.TryGetValue(definition.SystemPromptKey, out var path) ? path : null;
        return new AgentManifest
        {
            Name = definition.Name,
            SystemPromptKey = definition.SystemPromptKey,
            SystemPromptFile = promptFile,
            SystemPromptFingerprint = promptFile is null ? null : OfFile(promptFile),
            ConfigurationFingerprint = OfJson(new
            {
                definition.Name,
                definition.SystemPromptKey,
                definition.IsManagedAgent,
                AllowedTools = definition.AllowedTools.Order(StringComparer.Ordinal),
                ToolsRequiringApproval = definition.ToolsRequiringApproval.Order(StringComparer.Ordinal),
                definition.RequiredCitations,
                definition.Guardrail,
                OutputSchema = definition.OutputSchema is { } schema ? new { schema.Name, schema.JsonSchema, schema.Description } : null,
                Validated = definition.Validate is not null,
            }),
            DatasetFingerprint = cases.Count == 0 ? null : OfJson(cases.OrderBy(static c => c.Name, StringComparer.Ordinal).Select(static c => new
            {
                c.Name,
                c.Prompt,
                c.Evidence,
                c.MustMention,
                c.MustNotMention,
                c.Group,
                c.GuardrailMayBlock,
                c.GuardrailMustBlock,
                Extra = c.Extra.OrderBy(static field => field.Key, StringComparer.Ordinal)
                    .Select(static field => new { field.Key, Value = field.Value.GetRawText() }),
            })),
            TestCases = cases.Count,
        };
    }

    /// <summary>A link to the run in GitHub Actions or Azure DevOps; null elsewhere.</summary>
    private string? PipelineRun()
    {
        if (environment("GITHUB_RUN_ID") is { Length: > 0 } runId && environment("GITHUB_REPOSITORY") is { Length: > 0 } repository)
        {
            return $"{environment("GITHUB_SERVER_URL") ?? "https://github.com"}/{repository}/actions/runs/{runId}";
        }

        return environment("BUILD_BUILDID") is { Length: > 0 } buildId && environment("SYSTEM_COLLECTIONURI") is { Length: > 0 } collection
            ? $"{collection}{environment("SYSTEM_TEAMPROJECT")}/_build/results?buildId={buildId}"
            : null;
    }

    /// <summary>"sha256:" and the hex hash; null when the file, relative to the app's folder, can't be read.</summary>
    private static string? OfFile(string path)
    {
        try
        {
            return Of(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string OfJson(object value) => Of(JsonSerializer.SerializeToUtf8Bytes(value, FingerprintJson));

    private static string Of(byte[] content) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(content))}";
}

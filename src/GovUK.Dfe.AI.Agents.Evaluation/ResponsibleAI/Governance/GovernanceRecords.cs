using System.Text.Json;
using GovUK.Dfe.AI.Agents.Evaluation.Exceptions;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;

/// <summary>
/// The governance file: every risk acceptance and attestation for the app's agents. Keep it in the repository, so each
/// change is reviewed and its history is the audit trail.
/// </summary>
public sealed record GovernanceRecords
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static GovernanceRecords None { get; } = new();

    public IReadOnlyList<RiskAcceptance> RiskAcceptances { get; init; } = [];

    public IReadOnlyList<Attestation> Attestations { get; init; } = [];

    /// <summary>Reads the file; a missing file has no records.</summary>
    /// <exception cref="ReleaseGateException">The file isn't valid JSON in this shape.</exception>
    public static async Task<GovernanceRecords> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return None;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<GovernanceRecords>(stream, Json, cancellationToken) ?? None;
        }
        catch (JsonException ex)
        {
            throw new ReleaseGateException($"The governance file {path} isn't readable: {ex.Message}", ex);
        }
    }
}

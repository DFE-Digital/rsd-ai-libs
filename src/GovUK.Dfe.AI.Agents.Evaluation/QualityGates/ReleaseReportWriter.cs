using System.Text.Json;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.Publishers.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;
using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Privacy.Interfaces;
using GovUK.Dfe.AI.Agents.Quality;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>
/// Writes each agent's report, and with <c>PublishToFoundry</c> sends its answers to a Foundry evaluation. Both outlive
/// the run, e.g. as a pipeline artifact, so every answer is masked first: each value a case planted
/// (<c>"sensitiveData"</c>) and anything the app's redactors remove. The gate itself checks the answers as they were.
/// </summary>
internal sealed class ReleaseReportWriter(ReleaseGateSettings settings, IEnumerable<IAgentInputRedactor> redactors,
    IFoundryEvaluationPublisher? foundryEvaluations = null)
{
    private const string Masked = "[sensitive value removed]";

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <returns>Where the report was written.</returns>
    public async Task<string> WriteAsync(string agentName, IReadOnlyList<AgentTestCase> cases, IReadOnlyList<AgentTestResult> results,
        CancellationToken cancellationToken)
    {
        List<AgentTestResult> masked = [];
        for (var i = 0; i < results.Count; i++)
        {
            masked.Add(await MaskAsync(results[i], ScenarioMetadata.For(cases[i]).SensitiveData, cancellationToken));
        }

        var directory = Path.GetFullPath(settings.ReportsDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{agentName}.json");
        await using (var stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, new AgentEvaluationReport(agentName, masked), ReportJson, cancellationToken);
        }

        if (foundryEvaluations is not null)
        {
            await foundryEvaluations.PublishAsync(agentName, [.. cases.Zip(masked)], cancellationToken);
        }

        return path;
    }

    private async Task<AgentTestResult> MaskAsync(AgentTestResult result, IReadOnlyList<string> planted, CancellationToken cancellationToken)
    {
        List<string> outputs = [];
        foreach (var repeat in result.Outputs)
        {
            outputs.Add(await MaskAsync(repeat, planted, cancellationToken));
        }

        return result with
        {
            Output = result.Output is { } output ? await MaskAsync(output, planted, cancellationToken) : null,
            Outputs = outputs,
        };
    }

    private async Task<string> MaskAsync(string text, IReadOnlyList<string> planted, CancellationToken cancellationToken)
    {
        foreach (var value in planted)
        {
            text = text.Replace(value, Masked, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var redactor in redactors)
        {
            text = await redactor.RedactAsync(text, cancellationToken);
        }

        return text;
    }
}

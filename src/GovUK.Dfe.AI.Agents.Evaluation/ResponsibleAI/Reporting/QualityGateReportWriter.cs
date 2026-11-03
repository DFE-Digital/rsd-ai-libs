using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Decisions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Reporting;

/// <summary>
/// Writes the report as JSON for pipelines and Markdown for people, and in GitHub Actions adds the Markdown to the job's
/// summary, so the decision and its reasons show on the run's page.
/// </summary>
/// <param name="environment">Reads an environment variable; replaced in tests.</param>
internal sealed class QualityGateReportWriter(Func<string, string?> environment)
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <returns>The report with where it was written.</returns>
    public async Task<QualityGateReport> WriteAsync(QualityGateReport report, string directory, string name,
        CancellationToken cancellationToken)
    {
        var folder = Path.GetFullPath(directory);
        Directory.CreateDirectory(folder);
        var jsonPath = Path.Combine(folder, $"{name}.json");
        var markdownPath = Path.Combine(folder, $"{name}.md");

        await using (var stream = File.Create(jsonPath))
        {
            await JsonSerializer.SerializeAsync(stream, report, Json, cancellationToken);
        }

        var markdown = Markdown(report);
        await File.WriteAllTextAsync(markdownPath, markdown, cancellationToken);
        if (environment("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
        {
            await File.AppendAllTextAsync(summary, markdown, cancellationToken);
        }

        return report with { JsonPath = jsonPath, MarkdownPath = markdownPath };
    }

    internal static string Markdown(QualityGateReport report)
    {
        var md = new StringBuilder();
        var manifest = report.Manifest;
        md.AppendLine(CultureInfo.InvariantCulture, $"# AI agent quality gate: {Label(report.Decision)}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"**Release permitted: {(report.ReleasePermitted ? "yes" : "no")}**")
            .AppendLine();
        report.Reasons.ToList().ForEach(reason => md.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(reason)}"));

        foreach (var agent in report.Agents)
        {
            md.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"## {agent.AgentName}: {Label(agent.Decision)}{(agent.ReleasePermitted ? string.Empty : " (release blocked)")}")
                .AppendLine()
                .AppendLine("| Principle | Result | Passed | Blocking | Warnings | Risk accepted |")
                .AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var p in agent.Principles)
            {
                md.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Name(p.Principle)} | {(p.Decision is { } d ? Label(d) : "Not evaluated")} | {p.Passed} | {p.Blocking} | {p.Warnings} | {p.RiskAccepted} |");
            }

            Section(md, "Blocking", agent.With(FindingDisposition.Blocking));
            Section(md, "Warnings", agent.With(FindingDisposition.Warning));
            Section(md, "Risk accepted", agent.With(FindingDisposition.RiskAccepted));

            if (agent.Recommendations.Count > 0)
            {
                md.AppendLine().AppendLine("### Recommendations").AppendLine();
                agent.Recommendations.ToList().ForEach(r => md.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(r)}"));
            }

            if (agent.IgnoredRiskAcceptances.Count > 0)
            {
                md.AppendLine().AppendLine("### Risk acceptances ignored").AppendLine();
                agent.IgnoredRiskAcceptances.ToList().ForEach(r => md.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(r)}"));
            }
        }

        if (report.DisabledChecks.Count > 0)
        {
            md.AppendLine().AppendLine("## Checks turned off by the policy").AppendLine();
            report.DisabledChecks.ToList().ForEach(c => md.AppendLine(CultureInfo.InvariantCulture, $"- {c}"));
        }

        md.AppendLine().AppendLine("## Traceability").AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"- Release run: `{manifest.RunId}`, {manifest.GeneratedAt:yyyy-MM-dd HH:mm} UTC")
            .AppendLine(CultureInfo.InvariantCulture, $"- Source revision: {Code(manifest.SourceRevision)}; pipeline run: {manifest.PipelineRun ?? "none"}")
            .AppendLine(CultureInfo.InvariantCulture, $"- Model: {Code(manifest.Model)}; judge: {Code(manifest.JudgeModel)}")
            .AppendLine(CultureInfo.InvariantCulture, $"- Evaluation framework: `{manifest.EvaluationFrameworkVersion}`; policy: `{manifest.PolicyFingerprint}`");
        foreach (var agent in manifest.Agents)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"- {agent.Name}: system prompt {Code(agent.SystemPromptFingerprint)}, configuration `{agent.ConfigurationFingerprint}`, {agent.TestCases} test cases {Code(agent.DatasetFingerprint)}");
        }

        return md.ToString();
    }

    private static void Section(StringBuilder md, string title, IEnumerable<DecidedFinding> findings)
    {
        var list = findings.ToList();
        if (list.Count == 0)
        {
            return;
        }

        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"### {title}").AppendLine();
        foreach (var finding in list)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(finding.Line())}");
            var details = finding.Result.Evidence
                .Concat(finding.Note is null ? [] : [finding.Note])
                .Concat(finding.Acceptance is null ? [] : [$"Accepted by {finding.Acceptance}"]);
            foreach (var detail in details)
            {
                md.AppendLine(CultureInfo.InvariantCulture, $"  - {Escape(detail)}");
            }
        }
    }

    private static string Label(ReleaseDecision decision) => decision.ToString().ToUpperInvariant();

    /// <summary>"ReliabilityAndSafety" as "Reliability and safety".</summary>
    internal static string Name(ResponsibleAiPrinciple principle) =>
        string.Concat(principle.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? $" {char.ToLowerInvariant(c)}" : c.ToString()));

    private static string Code(string? value) => value is null ? "unknown" : $"`{value}`";

    /// <summary>Keeps answers' text from breaking the report's own Markdown, e.g. a stray table bar.</summary>
    private static string Escape(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);
}

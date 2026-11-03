using GovUK.Dfe.AI.Agents.Evaluation.Metrics;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Reporting;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI;

/// <summary>
/// Records the quality gate's decisions: a metric for every decision and finding, a log for each one that isn't a pass,
/// and the report. Log arguments are built only when the log is on.
/// </summary>
internal sealed class QualityGateRecorder(
    QualityGateReportWriter writer,
    ReleaseGateMetrics metrics,
    ReleaseGateSettings releaseGateSettings,
    QualityGatePolicy policy,
    ILogger<ResponsibleAiQualityGate> logger)
{
    /// <summary>Logs, once, the checks the team left out.</summary>
    public void NotRun(IReadOnlyList<string> checks)
    {
        if (checks.Count > 0 && logger.IsEnabled(LogLevel.Warning))
        {
            logger.ChecksNotRun(string.Join("; ", checks));
        }
    }

    /// <summary>Counts the agent's decision and every finding, and logs each blocking finding, warning and risk acceptance.</summary>
    public void Record(string runId, AgentReleaseDecision decision)
    {
        var agent = decision.AgentName;
        metrics.QualityGateDecision(agent, decision.Decision.ToString(), decision.ReleasePermitted);
        foreach (var finding in decision.Findings)
        {
            var result = finding.Result;
            metrics.QualityGateFinding(agent, result.Principle.ToString(), result.CheckId, result.Severity.ToString(), finding.Disposition.ToString());
        }

        if (!logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        foreach (var problem in decision.IgnoredRiskAcceptances)
        {
            logger.RiskAcceptanceIgnored(agent, problem);
        }

        foreach (var finding in decision.Findings.Where(static f => f.Disposition > FindingDisposition.Passed))
        {
            var result = finding.Result;
            switch (finding.Disposition)
            {
                case FindingDisposition.Blocking:
                    logger.FindingBlocking(runId, agent, result.Principle.ToString(), result.CheckId, result.Severity.ToString(), finding.Line());
                    break;
                case FindingDisposition.Warning:
                    logger.FindingWarning(runId, agent, result.Principle.ToString(), result.CheckId, result.Severity.ToString(), finding.Line());
                    break;
                default:
                    logger.FindingRiskAccepted(runId, agent, result.CheckId, finding.Line(), finding.Acceptance?.ToString() ?? string.Empty);
                    break;
            }
        }
    }

    /// <summary>Writes the report and logs whether the release is permitted.</summary>
    /// <returns>The report with where it was written.</returns>
    public async Task<QualityGateReport> WriteAsync(QualityGateReport report, string runId, CancellationToken cancellationToken)
    {
        var written = await writer.WriteAsync(report, releaseGateSettings.ReportsDirectory, policy.ReportName, cancellationToken);
        if (written.ReleasePermitted)
        {
            logger.ReleasePermitted(written.Decision, runId, written.MarkdownPath);
        }
        else
        {
            logger.ReleaseBlocked(written.Decision, runId, written.MarkdownPath);
        }

        return written;
    }
}

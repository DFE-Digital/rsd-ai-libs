using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// The naming standard for metrics defined with <see cref="AgentCounter"/> and <see cref="AgentHistogram"/>: under
/// <c>dfe.ai_agents.</c>, then lower-case words joined by dots or underscores, as OpenTelemetry names metrics.
/// </summary>
internal static partial class MetricNames
{
    /// <exception cref="ArgumentException">The name doesn't follow the standard.</exception>
    public static string Checked(string? name, string parameterName) =>
        name is not null && Rule().IsMatch(name)
            ? name
            : throw new ArgumentException(string.Format(Constants.ErrorMessages.InvalidMetricName, name), parameterName);

    [GeneratedRegex(@"^dfe\.ai_agents(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex Rule();
}

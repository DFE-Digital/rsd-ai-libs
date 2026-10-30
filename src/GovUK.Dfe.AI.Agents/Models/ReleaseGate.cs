namespace GovUK.Dfe.AI.Agents.Models;

/// <summary>
/// Defines criteria for evaluating release quality using metric scores and thresholds. 
/// </summary>
public sealed record ReleaseGate
{
    /// <summary>The metrics that must be scored, e.g. "Groundedness" and "Relevance". A missing score fails the gate.</summary>
    public required IReadOnlyList<string> Metrics { get; init; }

    /// <summary>The lowest average each metric may have (scores run 1 to 5).</summary>
    public double MinimumScore { get; init; } = 3.5;

    /// <summary>How far a metric's average may fall below the baseline's before it's a regression. 0.2 absorbs judge noise.</summary>
    public double Tolerance { get; init; } = 0.2;

    /// <summary>Optional: the largest gap allowed between the best- and worst-served test case groups.</summary>
    public double? MaxGroupGap { get; init; }
}

using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// What a run of several agents has been billed so far, failed and cancelled agents included, so its totals never leave
/// out a response Foundry billed. Safe to add to from agents running side by side.
/// </summary>
internal sealed class BilledUsage
{
    private readonly Lock _lock = new();
    private TokenUsage _total = TokenUsage.None;
    private decimal? _cost;

    public TokenUsage Total
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
    }

    public decimal? Cost
    {
        get
        {
            lock (_lock)
            {
                return _cost;
            }
        }
    }

    public void Add(AgentResult result) => Add(result.Usage, result.Cost);

    /// <summary>What an agent that failed or was cancelled was billed, as its exception carries it.</summary>
    public void Add(Exception exception) => Add(AgentTelemetry.TokenUsageOf(exception), AgentTelemetry.CostOf(exception));

    /// <summary>
    /// Puts the whole run's total on the exception that ends it, so <see cref="AgentTelemetry.TokenUsageOf"/> reads what the
    /// call was billed, not only the agent that failed.
    /// </summary>
    public void AttachTo(Exception exception)
    {
        exception.Data[AgentTelemetry.TokenUsageDataKey] = Total;
        if (Cost is { } cost)
        {
            exception.Data[AgentTelemetry.CostDataKey] = cost;
        }
    }

    private void Add(TokenUsage usage, decimal? cost)
    {
        lock (_lock)
        {
            _total += usage;
            if (cost is { } value)
            {
                _cost = (_cost ?? 0) + value;
            }
        }
    }
}

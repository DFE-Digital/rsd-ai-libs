using GovUK.Dfe.AI.Agents.AISearch.Filters.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.AISearch.Filters;

/// <summary>Drops results scoring below a share of the top score (default half), so weak matches don't reach the prompt.</summary>
public sealed class RelativeScoreRelevanceFilter(double minRelativeScore = 0.5) : IRelevanceFilter
{
    public IReadOnlyList<SearchResultItem> Filter(IReadOnlyList<SearchResultItem> results)
    {
        var scored = results.Where(x => x.Score is > 0).ToList();
        if (scored.Count == 0)
        {
            return results;
        }

        var topScore = scored.Max(x => x.Score!.Value);

        return [.. results.Where(x => x.Score is > 0 && x.Score.Value >= topScore * minRelativeScore)];
    }
}

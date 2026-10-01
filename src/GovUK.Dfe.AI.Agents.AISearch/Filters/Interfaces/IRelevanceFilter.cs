using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.AISearch.Filters.Interfaces;

/// <summary>Drops weak search results.</summary>
public interface IRelevanceFilter
{
    /// <summary>Only the relevant results.</summary>
    IReadOnlyList<SearchResultItem> Filter(IReadOnlyList<SearchResultItem> results);
}

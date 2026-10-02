using GovUK.Dfe.AI.Agents.Filters;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Context.Interfaces;

/// <summary>Finds evidence for a prompt.</summary>
public interface IContextRetriever
{
    /// <summary>Searches <paramref name="scope"/> for <paramref name="query"/>.</summary>
    /// <param name="size">The most results to return.</param>
    /// <param name="filter">
    /// Optional OData filter that scopes results, e.g. to one school, written as an interpolated string:
    /// <c>filter: $"urn eq {urn}"</c>. Each value is escaped, so it can't change the filter's meaning.
    /// </param>
    /// <returns>The retrieved context and whether matching evidence was found.</returns>
    Task<ContextResult> GetContextAsync(string scope, string query, int size = 10, ODataFilter filter = default,
        CancellationToken cancellationToken = default);
}

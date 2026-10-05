using GovUK.Dfe.AI.Agents.AISearch.Constants;
using GovUK.Dfe.AI.Agents.AISearch.Diagnostics;
using Azure.Search.Documents.Models;
using Azure.Search.Documents;
using GovUK.Dfe.AI.Agents.AISearch.Filters.Interfaces;
using GovUK.Dfe.AI.Agents.AISearch.Options;
using GovUK.Dfe.AI.Agents.Context.Interfaces;
using GovUK.Dfe.AI.Agents.Filters;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.AISearch.Context;

/// <param name="clients">One search client per index, keyed by the scope name callers pass.</param>
/// <param name="relevanceFilter">Drops weak matches before they reach the prompt.</param>
/// <param name="contentFields">
/// The fields to use as evidence for each index, keyed by scope, in order. An index with no entry
/// uses every non-empty string field.
/// </param>
/// <param name="indexes">Each index's settings, keyed by scope: its fields, semantic ranking and vector fields.</param>
/// <param name="maxEvidenceCharacters">
/// Optional: the most characters of evidence one search returns, added a whole result at a time, most relevant first.
/// Null: every relevant result.
/// </param>
public sealed class AzureSearchContextRetriever(IReadOnlyDictionary<string, SearchClient> clients, IRelevanceFilter relevanceFilter,
    ILogger<AzureSearchContextRetriever>? logger = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? contentFields = null,
    IReadOnlyDictionary<string, AzureSearchIndexOptions>? indexes = null, int? maxEvidenceCharacters = null)
    : IContextRetriever
{
    private readonly ILogger<AzureSearchContextRetriever> _logger = logger ?? NullLogger<AzureSearchContextRetriever>.Instance;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _contentFields =
        contentFields ?? new Dictionary<string, IReadOnlyList<string>>();
    private readonly IReadOnlyDictionary<string, AzureSearchIndexOptions> _indexes =
        indexes ?? new Dictionary<string, AzureSearchIndexOptions>();

    public async Task<ContextResult> GetContextAsync(string scope, string query, int size = 10, ODataFilter filter = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        if (!clients.TryGetValue(scope, out var client))
        {
            throw new InvalidOperationException(string.Format(ErrorMessages.NoAzureSearchClientConfigured, scope));
        }

        var odataFilter = filter.IsEmpty ? null : filter.ToString();
        var relevant = relevanceFilter.Filter(await SearchAsync(client, scope, query, size, odataFilter, cancellationToken).ConfigureAwait(false));
        if (relevant.Count == 0)
        {
            return new ContextResult(string.Format(ErrorMessages.NoAzureSearchInformationFound, scope), HasEvidence: false);
        }

        return new ContextResult(JoinWithinLimit(scope, relevant), HasEvidence: true);
    }

    /// <summary>
    /// Numbers the results as evidence, most relevant first. With a limit, whole results are added until the next would go
    /// over, so no result is cut mid-way and the citations still match. The first result is always kept, cut if needed.
    /// </summary>
    private string JoinWithinLimit(string scope, IReadOnlyList<SearchResultItem> relevant)
    {
        var separator = Environment.NewLine + Environment.NewLine;
        var blocks = relevant.Select((item, i) => $"--- {scope} Evidence {i + 1} ---\n{item.Content}").ToList();
        if (maxEvidenceCharacters is not { } limit || blocks.Sum(static block => block.Length) + separator.Length * (blocks.Count - 1) <= limit)
        {
            return string.Join(separator, blocks);
        }

        var kept = new List<string> { blocks[0].Length <= limit ? blocks[0] : blocks[0][..limit] };
        var length = kept[0].Length;
        foreach (var block in blocks.Skip(1))
        {
            if (length + separator.Length + block.Length > limit)
            {
                break;
            }

            kept.Add(block);
            length += separator.Length + block.Length;
        }

        _logger.KeptResultsWithinLimit(kept.Count, blocks.Count, scope, limit);
        return string.Join(separator, kept) + separator + string.Format(ErrorMessages.EvidenceResultsLeftOut, blocks.Count - kept.Count, limit);
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchAsync(SearchClient client, string scope, string query, int size, string? filter,
        CancellationToken cancellationToken)
    {
        var fields = FieldsFor(scope);
        var index = _indexes.GetValueOrDefault(scope);
        var searchOptions = new SearchOptions { Size = size, Filter = filter };
        foreach (var field in fields)
        {
            searchOptions.Select.Add(field);
        }

        var semantic = index?.SemanticConfiguration is { Length: > 0 };
        if (semantic)
        {
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = index!.SemanticConfiguration };
        }

        if (index?.VectorFields is { Count: > 0 } vectorFields)
        {
            var vector = new VectorizableTextQuery(query) { KNearestNeighborsCount = size };
            foreach (var field in vectorFields)
            {
                vector.Fields.Add(field);
            }

            // Filter first, so the nearest vectors come only from, e.g., the one school asked about.
            searchOptions.VectorSearch = new VectorSearchOptions { Queries = { vector }, FilterMode = VectorFilterMode.PreFilter };
        }

        var results = new List<SearchResultItem>();

        try
        {
            SearchResults<SearchDocument> response = await client.SearchAsync<SearchDocument>(query, searchOptions, cancellationToken)
                .ConfigureAwait(false);

            await foreach (var result in response.GetResultsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var content = ExtractContent(result.Document, fields);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    // The semantic ranker's score reflects meaning; without it, the keyword or hybrid score.
                    results.Add(new SearchResultItem(content, semantic ? result.SemanticSearch?.RerankerScore ?? result.Score : result.Score));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Azure Search query against {Scope} failed", scope);
            throw new InvalidOperationException(string.Format(ErrorMessages.AzureSearchQueryFailed, scope), ex);
        }

        return results;
    }

    private IReadOnlyList<string> FieldsFor(string scope)
    {
        if (_indexes.TryGetValue(scope, out var index) && index.ContentFields.Count > 0)
        {
            return index.ContentFields;
        }

        return _contentFields.TryGetValue(scope, out var fields) ? fields : [];
    }

    private static string ExtractContent(SearchDocument document, IReadOnlyList<string> fields)
    {
        var values = fields.Count > 0
            ? fields.Select(field => document.TryGetValue(field, out var value) ? value : null)
            : document.Select(field => field.Value);

        return string.Join(" ", values.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}

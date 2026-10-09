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
/// <remarks>
/// Each numbered result comes with its citation source (<see cref="ContextResult.Sources"/>): a link when one of its fields
/// holds a full web address, and the start of its text, e.g. "Copthorne School – Key Stage 2 results…". No settings needed.
/// </remarks>
public sealed class AzureSearchContextRetriever(IReadOnlyDictionary<string, SearchClient> clients, IRelevanceFilter relevanceFilter,
    ILogger<AzureSearchContextRetriever>? logger = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? contentFields = null,
    IReadOnlyDictionary<string, AzureSearchIndexOptions>? indexes = null, int? maxEvidenceCharacters = null)
    : IContextRetriever
{
    /// <summary>The longest text a citation shows, taken from the start of its result; longer text is cut with an ellipsis.</summary>
    internal const int MaxCitationTextLength = 80;

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

        var (text, kept) = JoinWithinLimit(scope, relevant);
        return new ContextResult(text, HasEvidence: true)
        {
            Sources = [.. relevant.Take(kept).Select((item, i) => new EvidenceSource(i + 1, item.Name ?? CitationText(item.Content), item.Link))],
        };
    }

    /// <summary>
    /// Numbers the results as evidence, most relevant first. With a limit, whole results are added until the next would go
    /// over, so no result is cut mid-way and the citations still match. The first result is always kept, cut if needed.
    /// </summary>
    /// <returns>The evidence, and how many results it holds (the first that many of <paramref name="relevant"/>).</returns>
    private (string Text, int Kept) JoinWithinLimit(string scope, IReadOnlyList<SearchResultItem> relevant)
    {
        var separator = Environment.NewLine + Environment.NewLine;
        var blocks = relevant.Select((item, i) => $"--- {scope} Evidence {i + 1} ---\n{item.Content}").ToList();
        if (maxEvidenceCharacters is not { } limit || blocks.Sum(static block => block.Length) + separator.Length * (blocks.Count - 1) <= limit)
        {
            return (string.Join(separator, blocks), blocks.Count);
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
        return (string.Join(separator, kept) + separator + string.Format(ErrorMessages.EvidenceResultsLeftOut, blocks.Count - kept.Count, limit),
            kept.Count);
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchAsync(SearchClient client, string scope, string query, int size, string? filter,
        CancellationToken cancellationToken)
    {
        var fields = FieldsFor(scope);
        var searchOptions = BuildSearchOptions(query, size, filter, fields, _indexes.GetValueOrDefault(scope));
        var semantic = searchOptions.QueryType == SearchQueryType.Semantic;
        var results = new List<SearchResultItem>();

        try
        {
            SearchResults<SearchDocument> response = await client.SearchAsync<SearchDocument>(query, searchOptions, cancellationToken)
                .ConfigureAwait(false);

            await foreach (var result in response.GetResultsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (ToItem(result, fields, semantic) is { } item)
                {
                    results.Add(item);
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

    /// <summary>The query: its fields, semantic ranking and vector search, as the index is set up.</summary>
    private static SearchOptions BuildSearchOptions(string query, int size, string? filter, IReadOnlyList<string> fields,
        AzureSearchIndexOptions? index)
    {
        var searchOptions = new SearchOptions { Size = size, Filter = filter };
        foreach (var field in fields)
        {
            searchOptions.Select.Add(field);
        }

        if (index?.SemanticConfiguration is { Length: > 0 } semanticConfiguration)
        {
            searchOptions.QueryType = SearchQueryType.Semantic;
            searchOptions.SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = semanticConfiguration };
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

        return searchOptions;
    }

    /// <summary>
    /// One result as evidence, with its citation link: the first value (in field order) that is wholly a full https or http
    /// address. That value is left out of the evidence, so the model never sees the link. Null when there's no content.
    /// </summary>
    private static SearchResultItem? ToItem(SearchResult<SearchDocument> result, IReadOnlyList<string> fields, bool semantic)
    {
        var values = (fields.Count > 0
                ? fields.Select(field => result.Document.TryGetValue(field, out var value) ? value : null)
                : result.Document.Select(static field => field.Value))
            .OfType<string>()
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var linkValue = values.FirstOrDefault(static value => WebAddress(value) is not null);
        var content = string.Join(" ", values.Where(value => !ReferenceEquals(value, linkValue)));
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        // The semantic ranker's score reflects meaning; without it, the keyword or hybrid score.
        return new SearchResultItem(content, semantic ? result.SemanticSearch?.RerankerScore ?? result.Score : result.Score)
        {
            Link = linkValue is null ? null : WebAddress(linkValue),
        };
    }

    private IReadOnlyList<string> FieldsFor(string scope)
    {
        if (_indexes.TryGetValue(scope, out var index) && index.ContentFields.Count > 0)
        {
            return index.ContentFields;
        }

        return _contentFields.TryGetValue(scope, out var fields) ? fields : [];
    }

    /// <summary>The value as a web address, when the whole value is a full https or http one.</summary>
    private static Uri? WebAddress(string value)
        => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var address)
           && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp)
            ? address
            : null;

    /// <summary>The start of a result's text, on one line, cut at a word with an ellipsis when it's long.</summary>
    internal static string CitationText(string content)
    {
        var text = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= MaxCitationTextLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', MaxCitationTextLength - 1);
        return string.Concat(text.AsSpan(0, cut > MaxCitationTextLength / 2 ? cut : MaxCitationTextLength - 1), "…");
    }
}

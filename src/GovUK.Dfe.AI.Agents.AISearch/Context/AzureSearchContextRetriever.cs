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
/// <param name="renderCitations">
/// Returns each numbered result's name and link as <see cref="ContextResult.Sources"/>, so a run can show its citations.
/// </param>
public sealed class AzureSearchContextRetriever(IReadOnlyDictionary<string, SearchClient> clients, IRelevanceFilter relevanceFilter,
    ILogger<AzureSearchContextRetriever>? logger = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? contentFields = null,
    IReadOnlyDictionary<string, AzureSearchIndexOptions>? indexes = null, int? maxEvidenceCharacters = null, bool renderCitations = false)
    : IContextRetriever
{
    /// <summary>The longest name a citation shows; longer ones are cut with an ellipsis.</summary>
    internal const int MaxCitationNameLength = 200;

    /// <summary>Fields used as a record's name when the index sets no <c>NameField</c>, in order.</summary>
    private static readonly string[] DefaultNameFields = ["title", "name"];

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
            Sources = renderCitations
                ? [.. relevant.Take(kept).Select((item, i) => new EvidenceSource(i + 1, item.Name ?? $"{scope} record {i + 1}", item.Link))]
                : [],
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
        var index = _indexes.GetValueOrDefault(scope);
        var citation = renderCitations ? new CitationFields(Set(index?.LinkField), Set(index?.NameField)) : (CitationFields?)null;
        var searchOptions = BuildSearchOptions(query, size, filter, fields, index, citation);
        var semantic = searchOptions.QueryType == SearchQueryType.Semantic;
        var results = new List<SearchResultItem>();

        try
        {
            SearchResults<SearchDocument> response = await client.SearchAsync<SearchDocument>(query, searchOptions, cancellationToken)
                .ConfigureAwait(false);

            await foreach (var result in response.GetResultsAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (ToItem(result, fields, citation, semantic) is { } item)
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
        AzureSearchIndexOptions? index, CitationFields? citation)
    {
        var searchOptions = new SearchOptions { Size = size, Filter = filter };

        // With no content fields every field comes back, citation fields included; listing only those would drop the content.
        if (fields.Count > 0)
        {
            foreach (var field in fields.Concat([citation?.Link, citation?.Name]).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                searchOptions.Select.Add(field);
            }
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

    /// <summary>One result as evidence, with its citation name and link when they're wanted; null when it has no content.</summary>
    private static SearchResultItem? ToItem(SearchResult<SearchDocument> result, IReadOnlyList<string> fields, CitationFields? citation,
        bool semantic)
    {
        var content = ExtractContent(result.Document, fields, citation?.Link);
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        // The semantic ranker's score reflects meaning; without it, the keyword or hybrid score.
        return new SearchResultItem(content, semantic ? result.SemanticSearch?.RerankerScore ?? result.Score : result.Score)
        {
            Name = citation is { } fieldsForName ? NameOf(result.Document, fieldsForName.Name) : null,
            Link = citation is { } fieldsForLink ? LinkOf(result.Document, fieldsForLink.Link) : null,
        };
    }

    /// <summary>The fields a citation's link and name come from, as the index sets them.</summary>
    private readonly record struct CitationFields(string? Link, string? Name);

    private IReadOnlyList<string> FieldsFor(string scope)
    {
        if (_indexes.TryGetValue(scope, out var index) && index.ContentFields.Count > 0)
        {
            return index.ContentFields;
        }

        return _contentFields.TryGetValue(scope, out var fields) ? fields : [];
    }

    /// <param name="linkField">Left out of the evidence, so the model never sees the link.</param>
    private static string ExtractContent(SearchDocument document, IReadOnlyList<string> fields, string? linkField)
    {
        var values = fields.Count > 0
            ? fields.Where(field => field != linkField).Select(field => document.TryGetValue(field, out var value) ? value : null)
            : document.Where(field => field.Key != linkField).Select(field => field.Value);

        return string.Join(" ", values.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    /// <summary>The record's name: its <c>NameField</c>, else a <c>title</c> or <c>name</c> field it has, on one line and capped.</summary>
    private static string? NameOf(SearchDocument document, string? nameField)
    {
        var candidates = nameField is not null ? [nameField] : DefaultNameFields;
        var name = candidates
            .Select(field => document.FirstOrDefault(pair => string.Equals(pair.Key, field, StringComparison.OrdinalIgnoreCase)).Value)
            .OfType<string>()
            .Select(static value => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .FirstOrDefault(static value => value.Length > 0);

        return name is { Length: > MaxCitationNameLength } ? string.Concat(name.AsSpan(0, MaxCitationNameLength - 1), "…") : name;
    }

    /// <summary>The record's web address, when its <c>LinkField</c> holds an absolute https or http one.</summary>
    private static Uri? LinkOf(SearchDocument document, string? linkField)
        => linkField is not null && document.TryGetValue(linkField, out var value) && value is string text
           && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp)
            ? link
            : null;

    private static string? Set(string? field) => string.IsNullOrWhiteSpace(field) ? null : field.Trim();
}

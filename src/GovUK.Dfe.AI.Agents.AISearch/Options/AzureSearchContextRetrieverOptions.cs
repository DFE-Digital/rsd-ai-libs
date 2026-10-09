namespace GovUK.Dfe.AI.Agents.AISearch.Options;

/// <summary>The <c>AiAgents:AISearch</c> section.</summary>
public sealed class AzureSearchContextRetrieverOptions
{
    public required string Endpoint { get; init; }

    public required IReadOnlyList<AzureSearchIndexOptions> Indexes { get; init; }

    /// <summary>Results scoring below this fraction of the top score are dropped.</summary>
    public double MinimumRelevanceFilter { get; init; } = 0.5;

    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// The most characters of evidence one search returns. Results are added whole, most relevant first, until the next
    /// would go over; the rest are left out, with a note. The default fits within core's <c>MaxEvidenceCharacters</c>
    /// (200,000), so a run never cuts a result mid-way. Raise both together for a model with a larger context.
    /// </summary>
    public int? MaxEvidenceCharacters { get; init; } = DefaultMaxEvidenceCharacters;

    /// <summary>Just under core's 200,000-character evidence limit, leaving room for the note and fencing.</summary>
    public const int DefaultMaxEvidenceCharacters = 190_000;

    /// <summary>
    /// Shows each <c>[Evidence n]</c> in a checked answer as a link to that result's <see cref="AzureSearchIndexOptions.LinkField"/>,
    /// or its name when it has none. Off: citations stay as <c>[Evidence n]</c>. Pass the search's <c>ContextResult</c> to the run.
    /// </summary>
    public bool RenderCitations { get; init; }

    /// <summary>Every index has a name, and no name appears twice.</summary>
    internal bool IndexesAreValid
        => Indexes.Count > 0
           && Indexes.All(index => !string.IsNullOrWhiteSpace(index.Name))
           && Indexes.Select(index => index.Name).Distinct(StringComparer.Ordinal).Count() == Indexes.Count;
}

/// <summary>One index agents can search.</summary>
public sealed class AzureSearchIndexOptions
{
    /// <summary>The index name, also the <c>scope</c> passed to <c>GetContextAsync</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Fields sent to the model, in order (also the search <c>$select</c>). Empty sends every string field,
    /// including ids and URLs.
    /// </summary>
    public IReadOnlyList<string> ContentFields { get; init; } = [];

    /// <summary>
    /// Optional: the index's semantic configuration. Set, the semantic ranker re-orders results by meaning, and weak ones
    /// are dropped by its score. Needs a Basic tier or higher search service.
    /// </summary>
    public string? SemanticConfiguration { get; init; }

    /// <summary>
    /// Optional: vector fields for hybrid search (keyword plus vector). The index's vectorizer turns the query into a
    /// vector, so the app needs no embedding model; the index must have one. <c>filter</c> applies before the vector search.
    /// </summary>
    public IReadOnlyList<string> VectorFields { get; init; } = [];

    /// <summary>
    /// Optional, with <c>RenderCitations</c>: the field holding each record's web address, its citation's link. It's never sent
    /// to the model, so the model can't write links itself. Only absolute https and http addresses are used.
    /// </summary>
    public string? LinkField { get; init; }

    /// <summary>
    /// Optional, with <c>RenderCitations</c>: the field holding each record's name, e.g. its title, shown as its citation. Unset:
    /// a <c>title</c> or <c>name</c> field among those returned, else "{index} record {n}".
    /// </summary>
    public string? NameField { get; init; }
}

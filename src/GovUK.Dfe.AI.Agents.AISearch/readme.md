# GovUK.Dfe.AI.Agents.AISearch

Gives your [GovUK.Dfe.AI.Agents](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md)
agents evidence from Azure AI Search. Results are numbered, so agents must cite them as `[Evidence n]`.

## Set up

```sh
dotnet add package GovUK.Dfe.AI.Agents.AISearch
```

```json
"AiAgents": {
  "Search": {
    "Endpoint": "https://<search>.search.windows.net",
    "Indexes": [ { "Name": "ofsted_index", "ContentFields": [ "title", "content" ] } ]
  }
}
```

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddAISearch());
```

Give the app's identity the **Search Index Data Reader** role. To use a different identity for search, add
`Search:Authentication`.

## Use

Inject `IContextRetriever`, search, and pass the result as evidence:

```csharp
var evidence = await search.GetContextAsync("ofsted_index", $"Ofsted inspection {urn}", size: 10,
    filter: $"urn eq {urn}", cancellationToken: ct);

var result = await agents.RunAsync(BriefingAgents.Ofsted, "Summarise the latest inspection.", evidence.Text, ct);
```

- **Filter every search** to the record you mean, e.g. one school. Relevance alone also returns similarly named ones.
- **Filters are escaped for you:** write them as interpolated strings, and every `{value}` is escaped, so a value can't
  change what the filter matches. A plain or concatenated string won't compile. If you already have an escaped
  filter (e.g. from `SearchFilter.Create`), pass it with `ODataFilter.Raw(filter)`.
- **Set `ContentFields`** on each index. Otherwise every string field, including ids and URLs, is sent to the model.
- **Weak matches are dropped:** results scoring below half the top score are removed. Change this with
  `Search:MinimumRelevanceFilter` (default `0.5`).

## Better matches: semantic and hybrid search

Keyword search misses documents that use different words, e.g. "safeguarding concerns" and "child protection issues".
Set either or both of these on an index:

```json
"Indexes": [ {
  "Name": "ofsted_index",
  "ContentFields": [ "title", "content" ],
  "SemanticConfiguration": "default",
  "VectorFields": [ "contentVector" ]
} ]
```

| Setting | Search type | The index needs |
| --- | --- | --- |
| Neither | Keyword | Nothing extra |
| `SemanticConfiguration` | Keyword, re-ranked by meaning | A semantic configuration, and the semantic ranker enabled (Basic tier or higher) |
| `VectorFields` | Hybrid: keyword plus vector | A vectorizer, so the service embeds the query (your app needs no embedding model) |
| Both | Hybrid, re-ranked by meaning (best results) | Both of the above |

Your `filter` is applied before the vector search, so hybrid results still match only the record you asked about.

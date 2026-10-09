# GovUK.Dfe.AI.Agents.AISearch

Gives your [GovUK.Dfe.AI.Agents](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md)
agents evidence from Azure AI Search. Results are numbered, so agents must cite them as `[Evidence n]`.

## Set up

```sh
dotnet add package GovUK.Dfe.AI.Agents.AISearch
```

```json
"AiAgents": {
  "AISearch": {
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
`AISearch:Authentication`, or set one in code with `agents.UseAISearchCredential(credential)`.

### Search without agents

An app that only needs search results, e.g. a search page, can add search on its own. It needs no Foundry settings and
no agents:

```csharp
builder.Services.AddAISearch(builder.Configuration, new ManagedIdentityCredential());
```

It reads the same `AiAgents:AISearch` section. Pass a credential, or leave it out and set a complete
`AiAgents:AISearch:Authentication` block (`TenantId`, `ClientId`, `ClientSecret`). If you later add agents with
`agents.AddAISearch()`, search is still registered only once.

## Use

Inject `IContextRetriever`, search, and pass the result as evidence:

```csharp
var evidence = await search.GetContextAsync("ofsted_index", $"Ofsted inspection {urn}", size: 10,
    filter: $"urn eq {urn}", cancellationToken: ct);

var result = await agents.RunAsync(BriefingAgents.Ofsted, "Summarise the latest inspection.", evidence, ct);
```

Pass the result itself, not `evidence.Text`, so its [citation sources](#citations-as-links) come with it.

- **Filter every search** to the record you mean, e.g. one school. Relevance alone also returns similarly named ones.
- **Filters are escaped for you:** write them as interpolated strings, and every `{value}` is escaped, so a value can't
  change what the filter matches. A plain or concatenated string won't compile. If you already have an escaped
  filter (e.g. from `SearchFilter.Create`), pass it with `ODataFilter.Raw(filter)`.
- **Set `ContentFields`** on each index. Otherwise every string field, including ids and URLs, is sent to the model.
- **Weak matches are dropped:** results scoring below half the top score are removed. Change this with
  `AISearch:MinimumRelevanceFilter` (default `0.5`).
- **Large results:** evidence is capped at `AISearch:MaxEvidenceCharacters` (default 190,000, just under core's
  200,000). Whole results are kept, most relevant first, and the rest are left out with a note, so no result is cut
  mid-way and citations still match. Raise both limits together for a larger model. To fetch less, lower `size`.

## Citations as links

Agents cite results as `[Evidence n]`. To show each citation as a link to its record, or the record's name when it has
no web address, turn on `RenderCitations` and name each index's fields:

```json
"AISearch": {
  "RenderCitations": true,
  "Indexes": [ { "Name": "ofsted_index", "ContentFields": [ "title", "content" ], "LinkField": "url", "NameField": "title" } ]
}
```

`Rated Good [Evidence 1].` then reaches your app as
`Rated Good <a href="https://reports.ofsted.gov.uk/...">Ofsted report, March 2024</a>.`

- **After the checks:** citations are rewritten only once the answer passes, so every one refers to real evidence.
  `Validate` and run observers still see `[Evidence n]`.
- **Safe:** the link field is never sent to the model, so it can't write links itself. Only absolute `https` and `http`
  addresses become links, and names and links are HTML-encoded, as they come from the index.
- **Names:** `NameField`, else a `title` or `name` field among those returned, else "ofsted_index record 3". Long names
  are cut to 200 characters.
- **Your app shows HTML:** render the answer as HTML, or as Markdown that allows inline HTML. It's safe: the citation
  links are the only HTML in an answer, as any the model writes is shown as text.
- Works for `RunAsync`, `RunStreamingAsync` (each piece) and `RunParallelAsync` (return the search result from
  `resolveEvidence`). Evidence joined from several searches has no sources, as their numbers would clash.

| Setting               | Default           | Purpose                                     |
| --------------------- | ----------------- | ------------------------------------------- |
| `RenderCitations`     | `false`           | Shows citations as links or names           |
| `Indexes:n:LinkField` | None              | The field holding each record's web address |
| `Indexes:n:NameField` | `title` or `name` | The field holding each record's name        |

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

| Setting                 | Search type                                 | The index needs                                                                   |
| ----------------------- | ------------------------------------------- | --------------------------------------------------------------------------------- |
| Neither                 | Keyword                                     | Nothing extra                                                                     |
| `SemanticConfiguration` | Keyword, re-ranked by meaning               | A semantic configuration, and the semantic ranker enabled (Basic tier or higher)  |
| `VectorFields`          | Hybrid: keyword plus vector                 | A vectorizer, so the service embeds the query (your app needs no embedding model) |
| Both                    | Hybrid, re-ranked by meaning (best results) | Both of the above                                                                 |

Your `filter` is applied before the vector search, so hybrid results still match only the record you asked about.

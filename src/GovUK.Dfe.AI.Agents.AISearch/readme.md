# GovUK.Dfe.AI.Agents.AISearch

Gives your [GovUK.Dfe.AI.Agents](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md)
agents evidence from Azure AI Search. Results are numbered, and each citation in an answer becomes a link to its result,
or the result's text.

## Set up

```sh
dotnet add package GovUK.Dfe.AI.Agents.AISearch
```

```json
"AiAgents": {
  "AISearch": {
    "Endpoint": "https://<search>.search.windows.net",
    "Indexes": [ { "Name": "ofsted_index", "ContentFields": [ "title", "content", "url" ] } ]
  }
}
```

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddAISearch());
```

**You need** an Azure AI Search service, and **Search Index Data Reader** on it for the app's identity. For a separate
identity, add `AISearch:Authentication` or call `agents.UseAISearchCredential(credential)`.

### Search without agents

An app that only needs search results, e.g. a search page, needs no Foundry settings and no agents:

```csharp
builder.Services.AddAISearch(builder.Configuration, new ManagedIdentityCredential());
```

It reads the same section, with the credential you pass or `AISearch:Authentication`. Adding agents later with
`agents.AddAISearch()` still registers search once.

## Use

Inject `IContextRetriever`, search, and pass the result as evidence:

```csharp
var evidence = await search.GetContextAsync("ofsted_index", $"Ofsted inspection {urn}", size: 10,
    filter: $"urn eq {urn}", cancellationToken: ct);

var result = await agents.RunAsync(BriefingAgents.Ofsted, "Summarise the latest inspection.", evidence, ct);
```

Pass the result itself, not `evidence.Text`, so its [citation sources](#citations) come with it.

- **Filter every search** to the record you mean, e.g. one school; relevance alone also returns similar names.
- **Filters are escaped for you:** each `{value}` in `$"urn eq {urn}"` is escaped, so it can't change the filter. A
  plain string won't compile; use `ODataFilter.Raw(filter)` for one that's already escaped.
- **Set `ContentFields`** on each index, so only those fields (of any type) reach the model, each labelled one per line,
  e.g. `number_on_roll: 398`.
- **Weak matches are dropped:** results below half the top score (`MinimumRelevanceFilter`).
- **Large results:** whole results are kept, most relevant first, up to `MaxEvidenceCharacters`; the rest are left out
  with a note, so citations still match.

## Citations

Agents cite results as `[Evidence n]`. Each one reaches your app as something a reader can use, with no settings:

| The result has                     | `Rated Good [Evidence 1].` becomes                                                              |
| ---------------------------------- | ----------------------------------------------------------------------------------------------- |
| A field holding a full web address | `Rated Good [<a href="https://reports.ofsted.gov.uk/...">Copthorne School – Key Stage 2…</a>].` |
| No web address                     | `Rated Good [Copthorne School – Key Stage 2…].`                                                 |

- **Text:** the start of the result, on one line, about 80 characters, in square brackets.
- **Link:** the first field whose whole value is an `https` or `http` address. It isn't sent to the model, so the model
  can't write links. With `ContentFields` set, include that field (e.g. `url`).
- **Render answers as HTML** (or Markdown that allows it): citation links are the only real HTML.
- **`RequiredCitations = false`:** citations aren't asked for, and any written are removed.
- Works for `RunAsync`, `RunStreamingAsync` and `RunParallelAsync` (return the search result from `resolveEvidence`).
  Evidence joined from several searches keeps `[Evidence n]`.

## Better matches: semantic and hybrid search

Keyword search misses different wording, e.g. "safeguarding concerns" and "child protection issues". Set either or
both on an index:

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

Your `filter` is applied before the vector search, so hybrid results still match only that record.

## Settings

All under `AiAgents:AISearch`.

| Setting                           | Default              | Purpose                                                                        |
| --------------------------------- | -------------------- | ------------------------------------------------------------------------------ |
| `Endpoint`                        | Required             | The search service, e.g. `https://<search>.search.windows.net`                 |
| `Indexes`                         | Required             | Each index's `Name` (the `scope` you search), plus the settings below          |
| `Indexes:n:ContentFields`         | Every string field   | The fields sent to the model, in order                                         |
| `Indexes:n:SemanticConfiguration` | None                 | Re-rank by meaning (Basic tier or higher)                                      |
| `Indexes:n:VectorFields`          | None                 | Hybrid keyword and vector search                                               |
| `Indexes:n:LabelFields`           | `true`               | Labels each value with its field, one per line; `false` sends the values alone |
| `MinimumRelevanceFilter`          | `0.5`                | Drops results below this share of the top score                                |
| `MaxEvidenceCharacters`           | 190,000              | Evidence per search, whole results only; just under core's 200,000             |
| `MaxRetryAttempts`                | 3                    | Retries of a failed search                                                     |
| `Authentication`                  | The default identity | A separate identity for search                                                 |

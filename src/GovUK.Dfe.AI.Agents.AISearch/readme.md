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

Give the app's identity the **Search Index Data Reader** role. For a separate identity, add `AISearch:Authentication`
or call `agents.UseAISearchCredential(credential)`.

### Search without agents

An app that only needs search results, e.g. a search page, needs no Foundry settings and no agents:

```csharp
builder.Services.AddAISearch(builder.Configuration, new ManagedIdentityCredential());
```

It reads the same section. Pass a credential, or set a complete `AISearch:Authentication` block. Adding agents later with
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
- **Filters are escaped for you:** in `$"urn eq {urn}"`, every `{value}` is escaped, so it can't change what the filter
  matches. A plain string won't compile; pass an already-escaped filter with `ODataFilter.Raw(filter)`.
- **Set `ContentFields`** on each index, so only those fields reach the model, whatever their type (text, numbers,
  dates, true/false). Each value is labelled with its field, one per line, e.g. `number_on_roll: 398`, so the model can
  tell similar numbers apart.
- **Weak matches are dropped:** results below half the top score (`MinimumRelevanceFilter`).
- **Large results:** whole results are kept, most relevant first, up to `MaxEvidenceCharacters`; the rest are left out
  with a note, so citations still match. To fetch less, lower `size`.

## Citations

Agents cite results as `[Evidence n]`. Each one reaches your app as something a reader can use, with no settings:

| The result has                     | `Rated Good [Evidence 1].` becomes                                                              |
| ---------------------------------- | ----------------------------------------------------------------------------------------------- |
| A field holding a full web address | `Rated Good [<a href="https://reports.ofsted.gov.uk/...">Copthorne School – Key Stage 2…</a>].` |
| No web address                     | `Rated Good [Copthorne School – Key Stage 2…].`                                                 |

- **Text:** the start of the result, on one line, cut at about 80 characters, in square brackets.
- **Link:** the first field whose whole value is a full `https` or `http` address. It isn't sent to the model, so the
  model can't write links itself. With `ContentFields` set, include the address field (e.g. `url`).
- **Render answers as HTML** (or Markdown that allows HTML). It's safe: citation links are the only real HTML.
- **No citations** for an agent with `RequiredCitations = false`: they aren't asked for, and any written are removed.
- Citations change only after the answer is checked. Works for `RunAsync`, `RunStreamingAsync` and `RunParallelAsync`
  (return the search result from `resolveEvidence`). Evidence joined from several searches keeps `[Evidence n]`.

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

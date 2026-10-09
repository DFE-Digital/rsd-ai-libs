using Azure.Search.Documents.Models;
using Azure.Search.Documents;
using Azure;
using GovUK.Dfe.AI.Agents.AISearch.Context;
using GovUK.Dfe.AI.Agents.AISearch.Filters;
using GovUK.Dfe.AI.Agents.AISearch.Options;
using GovUK.Dfe.AI.Agents.Context;
using GovUK.Dfe.AI.Agents.ValueObjects;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.AISearch.Tests;

/// <summary>Search evidence: keyword, semantic and hybrid queries, fields, numbering and relevance filtering.</summary>
public sealed class AzureSearchContextRetrieverTests
{
    private static readonly string[] EstablishmentFields = ["name", "summary"];
    private static readonly string[] InspectionFields = ["content"];

    private const string Scope = "establishment";

    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private readonly SearchClient _client = Substitute.For<SearchClient>();
    private readonly RelativeScoreRelevanceFilter _relevanceFilter = new();

    private AzureSearchContextRetriever CreateSut()
        => new(new Dictionary<string, SearchClient> { [Scope] = _client }, _relevanceFilter);

    private void SetUpSearchResults(params (string content, double? score)[] items)
    {
        var documents = items.Select(item =>
        {
            var document = new SearchDocument { ["content"] = item.content };
            return SearchModelFactory.SearchResult(document, item.score, highlights: null);
        });

        var results = SearchModelFactory.SearchResults(
            values: documents, totalCount: items.Length, facets: null, coverage: null, rawResponse: null!);

        _client.SearchAsync<SearchDocument>(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(results, null!));
    }

    [Fact]
    public async Task GetContextAsync_WithSemanticRankingAndVectorFields_RunsAHybridQueryFilteredFirst_AndKeepsWhatMatchesByMeaning()
    {
        SearchOptions? sent = null;
        var documents = new[]
        {
            // Keyword scores are close; the semantic ranker tells them apart.
            SearchModelFactory.SearchResult(new SearchDocument { ["content"] = "Safeguarding concerns raised in 2024." }, 10.0,
                highlights: null, SearchModelFactory.SemanticSearchResult(3.5, captions: null)),
            SearchModelFactory.SearchResult(new SearchDocument { ["content"] = "The school's safeguarding lead changed." }, 9.0,
                highlights: null, SearchModelFactory.SemanticSearchResult(0.5, captions: null)),
        };
        _client.SearchAsync<SearchDocument>(Arg.Any<string>(), Arg.Do<SearchOptions>(options => sent = options), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(SearchModelFactory.SearchResults(documents, 2, null, null, null!), null!));
        var index = new AzureSearchIndexOptions
        {
            Name = Scope,
            ContentFields = InspectionFields,
            SemanticConfiguration = "default",
            VectorFields = ["contentVector"],
        };
        var sut = new AzureSearchContextRetriever(new Dictionary<string, SearchClient> { [Scope] = _client }, _relevanceFilter,
            indexes: new Dictionary<string, AzureSearchIndexOptions> { [Scope] = index });

        var result = await sut.GetContextAsync(Scope, "concerns about safeguarding", size: 5, filter: $"urn eq {"100000"}",
            cancellationToken: cancellationToken);

        Assert.Contains("Safeguarding concerns raised in 2024.", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("safeguarding lead changed", result.Text, StringComparison.Ordinal);   // below half the top reranker score
        Assert.Equal(SearchQueryType.Semantic, sent!.QueryType);
        Assert.Equal("default", sent.SemanticSearch.SemanticConfigurationName);
        var vector = Assert.IsType<VectorizableTextQuery>(Assert.Single(sent.VectorSearch.Queries));
        Assert.Equal(("concerns about safeguarding", 5, "contentVector"), (vector.Text, vector.KNearestNeighborsCount!.Value, Assert.Single(vector.Fields)));
        Assert.Equal((VectorFilterMode.PreFilter, "urn eq '100000'"), (sent.VectorSearch.FilterMode!.Value, sent.Filter));
    }

    [Fact]
    public async Task GetContextAsync_ThrowsInvalidOperationException_WhenScopeNotConfigured()
    {
        var sut = CreateSut();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GetContextAsync("unknown-scope", "query", cancellationToken: cancellationToken));
    }

    [Theory]
    [InlineData("", "query")]
    [InlineData("  ", "query")]
    [InlineData(Scope, "")]
    [InlineData(Scope, "  ")]
    public async Task GetContextAsync_ThrowsArgumentException_WhenScopeOrQueryIsEmpty(string scope, string query)
    {
        var sut = CreateSut();

        await Assert.ThrowsAsync<ArgumentException>(() => sut.GetContextAsync(scope, query, cancellationToken: cancellationToken));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetContextAsync_ThrowsArgumentOutOfRangeException_WhenSizeIsNotPositive(int size)
    {
        var sut = CreateSut();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sut.GetContextAsync(Scope, "query", size, cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task GetContextAsync_ReturnsNoEvidence_WhenSearchReturnsNoResults()
    {
        SetUpSearchResults();
        var sut = CreateSut();

        var result = await sut.GetContextAsync(Scope, "query", cancellationToken: cancellationToken);

        Assert.False(result.HasEvidence);
    }

    [Fact]
    public async Task GetContextAsync_FormatsOnlyResultsThatPassTheRealRelevanceFilter()
    {
        SetUpSearchResults(("strong match", 1.0), ("borderline match", 0.5), ("weak match", 0.1));
        var sut = CreateSut();

        var result = await sut.GetContextAsync(Scope, "query", cancellationToken: cancellationToken);

        Assert.True(result.HasEvidence);
        Assert.Contains("--- establishment Evidence 1 ---", result.Text);
        Assert.Contains("strong match", result.Text);
        Assert.Contains("--- establishment Evidence 2 ---", result.Text);
        Assert.Contains("borderline match", result.Text);
        Assert.DoesNotContain("weak match", result.Text);
    }

    [Fact]
    public async Task GetContextAsync_ExtractsOnlyStringFields_FromEachDocument()
    {
        var document = new SearchDocument
        {
            ["title"] = "A title",
            ["content"] = "Body text",
            ["pageCount"] = 42,
            ["publishedAt"] = null!,
        };
        var results = SearchModelFactory.SearchResults(
            values: [SearchModelFactory.SearchResult(document, 1.0, highlights: null)],
            totalCount: 1, facets: null, coverage: null, rawResponse: null!);
        _client.SearchAsync<SearchDocument>(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(results, null!));

        var sut = CreateSut();
        var result = await sut.GetContextAsync(Scope, "query", cancellationToken: cancellationToken);

        Assert.Contains("A title", result.Text);
        Assert.Contains("Body text", result.Text);
        Assert.DoesNotContain("42", result.Text);
    }

    [Fact]
    public async Task GetContextAsync_DropsResultsWithNoExtractableContent()
    {
        var emptyDocument = new SearchDocument { ["pageCount"] = 1 };
        var usableDocument = new SearchDocument { ["content"] = "usable content" };
        var results = SearchModelFactory.SearchResults(
            values:
            [
                SearchModelFactory.SearchResult(emptyDocument, 1.0, highlights: null),
                SearchModelFactory.SearchResult(usableDocument, 1.0, highlights: null),
            ],
            totalCount: 2, facets: null, coverage: null, rawResponse: null!);
        _client.SearchAsync<SearchDocument>(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(results, null!));

        var sut = CreateSut();
        var result = await sut.GetContextAsync(Scope, "query", cancellationToken: cancellationToken);

        Assert.Contains("--- establishment Evidence 1 ---", result.Text);
        Assert.Contains("usable content", result.Text);
        Assert.DoesNotContain("Evidence 2", result.Text);
    }

    [Fact]
    public async Task GetContextAsync_PassesRequestedSize_ToSearchOptions()
    {
        SetUpSearchResults(("some content", 1.0));
        var sut = CreateSut();

        await sut.GetContextAsync(Scope, "query", size: 7, cancellationToken: cancellationToken);

        await _client.Received(1).SearchAsync<SearchDocument>(
            "query", Arg.Is<SearchOptions>(o => o.Size == 7), cancellationToken);
    }

    [Fact]
    public async Task GetContextAsync_UsesEachIndexsOwnContentFields_ForSelectAndForTheEvidence()
    {
        var establishments = Substitute.For<SearchClient>();
        var inspections = Substitute.For<SearchClient>();
        StubDocument(establishments, new SearchDocument { ["id"] = "est-1", ["name"] = "Oak Primary", ["summary"] = "Academy, 420 pupils." });
        StubDocument(inspections, new SearchDocument { ["id"] = "insp-9", ["content"] = "Rated Good in March 2024.", ["url"] = "https://example" });

        var sut = new AzureSearchContextRetriever(
            new Dictionary<string, SearchClient> { ["establishment_index"] = establishments, ["ofsted_index"] = inspections },
            _relevanceFilter,
            contentFields: new Dictionary<string, IReadOnlyList<string>>
            {
                ["establishment_index"] = ["name", "summary"],
                ["ofsted_index"] = ["content"],
            });

        var establishment = await sut.GetContextAsync("establishment_index", "Oak", cancellationToken: cancellationToken);
        var inspection = await sut.GetContextAsync("ofsted_index", "Oak", cancellationToken: cancellationToken);

        Assert.Contains("Oak Primary Academy, 420 pupils.", establishment.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("est-1", establishment.Text, StringComparison.Ordinal);
        Assert.Contains("Rated Good in March 2024.", inspection.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example", inspection.Text, StringComparison.Ordinal);

        await establishments.Received(1).SearchAsync<SearchDocument>("Oak",
            Arg.Is<SearchOptions>(o => o.Select.SequenceEqual(EstablishmentFields)), cancellationToken);
        await inspections.Received(1).SearchAsync<SearchDocument>("Oak",
            Arg.Is<SearchOptions>(o => o.Select.SequenceEqual(InspectionFields)), cancellationToken);
    }

    [Fact]
    public async Task GetContextAsync_UsesEveryStringField_AndSelectsNothing_ForAnIndexWithNoContentFields()
    {
        StubDocument(_client, new SearchDocument { ["title"] = "Oak Primary", ["content"] = "Rated Good." });
        var sut = new AzureSearchContextRetriever(new Dictionary<string, SearchClient> { [Scope] = _client }, _relevanceFilter,
            contentFields: new Dictionary<string, IReadOnlyList<string>> { ["some_other_index"] = ["name"] });

        var result = await sut.GetContextAsync(Scope, "Oak", cancellationToken: cancellationToken);

        Assert.Contains("Oak Primary", result.Text, StringComparison.Ordinal);
        Assert.Contains("Rated Good.", result.Text, StringComparison.Ordinal);
        await _client.Received(1).SearchAsync<SearchDocument>("Oak", Arg.Is<SearchOptions>(o => o.Select.Count == 0), cancellationToken);
    }

    private static void StubDocument(SearchClient client, SearchDocument document)
    {
        var results = SearchModelFactory.SearchResults(
            values: [SearchModelFactory.SearchResult(document, 1.0, highlights: null)],
            totalCount: 1, facets: null, coverage: null, rawResponse: null!);
        client.SearchAsync<SearchDocument>(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(results, null!));
    }

    [Fact]
    public void RelevanceFilter_FallsBackToUnfiltered_WhenNoResultHasAUsableScore()
    {
        SearchResultItem[] input = [new("result one", Score: null), new("result two", Score: null)];

        Assert.Equal(input, _relevanceFilter.Filter(input));
    }

    // ===================== Evidence size =====================

    [Fact]
    public async Task WithALimit_WholeResultsAreKept_MostRelevantFirst_AndTheRestAreLeftOutWithANote()
    {
        SetUpSearchResults(("first result about the school", 1.0), ("second result about the school", 0.9), ("third result", 0.8));
        var sut = new AzureSearchContextRetriever(new Dictionary<string, SearchClient> { [Scope] = _client }, _relevanceFilter,
            maxEvidenceCharacters: 140);

        var text = (await sut.GetContextAsync(Scope, "query", cancellationToken: cancellationToken)).Text;

        Assert.Contains("first result about the school", text);
        Assert.Contains("second result about the school", text);   // whole, never cut mid-way
        Assert.DoesNotContain("third result", text);
        Assert.EndsWith("[1 less relevant result(s) left out to keep the evidence within 140 characters.]", text);
    }

    [Fact]
    public async Task WithALimitSmallerThanTheTopResult_TheTopResultIsCut_SoThereIsAlwaysSomeEvidence()
    {
        SetUpSearchResults((new string('x', 500), 1.0), ("second", 0.9));
        var sut = new AzureSearchContextRetriever(new Dictionary<string, SearchClient> { [Scope] = _client }, _relevanceFilter,
            maxEvidenceCharacters: 100);

        var text = (await sut.GetContextAsync(Scope, "query", cancellationToken: cancellationToken)).Text;

        Assert.StartsWith("--- establishment Evidence 1 ---", text);
        Assert.DoesNotContain("second", text);
        Assert.Contains("[1 less relevant result(s) left out", text);
    }

    [Fact]
    public async Task WithoutALimit_EveryRelevantResultIsReturned()
    {
        SetUpSearchResults((new string('a', 5_000), 1.0), (new string('b', 5_000), 0.9));

        var text = (await CreateSut().GetContextAsync(Scope, "query", cancellationToken: cancellationToken)).Text;

        Assert.Contains(new string('b', 5_000), text);
        Assert.DoesNotContain("left out", text);
    }

    // ===================== Citation sources =====================

    /// <summary>The options of the last search, as the search service received them.</summary>
    private SearchOptions? _sent;

    private void SetUpDocuments(params SearchDocument[] documents)
    {
        var results = SearchModelFactory.SearchResults(
            values: documents.Select(document => SearchModelFactory.SearchResult(document, 1.0, highlights: null)),
            totalCount: documents.Length, facets: null, coverage: null, rawResponse: null!);
        _client.SearchAsync<SearchDocument>(Arg.Any<string>(), Arg.Do<SearchOptions>(options => _sent = options), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(results, null!));
    }

    [Fact]
    public async Task EachResult_IsCitedByItsTextAndAnyWebAddressItHas_AndTheAddressIsNeverSentToTheModel()
    {
        SetUpDocuments(
            new SearchDocument
            {
                ["title"] = "Copthorne School", ["content"] = "Key Stage 2: 72% met the standard.",
                ["url"] = "https://www.compare-school-performance.service.gov.uk/school/100000",
            },
            new SearchDocument { ["content"] = "Part of a trust of 12 academies.", ["page"] = "javascript:alert(1)" });

        var result = await CreateSut().GetContextAsync(Scope, "query", cancellationToken: cancellationToken);

        Assert.Equal(
        [
            new EvidenceSource(1, "Copthorne School Key Stage 2: 72% met the standard.",
                new Uri("https://www.compare-school-performance.service.gov.uk/school/100000")),
            new EvidenceSource(2, "Part of a trust of 12 academies. javascript:alert(1)"),   // not a web address, so no link
        ], result.Sources);
        Assert.DoesNotContain("compare-school-performance", result.Text, StringComparison.Ordinal);
        Assert.Empty(_sent!.Select);   // no settings: every field comes back
    }

    [Fact]
    public async Task ALongResult_IsCitedByItsStart_OnOneLine_CutAtAWord()
    {
        SetUpDocuments(new SearchDocument { ["content"] = "Copthorne School\n  Key Stage 2 results " + string.Join(' ', Enumerable.Repeat("improving", 20)) });

        var text = Assert.Single((await CreateSut().GetContextAsync(Scope, "query", cancellationToken: cancellationToken)).Sources).Name;

        Assert.StartsWith("Copthorne School Key Stage 2 results improving", text, StringComparison.Ordinal);
        Assert.EndsWith("improving…", text, StringComparison.Ordinal);
        Assert.InRange(text.Length, 1, AzureSearchContextRetriever.MaxCitationTextLength);
    }

    [Fact]
    public async Task OnlyResultsKeptWithinTheEvidenceLimit_HaveSources_SoNumbersStillMatch()
    {
        SetUpDocuments(new SearchDocument { ["content"] = new string('a', 60) }, new SearchDocument { ["content"] = new string('b', 60) });
        var sut = new AzureSearchContextRetriever(new Dictionary<string, SearchClient> { [Scope] = _client }, _relevanceFilter, maxEvidenceCharacters: 100);

        var result = await sut.GetContextAsync(Scope, "query", cancellationToken: cancellationToken);

        Assert.Equal(1, Assert.Single(result.Sources).Number);
        Assert.DoesNotContain("Evidence 2", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithContentFields_OnlyThoseFieldsAreFetched_SoALinkMustBeOneOfThem()
    {
        SetUpDocuments(new SearchDocument { ["content"] = "Rated Good.", ["url"] = "https://reports.ofsted.gov.uk/provider/21/100000" });
        var sut = new AzureSearchContextRetriever(new Dictionary<string, SearchClient> { [Scope] = _client }, _relevanceFilter,
            indexes: new Dictionary<string, AzureSearchIndexOptions> { [Scope] = new() { Name = Scope, ContentFields = ["content", "url"] } });

        var result = await sut.GetContextAsync(Scope, "query", cancellationToken: cancellationToken);

        Assert.Equal(["content", "url"], _sent!.Select);
        Assert.Equal(new Uri("https://reports.ofsted.gov.uk/provider/21/100000"), Assert.Single(result.Sources).Link);
    }
}

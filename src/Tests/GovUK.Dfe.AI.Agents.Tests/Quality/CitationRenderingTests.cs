using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Quality;

/// <summary>Each <c>[Evidence n]</c> becomes a safe link to its source, or its name, the same way every time.</summary>
public sealed class CitationRenderingTests
{
    private const string ReportLink = """<a href="https://reports.ofsted.gov.uk/provider/21/100000">Ofsted report, March 2024</a>""";
    private static readonly EvidenceSource Report = new(1, "Ofsted report, March 2024", new Uri("https://reports.ofsted.gov.uk/provider/21/100000"));
    private static readonly EvidenceSource Record = new(2, "Trust record");

    [Fact]
    public void ACitationWithALink_BecomesALink_OneWithout_BecomesItsName_AndEachReadsTheSameEverywhere()
        => Assert.Equal(
            $"Rated Good {ReportLink}; 12 academies Trust record. Since 2024 {ReportLink}.",
            Citations.Render("Rated Good [Evidence 1]; 12 academies [Evidence 2]. Since 2024 [Evidence 1].", [Report, Record]));

    [Fact]
    public void NamesAndLinks_AreHtmlEncoded_AsTheyComeFromSearchResults()
    {
        var hostile = new EvidenceSource(1, "<script>alert(1)</script> & \"Co\"", new Uri("https://example.org/a?b=1&c=\"x\""));

        var rendered = Citations.Render("See [Evidence 1].", [hostile])!;

        Assert.DoesNotContain("<script>", rendered, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;Co&quot;", rendered, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.org/a?b=1&amp;c=%22x%22\"", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("ftp://files.example.org/report")]
    public void ALinkThatIsntAWebAddress_IsNeverShown_OnlyTheName(string link)
        => Assert.Equal("See Trust record.", Citations.Render("See [Evidence 1].", [new EvidenceSource(1, "Trust record", new Uri(link))]));

    [Theory]
    [InlineData(true)]    // a citation with no source among others
    [InlineData(false)]   // no sources at all
    public void ACitationWithoutASource_IsLeftAsTheModelWroteIt(bool otherSources)
        => Assert.Equal("Good [Evidence 3].", Citations.Render("Good [Evidence 3].", otherSources ? [Report] : []));

    // ===================== AgentEvidence =====================

    [Fact]
    public void ASearchResult_BringsItsSources_AndPlainTextHasNone()
    {
        AgentEvidence fromSearch = new ContextResult("--- ofsted_index Evidence 1 ---\nRated Good.", HasEvidence: true) { Sources = [Report] };
        AgentEvidence fromText = "Rated Good.";

        Assert.Equal([Report], fromSearch.Sources);
        Assert.Empty(fromText.Sources);
        Assert.Null((AgentEvidence?)(string?)null);
    }

    [Theory]
    [InlineData(0, "Report", false)]   // no number 0
    [InlineData(1, " ", false)]        // a name is needed to show it
    [InlineData(1, "Report", true)]    // ambiguous: two sources for one number
    public void SourcesThatCantBeShownUnambiguously_AreRejected(int number, string name, bool twice)
    {
        EvidenceSource[] sources = twice ? [new(number, name), new(number, "Another")] : [new(number, name)];

        Assert.Throws<ArgumentException>(() => new AgentEvidence("text", sources));
    }
}

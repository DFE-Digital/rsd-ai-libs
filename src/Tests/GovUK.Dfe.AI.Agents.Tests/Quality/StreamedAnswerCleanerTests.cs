using GovUK.Dfe.AI.Agents.Quality;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Quality;

/// <summary>A streamed answer is cleaned exactly as a whole one, however it's split, and an image never gets through.</summary>
public sealed class StreamedAnswerCleanerTests
{
    [Theory]
    [InlineData("Rated Good ![chart](https://attacker.example/?d=1) overall.")]
    [InlineData("Done <img src=\"https://attacker.example/?d=1\"> now.")]
    [InlineData("Rated Good [Evidence 1](evidence/1) and [Evidence 2].")]
    [InlineData("See [GOV.UK](https://www.gov.uk) or [the report](files/report.pdf).")]
    [InlineData("Wow! Great results! [Evidence 3] confirms it!")]
    [InlineData("Unclosed [bracket at the end")]
    [InlineData("Rated Good<script>alert(1)</script> overall.")]
    [InlineData("Attendance is < 90% and 3<5, then <b>bold</b>.")]
    public void SplitAnyWay_TheResultMatchesCleaningTheWholeAnswer(string answer)
    {
        foreach (var size in new[] { 1, 2, 5, 11 })
        {
            var cleaner = new StreamedAnswerCleaner();
            var pieces = Chunk(answer, size).Select(cleaner.Add).ToList();
            pieces.Add(cleaner.Flush());

            Assert.Equal(AnswerLinks.Clean(answer), string.Concat(pieces));
            Assert.DoesNotContain(pieces, piece => piece.Contains("attacker", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Rated Good")]
    [InlineData("Attendance is < 90% overall")]   // a "<" that can't start a tag doesn't hold back the rest
    public void TextThatCantStartAnImageLinkOrTag_IsPassedOnStraightAway(string text)
        => Assert.Equal(text, new StreamedAnswerCleaner().Add(text));

    [Fact]
    public void TextAfterAPossibleImage_WaitsUntilTheImageIsComplete()
    {
        var cleaner = new StreamedAnswerCleaner();

        Assert.Equal("See ", cleaner.Add("See ![ch"));
        Assert.Equal(string.Empty, cleaner.Add("art](https://x.example/a.png"));
        Assert.Equal("chart.", cleaner.Add(")."));
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var start = 0; start < text.Length; start += size)
        {
            yield return text.Substring(start, Math.Min(size, text.Length - start));
        }
    }
}

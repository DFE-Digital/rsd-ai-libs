using GovUK.Dfe.AI.Agents.Quality;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Quality;

/// <summary>Answers never carry links that would resolve against the app's own address and give a 404.</summary>
public sealed class AnswerLinksTests
{
    [Theory]
    [InlineData("Rated Good [Evidence 1](evidence/1).", "Rated Good [Evidence 1].")]          // a citation stays a citation
    [InlineData("See [Evidence 2](/evidence/2) and [Evidence 3](#3).", "See [Evidence 2] and [Evidence 3].")]
    [InlineData("Read [the report](files/report.pdf).", "Read the report.")]                   // other relative links become text
    [InlineData("On [GOV.UK](https://www.gov.uk/ofsted).", "On [GOV.UK](https://www.gov.uk/ofsted).")]   // full addresses are kept
    [InlineData("Email [us](mailto:team@education.gov.uk).", "Email [us](mailto:team@education.gov.uk).")]
    [InlineData("Plain [Evidence 1] citation.", "Plain [Evidence 1] citation.")]
    [InlineData("No links at all.", "No links at all.")]
    public void RelativeLinks_BecomeText_AndFullAddressesAreKept(string answer, string expected)
        => Assert.Equal(expected, AnswerLinks.RemoveRelativeLinks(answer));

    [Fact]
    public void NoAnswer_StaysNoAnswer()
        => Assert.Null(AnswerLinks.RemoveRelativeLinks(null));
}

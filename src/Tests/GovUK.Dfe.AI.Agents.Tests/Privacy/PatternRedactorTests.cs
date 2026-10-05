using GovUK.Dfe.AI.Agents.Privacy;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Privacy;

/// <summary><c>PatternRedactor</c>: each named pattern is replaced, so a reader can still tell what was removed.</summary>
public sealed class PatternRedactorTests
{
    private static readonly PatternRedactor UkIdentifiers = new(new Dictionary<string, string>
    {
        ["UPN"] = @"\b[A-Z]\d{11}[0-9A-Z]\b",
        ["NI number"] = @"\b[A-CEGHJ-PR-TW-Z]{2}\d{6}[A-D]\b",
    });

    [Fact]
    public void EachPattern_IsReplacedWithItsName_AndOtherTextIsKept()
        => Assert.Equal("Pupil [UPN removed], parent [NI number removed], attendance 92%.",
            UkIdentifiers.Redact("Pupil A12345678901B, parent AB123456C, attendance 92%."));

    [Fact]
    public void TextWithNothingToRemove_IsUnchanged()
        => Assert.Equal("URN 100000 was rated Good.", UkIdentifiers.Redact("URN 100000 was rated Good."));

    [Fact]
    public void AnInvalidPattern_FailsWhenTheRedactorIsCreated_NotOnARun()
        => Assert.ThrowsAny<ArgumentException>(() => new PatternRedactor(new Dictionary<string, string> { ["bad"] = "(" }));
}

using GovUK.Dfe.AI.Agents.Filters;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Context;

/// <summary>Search filters: every interpolated value is escaped, so a value can't change what the filter matches.</summary>
public sealed class ODataFilterTests
{
    private static string Filter(ODataFilter filter) => filter.ToString();

    [Fact]
    public void AnInjectionAttempt_StaysOneQuotedValue()
    {
        var urn = "100000' or urn ne '";

        Assert.Equal("urn eq '100000'' or urn ne '''", Filter($"urn eq {urn}"));
    }

    [Fact]
    public void EachValueType_IsWrittenAsAnODataLiteral()
    {
        var date = new DateTimeOffset(2024, 3, 12, 9, 30, 0, TimeSpan.Zero);
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        string? missing = null;

        Assert.Equal(
            "name eq 'Oak' and urn eq 100000 and score ge 3.5 and open eq true and inspected ge 2024-03-12T09:30:00.0000000+00:00 "
            + "and id eq '0f8fad5b-d9cb-469f-a165-70867728950e' and closed eq null",
            Filter($"name eq {"Oak"} and urn eq {100000} and score ge {3.5} and open eq {true} and inspected ge {date} and id eq {id} and closed eq {missing}"));
    }

    [Fact]
    public void NumbersAndDates_IgnoreTheCurrentCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
        try
        {
            Assert.Equal("score ge 3.5", Filter($"score ge {3.5}"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void AValueWithNoLiteralForm_IsRejected()
    {
        var notAllowed = new[] { 1, 2 };

        Assert.Throws<ArgumentException>(() => Filter($"urn eq {notAllowed}"));
        Assert.Throws<ArgumentException>(() => Filter($"score eq {double.NaN}"));
    }

    [Fact]
    public void Raw_IsSentAsItIs_AndNoFilterIsEmpty()
    {
        Assert.Equal("urn eq '100000'", ODataFilter.Raw("urn eq '100000'").ToString());
        Assert.True(default(ODataFilter).IsEmpty);
        Assert.Throws<ArgumentException>(() => ODataFilter.Raw(" "));
    }
}

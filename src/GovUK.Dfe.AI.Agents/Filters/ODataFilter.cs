using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace GovUK.Dfe.AI.Agents.Filters;

/// <summary>
/// An OData filter for <see cref="Interfaces.IContextRetriever"/>. Write it as an interpolated string, e.g.
/// <c>filter: $"urn eq {urn}"</c>: every interpolated value is escaped as an OData literal, so a value can never change
/// the filter's meaning. A plain string doesn't convert; pass an already-escaped filter with <see cref="Raw"/>.
/// </summary>
[InterpolatedStringHandler]
public readonly struct ODataFilter
{
    private readonly StringBuilder? _built;
    private readonly string? _raw;

    /// <summary>Used by the compiler for <c>$"..."</c> filters.</summary>
    public ODataFilter(int literalLength, int formattedCount)
    {
        _built = new StringBuilder(literalLength + (formattedCount * 16));
        _raw = null;
    }

    private ODataFilter(string raw)
    {
        _built = null;
        _raw = raw;
    }

    /// <summary>Whether no filter was given.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(ToString());

    /// <summary>
    /// A filter that's already escaped, e.g. from <c>SearchFilter.Create</c>. It's sent as it is, so never build it
    /// by concatenating values.
    /// </summary>
    public static ODataFilter Raw(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        return new ODataFilter(filter);
    }

    /// <summary>Used by the compiler: the filter text between values.</summary>
    public void AppendLiteral(string value) => _built?.Append(value);

    /// <summary>Used by the compiler: a value, escaped as an OData literal.</summary>
    /// <exception cref="ArgumentException">The value's type has no OData literal form.</exception>
    public void AppendFormatted<T>(T value) => _built?.Append(ToLiteral(value));

    public override string ToString() => _raw ?? _built?.ToString() ?? string.Empty;

    /// <summary>Formats a value as an OData literal: strings quoted with any quotes doubled; numbers, dates and booleans invariant.</summary>
    internal static string ToLiteral(object? value) => value switch
    {
        null => "null",
        string text => Quote(text),
        char character => Quote(character.ToString()),
        Guid guid => Quote(guid.ToString()),
        bool flag => flag ? "true" : "false",
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal
            => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTime date => new DateTimeOffset(date).ToString("O", CultureInfo.InvariantCulture),
        _ => throw new ArgumentException(
            $"A {value.GetType().Name} can't be used in a filter. Use a string, number, boolean, date or Guid.", nameof(value)),
    };

    private static string Quote(string text) => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";
}

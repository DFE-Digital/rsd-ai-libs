using System.Text.RegularExpressions;
using GovUK.Dfe.AI.Agents.Privacy.Interfaces;

namespace GovUK.Dfe.AI.Agents.Privacy;

/// <summary>
/// Replaces each match of a regular expression with <c>[name removed]</c>, e.g. pupil numbers or National Insurance numbers.
/// </summary>
public sealed class PatternRedactor : IAgentInputRedactor
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private readonly (Regex Pattern, string Replacement)[] _patterns;

    /// <param name="patterns">Regular expressions by name, e.g. <c>["UPN"] = @"\b[A-Z]\d{11}[0-9A-Z]\b"</c>.</param>
    /// <exception cref="ArgumentException">A pattern isn't a valid regular expression.</exception>
    public PatternRedactor(IReadOnlyDictionary<string, string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns = [.. patterns.Select(static pattern => (
            new Regex(pattern.Value, RegexOptions.CultureInvariant, MatchTimeout),
            $"[{pattern.Key} removed]"))];
    }

    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _patterns.Aggregate(text, static (current, pattern) => pattern.Pattern.Replace(current, pattern.Replacement));
    }
}

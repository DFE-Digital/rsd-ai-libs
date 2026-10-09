using System.Diagnostics.CodeAnalysis;

namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>
/// Untrusted material for a run, sent to the model fenced as data, and where each numbered piece came from. Pass a search's
/// <see cref="ContextResult"/> as it is, so its sources come too; a <see langword="string"/> also converts, without sources.
/// </summary>
/// <remarks>
/// With sources, each <c>[Evidence n]</c> in a checked answer becomes a link to that source, or its name when it has no web
/// address. The model never sees the sources, so it can't make up a link.
/// </remarks>
public sealed class AgentEvidence
{
    /// <param name="text">The evidence, as sent to the model.</param>
    /// <param name="sources">Where each numbered piece came from; each number at most once.</param>
    /// <exception cref="ArgumentException">A source has a number below 1, a blank name, or a number used twice.</exception>
    public AgentEvidence(string text, IReadOnlyList<EvidenceSource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        sources ??= [];

        if (sources.FirstOrDefault(static source => source.Number < 1 || string.IsNullOrWhiteSpace(source.Name)) is { } invalid)
        {
            throw new ArgumentException($"Evidence source {invalid.Number} needs a number of 1 or more and a name.", nameof(sources));
        }

        if (sources.GroupBy(static source => source.Number).FirstOrDefault(static group => group.Count() > 1) is { } repeated)
        {
            throw new ArgumentException($"Evidence {repeated.Key} has more than one source.", nameof(sources));
        }

        Text = text;
        Sources = sources;
    }

    /// <summary>The evidence, as sent to the model.</summary>
    public string Text { get; }

    /// <summary>Where each numbered piece came from; empty when unknown.</summary>
    public IReadOnlyList<EvidenceSource> Sources { get; }

    [return: NotNullIfNotNull(nameof(text))]
    public static implicit operator AgentEvidence?(string? text) => FromString(text);

    [return: NotNullIfNotNull(nameof(result))]
    public static implicit operator AgentEvidence?(ContextResult? result) => FromContextResult(result);

    /// <summary>Evidence with no known sources.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static AgentEvidence? FromString(string? text) => text is null ? null : new(text);

    /// <summary>A search's evidence, with its sources.</summary>
    [return: NotNullIfNotNull(nameof(result))]
    public static AgentEvidence? FromContextResult(ContextResult? result) => result is null ? null : new(result.Text, result.Sources);

    public override string ToString() => Text;
}

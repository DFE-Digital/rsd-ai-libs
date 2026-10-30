namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>One search result.</summary>
public sealed record SearchResultItem(string Content, double? Score = null)
{
    /// <summary>The result's descriptive name, e.g. its title, if known.</summary>
    public string? Name { get; init; }

    /// <summary>The result's web address, if known.</summary>
    public Uri? Link { get; init; }
}

namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>One search result.</summary>
public sealed record SearchResultItem(string Content, double? Score = null);

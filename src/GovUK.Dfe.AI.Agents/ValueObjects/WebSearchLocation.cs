namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>
/// An approximate location used to bias a web search agent's results (e.g. towards local news or
/// region-specific sources). All parts are optional.
/// </summary>
public sealed record WebSearchLocation(string? Country = null, string? Region = null, string? City = null)
{
    /// <summary>The library's default web search location bias: United Kingdom, England, London.</summary>
    public static readonly WebSearchLocation UnitedKingdom = new(Country: "GB", Region: "England", City: "London");

    /// <summary>
    /// Search near <paramref name="city"/>, e.g. a school's or trust's town, to find its local news. With no city: the
    /// default, London, England, GB.
    /// </summary>
    /// <param name="country">The city's country, as a two-letter code. Default: GB.</param>
    public static WebSearchLocation ForCity(string? city, string country = "GB")
        => string.IsNullOrWhiteSpace(city) ? UnitedKingdom : new(Country: country, City: city.Trim());
}

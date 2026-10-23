namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>
/// Where one numbered piece of evidence came from, so the answer's <c>[Evidence n]</c> citation can be shown as a link to it,
/// or by name when it has no web address.
/// </summary>
/// <param name="Number">The <c>n</c> in <c>[Evidence n]</c>.</param>
/// <param name="Name">What the citation shows, e.g. the report's title.</param>
/// <param name="Link">Its web address, if it has one. Only absolute <c>https</c> and <c>http</c> addresses are shown as links.</param>
public sealed record EvidenceSource(int Number, string Name, Uri? Link = null);

namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>Evidence found for a prompt.</summary>
/// <param name="Text">The evidence, formatted for the prompt.</param>
/// <param name="HasEvidence">Whether anything matched.</param>
public sealed record ContextResult(string Text, bool HasEvidence)
{
    /// <summary>Where each numbered piece came from, when the retriever knows; empty otherwise.</summary>
    public IReadOnlyList<EvidenceSource> Sources { get; init; } = [];
}

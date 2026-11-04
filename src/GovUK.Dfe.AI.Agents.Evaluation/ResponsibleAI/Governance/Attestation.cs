namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;

/// <summary>
/// A person's record that a required review was done, e.g. the DPIA was approved, meeting one of the policy's
/// <c>RequiredAttestations</c>.
/// </summary>
public sealed record Attestation
{
    /// <summary>The requirement it meets, e.g. "dpia".</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Who approved it.</summary>
    public string ApprovedBy { get; init; } = string.Empty;

    /// <summary>Their role, e.g. "Data Protection Officer".</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>When it was approved.</summary>
    public DateOnly? ApprovedOn { get; init; }

    /// <summary>Optional: when it must be reviewed again.</summary>
    public DateOnly? ExpiresOn { get; init; }

    /// <summary>Where the evidence is, e.g. a link to the approved DPIA or the audit report.</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>Why it can't be relied on on <paramref name="today"/>. Empty: it's valid.</summary>
    public IReadOnlyList<string> Problems(DateOnly today)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ApprovedBy))
        {
            problems.Add("it has no approvedBy");
        }

        if (string.IsNullOrWhiteSpace(Role))
        {
            problems.Add("it has no role");
        }

        if (string.IsNullOrWhiteSpace(Evidence))
        {
            problems.Add("it has no evidence");
        }

        if (ApprovedOn is not { } approved)
        {
            problems.Add("it has no approvedOn date");
        }
        else if (approved > today)
        {
            problems.Add($"it isn't approved until {approved:yyyy-MM-dd}");
        }

        if (ExpiresOn is { } expires && expires <= today)
        {
            problems.Add($"it expired on {expires:yyyy-MM-dd}");
        }

        return problems;
    }
}

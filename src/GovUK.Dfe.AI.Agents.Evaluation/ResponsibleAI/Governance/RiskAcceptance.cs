using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;

/// <summary>
/// A named person's acceptance of one failed check's risk, so the release can go ahead with it visible in the report.
/// It must say why, who accepted it and in what role, when, a reference to the decision, and when it expires.
/// </summary>
public sealed record RiskAcceptance
{
    /// <summary>The check it accepts, e.g. "inclusiveness.readability".</summary>
    public string CheckId { get; init; } = string.Empty;

    /// <summary>The agent it applies to, or "*" for every agent.</summary>
    public string Agent { get; init; } = string.Empty;

    /// <summary>Optional: the one result it covers, e.g. a test case or metric; unset covers every result of the check.</summary>
    public string? Subject { get; init; }

    /// <summary>Why the risk is acceptable, and what limits it, e.g. a fix planned for the next release.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Who accepted it.</summary>
    public string AcceptedBy { get; init; } = string.Empty;

    /// <summary>Their role, e.g. "Senior Responsible Owner": someone with the authority to accept the risk.</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>When it was accepted.</summary>
    public DateOnly? AcceptedOn { get; init; }

    /// <summary>When it stops applying. Required, and at most the policy's <c>MaxRiskAcceptanceDays</c> after it was accepted.</summary>
    public DateOnly? ExpiresOn { get; init; }

    /// <summary>Where the decision is recorded, e.g. a risk log or ticket reference.</summary>
    public string Reference { get; init; } = string.Empty;

    /// <summary>Whether it covers <paramref name="result"/> of <paramref name="agentName"/>, valid or not.</summary>
    public bool Covers(string agentName, QualityCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Equals(CheckId, result.CheckId, StringComparison.OrdinalIgnoreCase)
            && (Agent == "*" || string.Equals(Agent, agentName, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(Subject) || string.Equals(Subject, result.Subject, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Why it can't be relied on on <paramref name="today"/>, e.g. it's expired or unsigned. Empty: it's valid.</summary>
    public IReadOnlyList<string> Problems(DateOnly today, int maxDays)
    {
        var problems = new List<string>();
        Require(CheckId, "checkId");
        Require(Agent, "agent");
        Require(Reason, "reason");
        Require(AcceptedBy, "acceptedBy");
        Require(Role, "role");
        Require(Reference, "reference");

        if (AcceptedOn is not { } accepted)
        {
            problems.Add("it has no acceptedOn date");
        }
        else if (accepted > today)
        {
            problems.Add($"it isn't accepted until {accepted:yyyy-MM-dd}");
        }

        if (ExpiresOn is not { } expires)
        {
            problems.Add("it has no expiresOn date");
        }
        else if (expires <= today)
        {
            problems.Add($"it expired on {expires:yyyy-MM-dd}");
        }
        else if (AcceptedOn is { } from && expires.DayNumber - from.DayNumber > maxDays)
        {
            problems.Add($"it lasts more than {maxDays} days");
        }

        return problems;

        void Require(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"it has no {field}");
            }
        }
    }

    /// <summary>For the report, e.g. "Jo Bloggs (SRO), ref RISK-12, until 2027-01-31: fix planned".</summary>
    public override string ToString() =>
        $"{AcceptedBy} ({Role}), ref {Reference}, until {ExpiresOn:yyyy-MM-dd}: {Reason}";
}

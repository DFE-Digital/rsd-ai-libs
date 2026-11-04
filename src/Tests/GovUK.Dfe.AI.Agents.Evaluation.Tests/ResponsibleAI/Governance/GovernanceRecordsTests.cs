using GovUK.Dfe.AI.Agents.Evaluation.Exceptions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;
using Xunit;
using static GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.QualityGateTestData;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.Governance;

/// <summary>The governance file: risk acceptances and attestations, the audit evidence for exceptions and human reviews.</summary>
public sealed class GovernanceRecordsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("governance-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_risk_acceptances_and_attestations_with_comments_allowed()
    {
        var path = Path.Combine(_folder, "approvals.json");
        await File.WriteAllTextAsync(path, """
            // Reviewed like code.
            {
              "riskAcceptances": [
                { "checkId": "inclusiveness.readability", "agent": "briefing-agent", "reason": "Ministers read specialist terms",
                  "acceptedBy": "Sam Owner", "role": "SRO", "acceptedOn": "2026-10-01", "expiresOn": "2027-01-01", "reference": "RISK-7" }
              ],
              "attestations": [
                { "id": "dpia", "approvedBy": "Dee Pio", "role": "DPO", "approvedOn": "2026-09-01", "evidence": "https://example.gov.uk/dpia" }
              ]
            }
            """, Token);

        var records = await GovernanceRecords.LoadAsync(path, Token);

        var acceptance = Assert.Single(records.RiskAcceptances);
        Assert.Equal(new DateOnly(2027, 1, 1), acceptance.ExpiresOn);
        Assert.Empty(acceptance.Problems(Today, 180));
        Assert.Empty(Assert.Single(records.Attestations).Problems(Today));
    }

    [Fact]
    public async Task Has_no_records_when_the_file_is_missing()
    {
        var records = await GovernanceRecords.LoadAsync(Path.Combine(_folder, "missing.json"), Token);

        Assert.Same(GovernanceRecords.None, records);
    }

    [Fact]
    public async Task Stops_the_gate_when_the_file_is_unreadable_rather_than_ignoring_exceptions()
    {
        var path = Path.Combine(_folder, "broken.json");
        await File.WriteAllTextAsync(path, "{ not json", Token);

        var error = await Assert.ThrowsAsync<ReleaseGateException>(() => GovernanceRecords.LoadAsync(path, Token));

        Assert.StartsWith($"The governance file {path} isn't readable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Requires_a_risk_acceptance_to_name_who_why_where_and_until_when()
    {
        var problems = new RiskAcceptance { CheckId = "x", Agent = "*" }.Problems(Today, 180);

        Assert.Equal(
            ["it has no reason", "it has no acceptedBy", "it has no role", "it has no reference", "it has no acceptedOn date", "it has no expiresOn date"],
            problems);
    }

    [Fact]
    public void Rejects_an_attestation_without_evidence_or_past_its_review_date()
    {
        var attestation = new Attestation { Id = "dpia", ApprovedBy = "Dee", Role = "DPO", ApprovedOn = Today.AddDays(-400), ExpiresOn = Today.AddDays(-1) };

        Assert.Equal(["it has no evidence", "it expired on 2026-11-02"], attestation.Problems(Today));
    }
}

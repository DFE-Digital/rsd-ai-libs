using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Quality;

/// <summary>Citation checks: only numbered evidence is checked, and only evidence that exists may be cited.</summary>
public sealed class CitationsTests
{
    private const string NumberedEvidence = "--- ofsted_index Evidence 1 ---\nRated Good.\n\n--- ofsted_index Evidence 2 ---\nInspected 2024.";

    [Theory]
    [InlineData("Rated Good [Evidence 1], inspected 2024 [Evidence 2].", null)]
    [InlineData("Rated Good.", "Cite the evidence")]
    [InlineData("Rated Good [Evidence 3].", "[Evidence 3] doesn't exist")]
    public void Check_AcceptsOnlyAnswersCitingEvidenceThatExists(string answer, string? expectedProblem)
    {
        var problem = Citations.Check(answer, Citations.CountEvidence(NumberedEvidence));

        if (expectedProblem is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(expectedProblem, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ForRun_ByDefault_AsksForCitations_AndChecksThemBeforeTheAgentsOwnValidation()
    {
        var definition = new AgentDefinition("ofsted-agent", "Ofsted") { Validate = _ => "own check" };

        var (prompt, validate) = Citations.ForRun(definition, "Summarise.", NumberedEvidence);

        Assert.EndsWith("as plain text [Evidence n], never as a link.", prompt, StringComparison.Ordinal);
        Assert.Contains("Cite the evidence", validate!(new AgentResult { AgentName = "ofsted-agent", Output = "No citations.", TotalTokens = 0 }), StringComparison.Ordinal);
        Assert.Equal("own check", validate(new AgentResult { AgentName = "ofsted-agent", Output = "Good [Evidence 1].", TotalTokens = 0 }));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Another agent's unnumbered output.", true)]   // unnumbered evidence isn't checked
    [InlineData(NumberedEvidence, false)]                      // the agent turned citations off
    public void ForRun_ChangesNothing_WhenTheEvidenceIsntNumbered_OrCitationsAreOff(string? evidence, bool RequiredCitations)
    {
        var definition = new AgentDefinition("ofsted-agent", "Ofsted") { RequiredCitations = RequiredCitations };

        var (prompt, validate) = Citations.ForRun(definition, "Summarise.", evidence);

        Assert.Equal("Summarise.", prompt);
        Assert.Null(validate);
    }
}

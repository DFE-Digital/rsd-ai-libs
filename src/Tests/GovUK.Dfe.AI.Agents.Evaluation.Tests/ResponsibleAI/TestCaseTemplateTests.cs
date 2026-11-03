using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Accountability;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Fairness;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Quality;
using Xunit;
using static GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.QualityGateTestData;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI;

/// <summary>
/// The test case templates apps copy (docs/templates/TestCases) load in the standard format and meet the quality gate's
/// conventions and dataset checks as they are, so a copy starts from a set that passes.
/// </summary>
public sealed class TestCaseTemplateTests
{
    private static readonly string Templates = Path.Combine(RepositoryRoot(), "docs", "templates", "TestCases", "my-agent");

    [Fact]
    public async Task Follow_every_test_case_convention()
    {
        var cases = await LoadAsync();

        Assert.Empty(cases.SelectMany(ReleaseSections.Problems));
        Assert.Empty(cases.SelectMany(c => ScenarioMetadata.Problems(c, requireMetadata: true)));
    }

    [Fact]
    public async Task Cover_each_principle_tested_by_cases_in_its_own_file()
    {
        var cases = await LoadAsync();

        var principles = cases.Select(c => ScenarioMetadata.For(c).Principle).Distinct().Order();
        Assert.Equal(
            [ResponsibleAiPrinciple.Fairness, ResponsibleAiPrinciple.ReliabilityAndSafety, ResponsibleAiPrinciple.PrivacyAndSecurity,
                ResponsibleAiPrinciple.Inclusiveness, ResponsibleAiPrinciple.Transparency],
            principles);
        Assert.Contains("guardrail/privacy-and-security/PRIV-002", cases.Select(c => c.Name));
    }

    [Fact]
    public async Task Pass_the_gates_dataset_and_group_checks_when_every_case_passes()
    {
        var cases = await LoadAsync();
        var context = CheckContext(cases.Select(c => (c, c.GuardrailMustBlock
            ? Result(c.Name, output: null, blocked: true)
            : Result(c.Name, group: c.Group))));

        IQualityCheck[] checks = [new EvaluationDatasetCheck(), new GroupSampleSizeCheck(), new GroupOutcomeParityCheck()];
        foreach (var check in checks)
        {
            var notMet = (await RunAsync(check, context)).Where(static r => r.NotMet).Select(static r => r.Summary);
            Assert.Empty(notMet);
        }
    }

    private static Task<IReadOnlyList<AgentTestCase>> LoadAsync() =>
        AgentTestCase.LoadAsync(Templates, TestContext.Current.CancellationToken);

    /// <summary>The folder with the solution file, above the test's own folder.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GovUK.Dfe.AI.Agents.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root wasn't found above the tests.");
    }
}

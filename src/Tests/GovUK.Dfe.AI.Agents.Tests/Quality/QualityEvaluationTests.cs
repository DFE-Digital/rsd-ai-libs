using System.Text.Json;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using NSubstitute.ExceptionExtensions;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Quality;

/// <summary>The test-case release gate. Live-run scoring and the judge are in the .Evaluation tests.</summary>
public sealed class QualityEvaluationTests
{
    private static readonly AgentDefinition Ofsted = new("ofsted-agent", "Ofsted");

    private static IAgentRunEvaluator ScoresGroundedness(double score)
    {
        var evaluator = Substitute.For<IAgentRunEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CompletedAgentRun>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, double> { ["Groundedness"] = score });
        return evaluator;
    }

    // ===================== Release gate =====================

    private static IAgentService Answers(string output)
    {
        var agents = Substitute.For<IAgentService>();
        agents.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "ofsted-agent", Output = output, TotalTokens = 30, AgentVersion = "4" });
        return agents;
    }

    private static readonly AgentTestCase GoodSchool = new("good-school", "Summarise URN 100000.")
    {
        MustMention = ["good", "2024"],
        MustNotMention = ["Outstanding"],
    };

    [Fact]
    public async Task TestRunner_ChecksEachCasesFacts_CaseInsensitively_AndScoresIt()
    {
        var report = await new AgentTestRunner(Answers("Rated GOOD in 2024."), ScoresGroundedness(4)).RunAsync(Ofsted, [GoodSchool], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(4, report.AverageScores["Groundedness"]);
    }

    [Fact]
    public async Task TestRunner_CanTestTheDeployedVersion()
    {
        var agents = Answers("Rated Good in 2024.");

        await new AgentTestRunner(agents).RunAsync(Ofsted, [GoodSchool], AgentTestTarget.Deployed, cancellationToken: TestContext.Current.CancellationToken);

        await agents.Received(1).RunAsync(Ofsted, GoodSchool.Prompt, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestRunner_ReportsEveryMissingOrForbiddenFact()
    {
        var report = await new AgentTestRunner(Answers("Rated Outstanding.")).RunAsync(Ofsted, [GoodSchool], cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(report.Results);
        Assert.False(report.Passed);
        Assert.Equal(["Doesn't mention \"good\"", "Doesn't mention \"2024\"", "Mentions \"Outstanding\""], result.Failures);
    }

    [Fact]
    public async Task TestRunner_RecordsAFailedRun_AsAFailedCase_AndCarriesOn()
    {
        var agents = Substitute.For<IAgentService>();
        agents.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("outer", new TimeoutException("Took too long.")));

        var report = await new AgentTestRunner(agents).RunAsync(Ofsted, [GoodSchool, GoodSchool with { Name = "second" }], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Results.Count);
        Assert.All(report.Results, result => Assert.Equal(["The run failed: Took too long."], result.Failures));
    }

    [Fact]
    public void Report_FlagsMetricsBelowTheMinimum_AndRegressionsFromTheBaseline()
    {
        static AgentEvaluationReport Scored(double groundedness, double relevance) => new("ofsted-agent",
            [new AgentTestResult("c", "x", [], new Dictionary<string, double> { ["Groundedness"] = groundedness, ["Relevance"] = relevance })]);

        var baseline = Scored(4.5, 4.0);
        var candidate = Scored(3.9, 4.0);

        Assert.Equal(["Groundedness"], candidate.BelowMinimum(4));
        Assert.Equal(["Groundedness"], candidate.RegressionsFrom(baseline, tolerance: 0.2));
        Assert.Empty(Scored(4.4, 4.0).RegressionsFrom(baseline, tolerance: 0.2));
    }

    [Fact]
    public void AJudgeThatScoredNothing_FailsTheGate_InsteadOfPassingIt()
    {
        var baseline = new AgentEvaluationReport("ofsted-agent",
            [new AgentTestResult("c", "x", [], new Dictionary<string, double> { ["Groundedness"] = 4.5 })]);
        var unscored = new AgentEvaluationReport("ofsted-agent", [new AgentTestResult("c", "x", [], new Dictionary<string, double>())]);

        Assert.Equal(["Groundedness"], unscored.BelowMinimum(3.5, "Groundedness"));
        Assert.Equal(["Groundedness"], unscored.RegressionsFrom(baseline));
    }

    [Fact]
    public void Report_RoundTripsThroughJson_SoItCanBeTheNextBaseline()
    {
        var report = new AgentEvaluationReport("ofsted-agent",
            [new AgentTestResult("c", "x", [], new Dictionary<string, double> { ["Groundedness"] = 4.5 })]);

        var baseline = JsonSerializer.Deserialize<AgentEvaluationReport>(JsonSerializer.Serialize(report))!;

        Assert.Equal(4.5, baseline.AverageScores["Groundedness"]);
    }

    [Fact]
    public async Task TestCases_LoadFromJsonFiles_NamedAfterTheFile()
    {
        var directory = Directory.CreateTempSubdirectory("aiagents-cases-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "urn-100000.json"),
                """{ "prompt": "Summarise URN 100000.", "evidence": "Rated Good.", "mustMention": [ "Good" ] }""", cancellationToken: TestContext.Current.CancellationToken);

            var testCase = Assert.Single(await AgentTestCase.LoadAsync(directory, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(("urn-100000", "Summarise URN 100000.", "Rated Good."), (testCase.Name, testCase.Prompt, testCase.Evidence));
            Assert.Equal(["Good"], testCase.MustMention);
            Assert.Empty(testCase.MustNotMention);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

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
        agents.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<AgentEvidence?>(), Arg.Any<CancellationToken>())
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

        await agents.Received(1).RunAsync(Ofsted, GoodSchool.Prompt, Arg.Any<AgentEvidence?>(), Arg.Any<CancellationToken>());
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
        agents.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<AgentEvidence?>(), Arg.Any<CancellationToken>())
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
                """{ "prompt": "Summarise URN 100000.", "evidence": "Rated Good.", "mustMention": [ "Good" ], "group": "special-schools" }""", cancellationToken: TestContext.Current.CancellationToken);

            var testCase = Assert.Single(await AgentTestCase.LoadAsync(directory, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(("urn-100000", "Summarise URN 100000.", "Rated Good."), (testCase.Name, testCase.Prompt, testCase.Evidence));
            Assert.Equal(["Good"], testCase.MustMention);
            Assert.Empty(testCase.MustNotMention);
            Assert.Equal("special-schools", testCase.Group);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ===================== Fairness across groups =====================

    private static AgentTestResult Scored(string group, double groundedness)
        => new($"{group}-case", "x", [], new Dictionary<string, double> { ["Groundedness"] = groundedness, ["Relevance"] = 4 }) { Group = group };

    [Fact]
    public void Report_ComparesGroups_AndFlagsAMetricWhereOneGroupIsServedWorse()
    {
        var report = new AgentEvaluationReport("ofsted-agent",
        [
            Scored("academies", 4.5), Scored("academies", 4.3), Scored("special-schools", 3.1),
            new AgentTestResult("ungrouped", "x", [], new Dictionary<string, double> { ["Groundedness"] = 1 }),   // not in any group
        ]);

        Assert.Equal(4.4, report.AverageScoresByGroup["academies"]["Groundedness"], precision: 6);
        Assert.Equal(["academies", "special-schools"], report.AverageScoresByGroup.Keys.Order());
        Assert.Equal(["Groundedness: special-schools 3.1, academies 4.4"], report.GroupGaps(0.5));
        Assert.Empty(report.GroupGaps(0.5, "Relevance"));   // same for every group
        Assert.Empty(report.GroupGaps(2));
    }

    [Fact]
    public async Task TestRunner_KeepsEachCasesGroup_InItsResult()
    {
        var report = await new AgentTestRunner(Answers("Rated good in 2024.")).RunAsync(Ofsted, [GoodSchool with { Group = "rural" }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("rural", Assert.Single(report.Results).Group);
    }

    // ===================== Release gate with tolerance =====================

    private static AgentEvaluationReport Report(double groundedness, double relevance, params string[] failures)
        => new("ofsted-agent", [new AgentTestResult("good-school", "x", failures,
            new Dictionary<string, double> { ["Groundedness"] = groundedness, ["Relevance"] = relevance })]);

    private static readonly ReleaseGate Gate = new() { Metrics = ["Groundedness", "Relevance"], MinimumScore = 3.5, Tolerance = 0.2 };

    [Theory]
    [InlineData(4.3, true)]    // within the tolerance: judge noise
    [InlineData(4.2, false)]   // a real drop
    public void Gate_AllowsADropWithinTheTolerance_ButNotBeyondIt(double groundedness, bool passes)
    {
        var failures = Report(groundedness, 4.0).FailuresAgainst(Gate, baseline: Report(4.5, 4.0));

        Assert.Equal(passes, failures.Count == 0);
        if (!passes)
        {
            Assert.Equal([$"Groundedness: fell from 4.5 to {groundedness}, more than the tolerance 0.2"], failures);
        }
    }

    [Fact]
    public void Gate_ListsEveryReason_ItFails()
    {
        var report = new AgentEvaluationReport("ofsted-agent",
        [
            new AgentTestResult("good-school", "x", ["Doesn't mention \"good\""], new Dictionary<string, double> { ["Groundedness"] = 3.0 }) { Group = "academies" },
            new AgentTestResult("special-school", "x", [], new Dictionary<string, double> { ["Groundedness"] = 2.0 }) { Group = "special-schools" },
        ]);

        var failures = report.FailuresAgainst(Gate with { MaxGroupGap = 0.5 });

        Assert.Equal(
        [
            "Case good-school: Doesn't mention \"good\"",
            "Groundedness: averaged 2.5, below the minimum 3.5",
            "Relevance: not scored",
            "Groundedness: special-schools 2, academies 3: a gap over 0.5",
        ], failures);
    }

    [Fact]
    public void Gate_OnTheFirstRelease_HasNoBaselineToCompare()
        => Assert.Empty(Report(4.0, 4.0).FailuresAgainst(Gate));

    [Fact]
    public async Task Repeats_AverageEachCasesScores_AndAFactWrongInAnyRunFailsTheCase()
    {
        var agents = Substitute.For<IAgentService>();
        agents.RunAsync(Arg.Any<AgentDefinition>(), Arg.Any<string>(), Arg.Any<AgentEvidence?>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResult { AgentName = "ofsted-agent", Output = "Rated good in 2024." },
                     new AgentResult { AgentName = "ofsted-agent", Output = "Rated good." });
        var evaluator = Substitute.For<IAgentRunEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CompletedAgentRun>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, double> { ["Groundedness"] = 3 }, new Dictionary<string, double> { ["Groundedness"] = 5 });

        var report = await new AgentTestRunner(agents, evaluator).RunAsync(Ofsted, [GoodSchool], repeats: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(report.Results);
        Assert.Equal(4, result.Scores["Groundedness"]);
        Assert.Equal(["Doesn't mention \"2024\" (in 1 of 2 runs)"], result.Failures);
    }
}

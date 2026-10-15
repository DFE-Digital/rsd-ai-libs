using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary><c>PublishToFoundry</c>: release-gate answers sent to Foundry's Evaluations page.</summary>
public sealed class FoundryEvaluationTests : IDisposable
{
    private const string Outside = "outside what I can answer";

    private static readonly Dictionary<string, string?> PublishToFoundry = new() { ["PublishToFoundry"] = "true" };

    private readonly ReleaseGateTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_each_agent_s_answers_to_an_evaluation_scored_by_the_judge_model()
    {
        _host.AddCaseForEveryAgent(mustMention: ["Good"]);
        _host.AddCaseForEveryAgent(caseName: "out-of-scope", prompt: "Weather?", mustMention: [Outside]);
        _host.AnswerWith($"This is {Outside}. Rated Good [Evidence 1].");
        _host.JudgeScores(4.5);

        await _host.Build(judge: true, settings: PublishToFoundry, configure: s => s.RefusalPhrases.Add(Outside))
            .EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        var evaluations = Posted("/evals");
        Assert.Equal(ReleaseGateTestHost.Agents.Select(a => $"{a.Name} release gate ({ReleaseGateTestHost.JudgeModel})").Order(),
            evaluations.Select(e => (string)e["name"]!).Order());
        var criteria = evaluations[0]["testing_criteria"]!.AsArray();
        Assert.Equal(["builtin.groundedness", "builtin.relevance"], criteria.Select(c => (string)c!["evaluator_name"]!));
        Assert.All(criteria, c => Assert.Equal(ReleaseGateTestHost.JudgeModel, (string)c!["initialization_parameters"]!["model"]!));

        var runs = Posted("/runs");
        Assert.Equal(ReleaseGateTestHost.Agents.Length, runs.Count);
        var item = Assert.Single(runs[0]["data_source"]!["source"]!["content"]!.AsArray())!["item"]!;   // the refusal is left out
        Assert.Equal("What was the outcome?", (string)item["query"]!);
        Assert.Contains("outcome: Good", (string)item["context"]!, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.Logs, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Adds_a_run_to_the_agent_s_existing_evaluation()
    {
        _host.AddCaseForEveryAgent();
        _host.FoundryEvaluations.AddExistingEvaluation("eval-trust", $"trust-agent release gate ({ReleaseGateTestHost.JudgeModel})");

        await _host.Build(settings: PublishToFoundry).EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.DoesNotContain(Posted("/evals"), e => ((string)e["name"]!).StartsWith("trust-agent", StringComparison.Ordinal));
        Assert.Contains(_host.FoundryEvaluations.Posts, p => p.Path.EndsWith("/evals/eval-trust/runs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Foundry_failure_is_logged_and_does_not_affect_the_gate()
    {
        _host.AddCaseForEveryAgent();
        _host.FoundryEvaluations.FailWith = HttpStatusCode.Forbidden;

        var results = await _host.Build(settings: PublishToFoundry).EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, r => Assert.True(r.Passed));
        Assert.Contains(_host.Logs, l => l.Level == LogLevel.Warning
            && l.Message.Contains("couldn't start the Foundry evaluation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sends_nothing_unless_turned_on()
    {
        _host.AddCaseForEveryAgent();

        await _host.Build().EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.Empty(_host.FoundryEvaluations.Posts);
    }

    private List<JsonObject> Posted(string pathEnd) =>
        [.. _host.FoundryEvaluations.Posts.Where(p => p.Path.EndsWith(pathEnd, StringComparison.Ordinal)).Select(p => p.Body)];
}

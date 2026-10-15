using System.Text.Json;
using Azure.Core;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.Extensions;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary>
/// The release gate with fake agents, judge and Foundry, and temporary folders for test cases, reports and baselines.
/// </summary>
internal sealed class ReleaseGateTestHost : IDisposable
{
    public const string JudgeModel = "myconnection/judge";

    public static readonly AgentDefinition[] Agents = [new("ofsted-agent", "Ofsted"), new("trust-agent", "Trust")];

    private readonly string _root = Directory.CreateTempSubdirectory("release-gate-tests-").FullName;
    private ServiceProvider? _provider;

    public ReleaseGateTestHost() => AnswerWith("Rated Good [Evidence 1].");

    public IAgentService AgentService { get; } = Substitute.For<IAgentService>();

    public IAgentRuntimeService Runtime { get; } = Substitute.For<IAgentRuntimeService>();

    public IAgentRunEvaluator Judge { get; } = Substitute.For<IAgentRunEvaluator>();

    public FakeFoundryEvaluations FoundryEvaluations { get; } = new();

    public List<(LogLevel Level, string Message)> Logs { get; } = [];

    public string ReportsDirectory => Path.Combine(_root, "reports");

    private string TestCasesDirectory => Path.Combine(_root, "cases");

    private string BaselinesDirectory => Path.Combine(_root, "baselines");

    public void AnswerWith(string output) =>
        AgentService.Configure().RunAsync(default!, default!, default, default).ReturnsForAnyArgs(call =>
            new AgentResult { AgentName = call.Arg<AgentDefinition>().Name, Output = output });

    public void JudgeScores(double score) =>
        Judge.EvaluateAsync(default!, default).ReturnsForAnyArgs(JudgeMetrics.All.ToDictionary(metric => metric, _ => score));

    public void AddCase(string agentName, string caseName = "case", string prompt = "What was the outcome?",
        string[]? mustMention = null, string[]? mustNotMention = null, string? group = null, bool guardrailMayBlock = false)
    {
        var directory = Directory.CreateDirectory(Path.Combine(TestCasesDirectory, agentName));
        File.WriteAllText(Path.Combine(directory.FullName, $"{caseName}.json"), JsonSerializer.Serialize(new
        {
            prompt,
            evidence = "--- ofsted_index Evidence 1 ---\noutcome: Good",
            mustMention = mustMention ?? [],
            mustNotMention = mustNotMention ?? [],
            group,
            guardrailMayBlock,
        }));
    }

    public void AddCaseForEveryAgent(string caseName = "case", string prompt = "What was the outcome?",
        string[]? mustMention = null, string? group = null, bool guardrailMayBlock = false)
    {
        foreach (var agent in Agents)
        {
            AddCase(agent.Name, caseName, prompt, mustMention, group: group, guardrailMayBlock: guardrailMayBlock);
        }
    }

    /// <summary>Saves a baseline for every agent, in which each judge metric averaged <paramref name="score"/>.</summary>
    public void SaveBaselineForEveryAgent(double score)
    {
        Directory.CreateDirectory(BaselinesDirectory);
        foreach (var agent in Agents)
        {
            var report = new AgentEvaluationReport(agent.Name,
                [new AgentTestResult("case", "Rated Good [Evidence 1].", [], JudgeMetrics.All.ToDictionary(m => m, _ => score))]);
            File.WriteAllText(Path.Combine(BaselinesDirectory, $"{agent.Name}.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
    }

    public void SaveUnreadableBaseline(string agentName)
    {
        Directory.CreateDirectory(BaselinesDirectory);
        File.WriteAllText(Path.Combine(BaselinesDirectory, $"{agentName}.json"), "{ not json");
    }

    /// <param name="judge">Whether an evaluator is registered, so answers are scored.</param>
    /// <param name="settings">Extra <c>AiAgents:Evaluation:ReleaseGate</c> settings, by name.</param>
    public IAgentReleaseGate Build(bool judge = false, Dictionary<string, string?>? settings = null,
        Action<ReleaseGateSettings>? configure = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/briefings",
            ["AiAgents:Foundry:DefaultModel"] = "myconnection/gpt-5.1",
            ["AiAgents:Evaluation:JudgeModel"] = JudgeModel,
            ["AiAgents:Evaluation:SampleRate"] = "0",
            ["AiAgents:Evaluation:ReleaseGate:TestCasesDirectory"] = TestCasesDirectory,
            ["AiAgents:Evaluation:ReleaseGate:ReportsDirectory"] = ReportsDirectory,
            ["AiAgents:Evaluation:ReleaseGate:BaselinesDirectory"] = BaselinesDirectory,
        };
        foreach (var (name, value) in settings ?? [])
        {
            values[$"AiAgents:Evaluation:ReleaseGate:{name}"] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new ListLoggerProvider(Logs)));
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), agents =>
        {
            agents.UseCredential(Substitute.For<TokenCredential>()).AddReleaseGate(configure);
            if (judge)
            {
                agents.AddCustomQualityEvaluation(_ => Judge);
            }
        });
        services.AddSingleton(AgentService);
        services.AddSingleton(Runtime);
        services.AddSingleton(FoundryEvaluations.Client());

        _provider = services.BuildServiceProvider();
        return _provider.GetRequiredService<IAgentReleaseGate>();
    }

    public void Dispose()
    {
        _provider?.Dispose();
        FoundryEvaluations.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class ListLoggerProvider(List<(LogLevel, string)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(logs);

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    private sealed class ListLogger(List<(LogLevel, string)> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (logs)
            {
                logs.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}

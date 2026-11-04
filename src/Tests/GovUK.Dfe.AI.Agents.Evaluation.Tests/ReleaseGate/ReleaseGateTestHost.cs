using System.Text.Json;
using Azure.Core;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.Extensions;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Reporting;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary>
/// The release gate with fake agents, judge and Foundry, and temporary folders for test cases, reports and baselines.
/// </summary>
internal sealed class ReleaseGateTestHost : IDisposable
{
    public const string JudgeModel = "myconnection/judge";

    public static readonly AgentDefinition[] Agents = [new("ofsted-agent", "Ofsted"), new("trust-agent", "Trust")];

    /// <summary>As the release gate writes reports.</summary>
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Directory.CreateTempSubdirectory("release-gate-tests-").FullName;
    private ServiceProvider? _provider;

    public ReleaseGateTestHost() => AnswerWith("Rated Good [Evidence 1].");

    public IAgentService AgentService { get; } = Substitute.For<IAgentService>();

    public IAgentRuntimeService Runtime { get; } = Substitute.For<IAgentRuntimeService>();

    public IAgentRunEvaluator Judge { get; } = Substitute.For<IAgentRunEvaluator>();

    public FakeFoundryEvaluations FoundryEvaluations { get; } = new();

    /// <summary>The guardrail checks <c>CheckReleaseAsync</c> runs first, as <c>AddGuardrails()</c> registers one.</summary>
    public List<IGuardrailCheck> GuardrailChecks { get; } = [];

    public List<(LogLevel Level, string Message)> Logs { get; } = [];

    /// <summary>Each log's fields, by name, as Application Insights keeps them in <c>customDimensions</c>.</summary>
    public List<(LogLevel Level, IReadOnlyDictionary<string, object?> Fields)> LogFields { get; } = [];

    public string ReportsDirectory => Path.Combine(_root, "reports");

    private string TestCasesDirectory => Path.Combine(_root, "cases");

    private string BaselinesDirectory => Path.Combine(_root, "baselines");

    public void AnswerWith(string output) =>
        AgentService.Configure().RunAsync(default!, default!, default, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(call =>
            new AgentResult { AgentName = call.Arg<AgentDefinition>().Name, Output = output });

    public void JudgeScores(double score) =>
        Judge.EvaluateAsync(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(JudgeMetrics.All.ToDictionary(metric => metric, _ => score));

    /// <summary>The principle the release gate's own tests are filed under.</summary>
    public const string Type = "reliability-and-safety";

    /// <summary>A test's name as the loader gives it: its section, its file's type and its id.</summary>
    public static string NameOf(string id, bool attack = false) =>
        $"{(attack ? ReleaseSections.Guardrail : ReleaseSections.Evaluation)}/{Type}/{id}";

    /// <summary>Adds one test, in its own file in the standard format: <c>{ "type", "description", "tests": [ ... ] }</c>.</summary>
    /// <param name="id">The test's id; its name is <see cref="NameOf"/>.</param>
    /// <param name="blocking">Whether a guardrail may, or must, block the case: its <c>guardrailMayBlock</c> or <c>guardrailMustBlock</c>.</param>
    public void AddCase(string agentName, string id = "case", string prompt = "What was the outcome?",
        string[]? mustMention = null, string[]? mustNotMention = null, string? group = null,
        GuardrailBlocking blocking = GuardrailBlocking.None)
    {
        var path = Path.Combine(TestCasesDirectory, agentName, $"{id}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            type = Type,
            description = "The release gate's own tests.",
            tests = new[]
            {
                new
                {
                    id,
                    description = "Gets the outcome wrong",
                    prompt,
                    evidence = "--- ofsted_index Evidence 1 ---\noutcome: Good",
                    mustMention = mustMention ?? [],
                    mustNotMention = mustNotMention ?? [],
                    group,
                    guardrailMayBlock = blocking == GuardrailBlocking.May,
                    guardrailMustBlock = blocking == GuardrailBlocking.Must,
                },
            },
        }));
    }

    public void AddCaseForEveryAgent(string id = "case", string prompt = "What was the outcome?",
        string[]? mustMention = null, string? group = null, GuardrailBlocking blocking = GuardrailBlocking.None)
    {
        foreach (var agent in Agents)
        {
            AddCase(agent.Name, id, prompt, mustMention, group: group, blocking: blocking);
        }
    }

    /// <summary>Saves a baseline for every agent, in which each judge metric averaged <paramref name="score"/>.</summary>
    public void SaveBaselineForEveryAgent(double score)
    {
        Directory.CreateDirectory(BaselinesDirectory);
        foreach (var agent in Agents)
        {
            var report = new AgentEvaluationReport(agent.Name,
                [new AgentTestResult(NameOf("case"), "Rated Good [Evidence 1].", [], JudgeMetrics.All.ToDictionary(m => m, _ => score))]);
            File.WriteAllText(Path.Combine(BaselinesDirectory, $"{agent.Name}.json"),
                JsonSerializer.Serialize(report, ReportJson));
        }
    }

    public void SaveUnreadableBaseline(string agentName)
    {
        Directory.CreateDirectory(BaselinesDirectory);
        File.WriteAllText(Path.Combine(BaselinesDirectory, $"{agentName}.json"), "{ not json");
    }

    /// <summary>The quality gate's governance file, which <see cref="SaveGovernance"/> writes.</summary>
    public string GovernanceFile => Path.Combine(_root, "governance.json");

    /// <summary>The day the quality gate thinks it is, for risk acceptances' dates.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Writes a test case file as given, for fields <see cref="AddCase"/> doesn't take, e.g. a principle or planted data.</summary>
    public void AddCaseFile(string agentName, string caseName, object content)
    {
        var path = Path.Combine(TestCasesDirectory, agentName, $"{caseName}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(content));
    }

    public void SaveGovernance(object records) => File.WriteAllText(GovernanceFile, JsonSerializer.Serialize(records, ReportJson));

    /// <summary>The Responsible AI quality gate on top of the release gate, with a judge by default.</summary>
    /// <param name="policy">Extra <c>AiAgents:Evaluation:QualityGate</c> settings, by name.</param>
    public IResponsibleAiQualityGate BuildQualityGate(bool judge = true, Dictionary<string, string?>? settings = null,
        Dictionary<string, string?>? policy = null, Action<QualityGateBuilder>? configure = null)
    {
        var values = new Dictionary<string, string?> { ["AiAgents:Evaluation:QualityGate:GovernanceFile"] = GovernanceFile };

        // Each agent's system prompt, so the manifest can fingerprint it.
        foreach (var agent in Agents)
        {
            var prompt = Path.Combine(_root, "prompts", $"{agent.SystemPromptKey}.md");
            Directory.CreateDirectory(Path.GetDirectoryName(prompt)!);
            File.WriteAllText(prompt, $"You brief ministers on {agent.SystemPromptKey} records.");
            values[$"AiAgents:PromptFiles:SystemPrompts:{agent.SystemPromptKey}"] = prompt;
        }

        foreach (var (name, value) in policy ?? [])
        {
            values[$"AiAgents:Evaluation:QualityGate:{name}"] = value;
        }

        _provider = Provider(judge, settings, null, values, agents => agents.AddResponsibleAiQualityGate(configure));
        return _provider.GetRequiredService<IResponsibleAiQualityGate>();
    }

    /// <param name="judge">Whether an evaluator is registered, so answers are scored.</param>
    /// <param name="settings">Extra <c>AiAgents:Evaluation:ReleaseGate</c> settings, by name.</param>
    public IAgentReleaseGate Build(bool judge = false, Dictionary<string, string?>? settings = null,
        Action<ReleaseGateSettings>? configure = null)
    {
        _provider = Provider(judge, settings, configure, [], _ => { });
        return _provider.GetRequiredService<IAgentReleaseGate>();
    }

    private ServiceProvider Provider(bool judge, Dictionary<string, string?>? settings, Action<ReleaseGateSettings>? configure,
        Dictionary<string, string?> extra, Action<AgentsBuilder> addMore)
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

        foreach (var (name, value) in extra)
        {
            values[name] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new ListLoggerProvider(Logs, LogFields)));
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), agents =>
        {
            agents.UseCredential(Substitute.For<TokenCredential>()).AddReleaseGate(configure);
            if (judge)
            {
                agents.AddCustomQualityEvaluation(_ => Judge);
            }

            addMore(agents);
        });
        services.AddSingleton(AgentService);
        services.AddSingleton(Runtime);
        services.AddSingleton(FoundryEvaluations.Client());
        GuardrailChecks.ForEach(check => services.AddSingleton(check));

        // Not the pipeline's: a test run in GitHub Actions mustn't write to the job's summary.
        services.AddSingleton<TimeProvider>(new FixedTime(Now));
        services.AddSingleton(new QualityGateReportWriter(_ => null));

        return services.BuildServiceProvider();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        _provider?.Dispose();
        FoundryEvaluations.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class ListLoggerProvider(List<(LogLevel, string)> logs, List<(LogLevel, IReadOnlyDictionary<string, object?>)> fields)
        : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(logs, fields);

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    private sealed class ListLogger(List<(LogLevel, string)> logs, List<(LogLevel, IReadOnlyDictionary<string, object?>)> fields)
        : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (logs)
            {
                logs.Add((logLevel, formatter(state, exception)));
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    fields.Add((logLevel, values.ToDictionary(static field => field.Key, static field => field.Value)));
                }
            }
        }
    }
}

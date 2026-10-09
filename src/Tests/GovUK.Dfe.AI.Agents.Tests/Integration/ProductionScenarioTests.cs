using Azure.AI.Projects.Agents;
using Azure.Core;
using GovUK.Dfe.AI.Agents.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Concurrency.Interfaces;
using GovUK.Dfe.AI.Agents.Context;
using GovUK.Dfe.AI.Agents.Extensions;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Validators;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenAI.Responses;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Integration;

/// <summary>
/// Production scenarios through the recommended entry point, <c>AddAgents</c>: several app instances (and
/// apps) share one Foundry project, as they do when an app scales out or is redeployed. Each instance is its own
/// service provider built from its own configuration; only the Foundry admin API (<see cref="InMemoryFoundry"/>)
/// and the Responses API (<see cref="ScriptedResponsesClient"/>) are fakes, and both are shared.
/// </summary>
public sealed class ProductionScenarioTests : IDisposable
{
    private const string DefaultModel = "my-connection/gpt-4o";
    private const string FallbackText = "This section could not be generated due to an error retrieving or analysing evidence.";

    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private readonly string _promptDirectory = Directory.CreateTempSubdirectory("aiagents-prod-").FullName;
    private readonly InMemoryFoundry _foundry = new();
    private readonly ScriptedResponsesClient _responses = new();
    private readonly CollectingLoggerProvider _logs = new();
    private readonly List<ServiceProvider> _instances = [];
    private int _promptFiles;

    private static readonly AgentDefinition Ofsted = new("ofsted-agent", "Ofsted");

    public void Dispose()
    {
        _instances.ForEach(instance => instance.Dispose());
        Directory.Delete(_promptDirectory, recursive: true);
    }

    // ===================== Harness =====================

    /// <summary>The settings one app instance deploys with; <paramref name="ofstedPrompt"/> is its Ofsted prompt file's content.</summary>
    private Dictionary<string, string?> Settings(string application = "briefing-tool", string? ofstedPrompt = "You analyse Ofsted reports.")
    {
        var settings = new Dictionary<string, string?>
        {
            ["AiAgents:ApplicationName"] = application,
            ["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/shared",
            ["AiAgents:Foundry:DefaultModel"] = DefaultModel,
        };
        if (ofstedPrompt is not null)
        {
            settings["AiAgents:PromptFiles:SystemPrompts:Ofsted"] = PromptFile(ofstedPrompt);
        }

        return settings;
    }

    private string PromptFile(string content)
    {
        var path = Path.Combine(_promptDirectory, $"prompt-{Interlocked.Increment(ref _promptFiles)}.md");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Starts one app instance, wired exactly as an app would with <c>AddAgents</c>. With <paramref name="tools"/>,
    /// ofsted-agent gets tools the app runs itself.
    /// </summary>
    private ServiceProvider StartInstance(Dictionary<string, string?> settings, AgentDefinition[]? definitions = null,
        FakeToolServer? tools = null, IRunSlotStore? sharedSlots = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), agents =>
        {
            agents.UseCredential(Substitute.For<TokenCredential>()).AddAgents(definitions ?? [Ofsted]);
            if (tools is not null)
            {
                agents.AddTools("ofsted-agent", tools);
            }
        });

        services.AddSingleton(_foundry.Admin);
        services.AddSingleton<IFoundryResponsesClient>(_responses);

        if (sharedSlots is not null)
        {
            services.AddSingleton(sharedSlots);   // stands in for the blob container every instance shares
        }

        var instance = services.BuildServiceProvider();
        _instances.Add(instance);
        return instance;
    }

    private static IAgentService Agents(ServiceProvider instance) => instance.GetRequiredService<IAgentService>();

    private static Task StartToolCheckAsync(ServiceProvider instance)
        => instance.GetServices<IHostedService>().OfType<AgentToolCompatibilityValidator>().Single().StartAsync(CancellationToken.None);

    private IReadOnlyList<string> VersionNumbers(string agentName) => [.. _foundry.Versions(agentName).Select(version => version.Version)];

    /// <summary>A school-performance tool server whose get_performance_data returns <paramref name="output"/>.</summary>
    private static FakeToolServer PerformanceTools(string output) => new("get_performance_data") { Output = output };

    public sealed record OfstedFindings(string Rating, IReadOnlyList<string> Strengths);

    // ===================== Scaling out and redeploying =====================

    [Fact]
    public async Task ScaledOut_EveryInstanceWithTheSamePrompt_SharesOneVersion()
    {
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Good."));
        var instances = Enumerable.Range(0, 3).Select(_ => StartInstance(Settings())).ToList();

        foreach (var instance in instances)
        {
            await Agents(instance).RunAsync(Ofsted, "Summarise.", cancellationToken: cancellationToken);
        }

        Assert.Equal(["1"], VersionNumbers("ofsted-agent"));
        Assert.All(_responses.Calls, call => Assert.Equal("1", call.AgentVersion));
    }

    [Fact]
    public async Task RollingDeploy_OldAndNewInstancesEachRunTheirOwnVersion_WithoutCreatingVersionsBackAndForth()
    {
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Good."));
        var oldInstance = StartInstance(Settings(ofstedPrompt: "You analyse Ofsted reports."));
        var newInstance = StartInstance(Settings(ofstedPrompt: "You analyse Ofsted reports. Cite the inspection date."));

        await Agents(oldInstance).RunAsync(Ofsted, "Summarise.", cancellationToken: cancellationToken);
        await Agents(newInstance).RunAsync(Ofsted, "Summarise.", cancellationToken: cancellationToken);
        // While both are live, each keeps finding its own version among the recent ones.
        await Agents(oldInstance).RunAsync(Ofsted, "Summarise.", cancellationToken: cancellationToken);
        await Agents(newInstance).RunAsync(Ofsted, "Summarise.", cancellationToken: cancellationToken);

        Assert.Equal(["1", "2"], VersionNumbers("ofsted-agent"));
        Assert.Equal(["1", "2", "1", "2"], _responses.Calls.Select(call => call.AgentVersion));
    }

    // ===================== Version pruning =====================

    [Fact]
    public async Task KeepLatestVersions_AfterFivePromptChanges_KeepsFiveFourAndThree_PlusTheVersionProductionIsPinnedTo()
    {
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Good."));

        for (var change = 1; change <= 5; change++)
        {
            var settings = Settings(ofstedPrompt: $"You analyse Ofsted reports. Revision {change}.");
            settings["AiAgents:KeepLatestVersions"] = "3";
            settings["AiAgents:ProtectedVersions:ofsted-agent:0"] = "1";   // production still runs version 1
            await Agents(StartInstance(settings)).RunAsync(Ofsted, "Summarise.", cancellationToken: cancellationToken);
        }

        Assert.Equal(["1", "3", "4", "5"], VersionNumbers("ofsted-agent"));
    }

    [Fact]
    public async Task ScaledOut_TwoInstancesPruningAtOnce_BothSucceed_AndKeepTheNewestVersions()
    {
        for (var version = 1; version <= 5; version++)
        {
            _foundry.Seed("ofsted-agent", DefaultModel, $"Revision {version}.");
        }

        var first = StartInstance(Settings()).GetRequiredService<IAgentFactory>();
        var second = StartInstance(Settings()).GetRequiredService<IAgentFactory>();
        // The second instance prunes just before the first deletes, so the first finds versions 1 and 2 already gone.
        _foundry.BeforeNextVersionDelete = () => second.PruneVersionsAsync("ofsted-agent", 3, cancellationToken);

        await first.PruneVersionsAsync("ofsted-agent", 3, cancellationToken);

        Assert.Null(_foundry.BeforeNextVersionDelete);   // the race really happened
        Assert.Equal(["3", "4", "5"], VersionNumbers("ofsted-agent"));
        Assert.Empty(_logs.AtLevel(LogLevel.Error));
    }

    [Fact]
    public async Task PinnedInstance_OnlyUsesItsVersion_AndNeverCreatesOrPrunes_EvenWithKeepLatestVersions()
    {
        for (var version = 1; version <= 5; version++)
        {
            _foundry.Seed("ofsted-agent", DefaultModel, $"Revision {version}.");
        }

        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Good."));
        var settings = Settings(ofstedPrompt: "A prompt no deployed version has.");
        settings["AiAgents:VersionPins:ofsted-agent"] = "2";
        settings["AiAgents:KeepLatestVersions"] = "2";
        var production = StartInstance(settings);

        await Agents(production).RunAsync(Ofsted, "Summarise.", cancellationToken: cancellationToken);
        await production.GetRequiredService<IAgentFactory>().PruneVersionsAsync("ofsted-agent", 1, cancellationToken);

        Assert.Equal("2", Assert.Single(_responses.Calls).AgentVersion);
        Assert.Equal(["1", "2", "3", "4", "5"], VersionNumbers("ofsted-agent"));
        Assert.Empty(_foundry.CreatedNames);
    }

    // ===================== Centrally managed agents =====================

    [Fact]
    public async Task CentralProvisioning_ThenAConsumingApp_RunsThePinnedVersion_AndRunsItsToolsItself()
    {
        var definition = Ofsted with { AllowedTools = ["get_performance_data"] };

        // 1. A release job provisions the agent - no runs.
        var job = StartInstance(Settings(application: "agent-provisioning"), [definition], PerformanceTools("unused"));
        var provisioned = Assert.Single(await Agents(job).ProvisionAsync([definition], cancellationToken));
        Assert.Empty(_responses.Calls);

        // 2. A consuming app with no prompt file pins that version and runs its tools with its own token.
        var settings = Settings(ofstedPrompt: null);
        settings["AiAgents:ExternallyManagedAgents:ofsted-agent"] = provisioned.Version;
        var consumer = StartInstance(settings, [definition], PerformanceTools("KS2: 72% met the expected standard."));
        await StartToolCheckAsync(consumer);

        _responses.Reply("ofsted-agent",
            FoundryResponses.FunctionCall("r1", "call-1", "get_performance_data"),
            FoundryResponses.Completed("r2", "72% met the expected standard."));
        var result = await Agents(consumer).RunAsync(definition, "Brief me on URN 100000.", cancellationToken: cancellationToken);

        Assert.Equal("72% met the expected standard.", result.Output);
        Assert.All(_responses.Calls, call => Assert.Equal(provisioned.Version, call.AgentVersion));
        Assert.Contains("KS2: 72% met the expected standard.", _responses.Calls[^1].SerializedInput, StringComparison.Ordinal);
        Assert.Equal(["ofsted-agent"], _foundry.CreatedNames);   // only the job ever created anything
    }

    // ===================== A briefing =====================

    [Fact]
    public async Task Briefing_SpecialistsInParallel_ThenSynthesis_WithEvidenceToolsAFailureAndTokenTotals()
    {
        var settings = Settings();
        settings["AiAgents:PromptFiles:SystemPrompts:Trust"] = PromptFile("You analyse academy trusts.");
        settings["AiAgents:PromptFiles:SystemPrompts:News"] = PromptFile("You summarise local news.");
        settings["AiAgents:PromptFiles:SystemPrompts:Synthesis"] = PromptFile("You write briefings.");
        var ofsted = Ofsted with { AllowedTools = ["get_performance_data"] };
        var trust = new AgentDefinition("trust-agent", "Trust");
        var news = new AgentDefinition("news-agent", "News", IsManagedAgent: false);
        var synthesis = new AgentDefinition("synthesis-agent", "Synthesis");
        var app = StartInstance(settings, [ofsted, trust, news, synthesis], PerformanceTools("KS2: 72%."));

        _responses.Reply("ofsted-agent",
            FoundryResponses.FunctionCall("o1", "call-1", "get_performance_data"),
            FoundryResponses.Completed("o2", "Rated Good; KS2 72%.", totalTokens: 100));
        _responses.Reply("trust-agent", FoundryResponses.Completed("t1", "Part of a 12-academy trust.", totalTokens: 50));
        _responses.Fail("news-agent", new InvalidOperationException("News search is down."));
        _responses.Reply("synthesis-agent", FoundryResponses.Completed("s1", "Briefing: a Good school in a 12-academy trust.", totalTokens: 200));

        var specialists = await Agents(app).RunParallelAsync([ofsted, trust, news],
            (definition, _) => Task.FromResult($"Brief me on URN 100000 ({definition.Name})."), new AgentContext(),
            cancellationToken: cancellationToken,
            resolveEvidence: (definition, _) => Task.FromResult(definition == trust ? "Trust record: 12 academies." : null));
        var succeeded = specialists.Where(result => result.Output != FallbackText).ToList();
        var briefing = await Agents(app).RunAsync(synthesis, "Write the briefing.",
            string.Join("\n\n", succeeded.Select(result => $"{result.AgentName}: {result.Output}")), cancellationToken);

        Assert.Equal(FallbackText, specialists.Single(result => result.AgentName == "news-agent").Output);
        Assert.Contains("Trust record: 12 academies.", _responses.CallsFor("trust-agent")[0].SerializedInput, StringComparison.Ordinal);
        Assert.DoesNotContain("Trust record", _responses.CallsFor("ofsted-agent")[0].SerializedInput, StringComparison.Ordinal);
        var synthesisInput = Assert.Single(_responses.CallsFor("synthesis-agent")).SerializedInput;
        Assert.Contains("Rated Good; KS2 72%.", synthesisInput, StringComparison.Ordinal);
        Assert.Contains("Part of a 12-academy trust.", synthesisInput, StringComparison.Ordinal);
        Assert.Equal("Briefing: a Good school in a 12-academy trust.", briefing.Output);

        var usage = specialists.Append(briefing).ToTokenUsageSummary();
        Assert.Equal(350, usage.Total.TotalTokens);
        Assert.Equal(100, usage.ByAgent["ofsted-agent"].TotalTokens);
        Assert.Equal(200, usage.ByAgent["synthesis-agent"].TotalTokens);
    }

    /// <summary>Three specialists that each take a moment to answer, with their prompt files.</summary>
    private (Dictionary<string, string?> Settings, AgentDefinition[] Specialists) SlowSpecialists()
    {
        var settings = Settings();
        settings["AiAgents:PromptFiles:SystemPrompts:Trust"] = PromptFile("You analyse academy trusts.");
        settings["AiAgents:PromptFiles:SystemPrompts:News"] = PromptFile("You summarise local news.");
        AgentDefinition[] specialists = [Ofsted, new("trust-agent", "Trust"), new("news-agent", "News", IsManagedAgent: false)];
        foreach (var specialist in specialists)
        {
            _responses.ReplyAfter(specialist.Name, TimeSpan.FromMilliseconds(150), FoundryResponses.Completed("r", "Done."));
        }

        return (settings, specialists);
    }

    private static Task<IReadOnlyList<AgentResult>> BriefAsync(ServiceProvider instance, AgentDefinition[] specialists)
        => Agents(instance).RunParallelAsync(specialists, (_, _) => Task.FromResult("Brief me."), new AgentContext());

    [Fact]
    public async Task GlobalLimit_InstancesRunningBriefingsTogether_NeverExceedIt()
    {
        var (settings, specialists) = SlowSpecialists();
        settings["AiAgents:GlobalConcurrency:MaxConcurrentRuns"] = "2";
        settings["AiAgents:GlobalConcurrency:BlobContainerUri"] = "https://account.blob.core.windows.net/run-slots";
        var sharedSlots = new InMemoryRunSlotStore(capacity: 2);
        var instances = Enumerable.Range(0, 3).Select(_ => StartInstance(new(settings), specialists, sharedSlots: sharedSlots)).ToList();

        var briefings = await Task.WhenAll(instances.Select(instance => BriefAsync(instance, specialists)));

        Assert.All(briefings.SelectMany(results => results), result => Assert.Equal("Done.", result.Output));
        Assert.Equal(2, _responses.PeakConcurrentResponses);
    }

    [Fact]
    public async Task MaxConcurrency_AppliesToEveryBriefingOnTheInstance_NotEachOneSeparately()
    {
        var (settings, specialists) = SlowSpecialists();
        settings["AiAgents:MaxConcurrency"] = "2";
        var instance = StartInstance(settings, specialists);

        await Task.WhenAll(BriefAsync(instance, specialists), BriefAsync(instance, specialists));

        Assert.Equal(2, _responses.PeakConcurrentResponses);
    }

    [Fact]
    public async Task Briefing_SpecialistsReallyRunAtTheSameTime()
    {
        var settings = Settings();
        settings["AiAgents:PromptFiles:SystemPrompts:Trust"] = PromptFile("You analyse academy trusts.");
        settings["AiAgents:PromptFiles:SystemPrompts:News"] = PromptFile("You summarise local news.");
        var definitions = new[] { Ofsted, new AgentDefinition("trust-agent", "Trust"), new AgentDefinition("news-agent", "News", IsManagedAgent: false) };
        foreach (var definition in definitions)
        {
            _responses.ReplyAfter(definition.Name, TimeSpan.FromMilliseconds(300), FoundryResponses.Completed("r", "Done."));
        }

        var results = await Agents(StartInstance(settings, definitions)).RunParallelAsync(definitions,
            (_, _) => Task.FromResult("Brief me."), new AgentContext(), cancellationToken: cancellationToken);

        Assert.All(results, result => Assert.Equal("Done.", result.Output));
        Assert.Equal(3, _responses.PeakConcurrentResponses);
    }

    [Fact]
    public async Task StructuredOutput_TheDefinitionsSchemaIsDeployedWithTheAgent_AndTheAnswerReadsBackTyped()
    {
        var definition = Ofsted with { OutputSchema = AgentOutputSchema.For<OfstedFindings>("ofsted_findings") };
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", """{"rating":"Good","strengths":["Leadership","Reading"]}"""));

        var result = await Agents(StartInstance(Settings(), [definition])).RunAsync(definition, "Summarise.", cancellationToken: cancellationToken);

        Assert.NotNull(_foundry.Definition("ofsted-agent", "1").TextOptions);
        var findings = result.ReadOutputAs<OfstedFindings>();
        Assert.Equal("Good", findings.Rating);
        Assert.Equal(["Leadership", "Reading"], findings.Strengths);
    }

    [Fact]
    public async Task Citations_AreRequiredByDefault_AndAnAnswerCitingEvidenceThatDoesntExist_IsCorrected()
    {
        var definition = Ofsted;   // RequiredCitations is on unless the definition turns it off
        _responses.Reply("ofsted-agent",
            FoundryResponses.Completed("r1", "Rated Outstanding [Evidence 5]."),
            FoundryResponses.Completed("r2", "Rated Good [Evidence 1]."));
        const string SearchEvidence = "--- ofsted_index Evidence 1 ---\nRated Good.\n\n--- ofsted_index Evidence 2 ---\nInspected 2024.";

        var result = await Agents(StartInstance(Settings(), [definition])).RunAsync(definition, "Summarise.", SearchEvidence, cancellationToken);

        Assert.Equal("Rated Good [Evidence 1].", result.Output);
        Assert.Contains("Cite the evidence each point relies on", _responses.Calls[0].SerializedInput, StringComparison.Ordinal);
        Assert.Contains("[Evidence 5] doesn't exist", _responses.Calls[1].SerializedInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseGate_TestsTheCandidate_WithoutPublishingAVersion()
    {
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Good."));
        var app = StartInstance(Settings());

        var report = await app.GetRequiredService<IAgentTestRunner>().RunAsync(Ofsted,
            [new AgentTestCase("good-school", "Summarise.") { MustMention = ["Good"] }], cancellationToken: cancellationToken);

        Assert.True(report.Passed);
        Assert.Empty(_foundry.Versions("ofsted-agent"));   // no managed version for "latest" to pick up
        Assert.Empty(_foundry.AgentNames);                  // the ephemeral copy was deleted
    }

    // ===================== Several apps in one Foundry project =====================

    [Fact]
    public async Task SharedProject_EachAppsOrphanSweep_RemovesOnlyItsOwnLeftoverEphemeralAgents_NeverManagedOnes()
    {
        _foundry.Seed("ofsted-agent", DefaultModel, "A managed agent that must survive the sweep.");
        var webSearch = new AgentDefinition("web-search-agent", "WebSearch", IsManagedAgent: false);
        Dictionary<string, string?> SettingsFor(string application)
        {
            var settings = Settings(application);
            settings["AiAgents:PromptFiles:SystemPrompts:WebSearch"] = PromptFile("You search the web.");
            return settings;
        }

        var briefingTool = StartInstance(SettingsFor("briefing-tool"), [webSearch]);
        var casework = StartInstance(SettingsFor("casework-tool"), [webSearch]);
        _responses.Reply("web-search-agent", FoundryResponses.Completed("r1", "News."));
        _foundry.FailDeletes = true;   // both apps' clean-ups fail, leaving an orphan each
        await Agents(briefingTool).RunAsync(webSearch, "Search.", cancellationToken: cancellationToken);
        await Agents(casework).RunAsync(webSearch, "Search.", cancellationToken: cancellationToken);
        var before = _foundry.AgentNames.ToList();
        _foundry.FailDeletes = false;

        var swept = await briefingTool.GetRequiredService<IAgentRuntimeService>().DeleteOrphanedEphemeralAgentsAsync(TimeSpan.FromHours(2), cancellationToken);

        var sweptName = Assert.Single(swept);
        Assert.StartsWith("web-search-agent-", sweptName, StringComparison.Ordinal);
        Assert.Equal(3, before.Count);   // two orphans and the managed agent
        Assert.Equal(before.Where(name => name != sweptName), _foundry.AgentNames);   // casework's orphan and ofsted-agent are left alone
    }
}

using Azure.Core;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Concurrency;
using GovUK.Dfe.AI.Agents.Context.Interfaces;
using GovUK.Dfe.AI.Agents.Extensibility;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.Factories;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Providers.Interfaces;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Services;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests;

/// <summary><c>AddAgents</c>: settings, validation in one error, credentials per service, and versions.</summary>
public sealed class AddAgentsTests
{
    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["AiAgents:ApplicationName"] = "briefing-tool",
        ["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/briefings",
        ["AiAgents:Foundry:DefaultModel"] = "my-connection/gpt-4o",
        ["AiAgents:Authentication:TenantId"] = "tenant-1",
        ["AiAgents:Authentication:ClientId"] = "client-1",
        ["AiAgents:Authentication:ClientSecret"] = "secret-1",
        ["AiAgents:RunTimeout"] = "00:02:00",
        ["AiAgents:MaxConcurrency"] = "4",
        ["AiAgents:VersionPins:ofsted-agent"] = "3",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings, Action<AgentsBuilder>? configure = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), configure);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> WithGlobalConcurrency(Dictionary<string, string?> settings)
    {
        settings["AiAgents:GlobalConcurrency:MaxConcurrentRuns"] = "20";
        settings["AiAgents:GlobalConcurrency:BlobContainerUri"] = "https://account.blob.core.windows.net/run-slots";
        return settings;
    }

    // ===================== Credentials per service =====================

    private static Dictionary<string, string?> WithoutDefaultPrincipal(Dictionary<string, string?> settings)
    {
        foreach (var key in settings.Keys.Where(key => key.StartsWith("AiAgents:Authentication:", StringComparison.Ordinal)).ToList())
        {
            settings.Remove(key);
        }

        return settings;
    }

    private static void OwnPrincipal(Dictionary<string, string?> settings, string path, string tenant = "tenant-1")
    {
        settings[$"AiAgents:{path}:Authentication:TenantId"] = tenant;
        settings[$"AiAgents:{path}:Authentication:ClientId"] = $"{path}-client";
        settings[$"AiAgents:{path}:Authentication:ClientSecret"] = $"{path}-secret";
    }

    [Fact]
    public void AServiceWithoutItsOwnBlock_FallsBackToTheDefault_SoTheDefaultIsRequired()
    {
        var settings = WithoutDefaultPrincipal(WithGlobalConcurrency(ValidSettings()));
        OwnPrincipal(settings, "Foundry");   // the run-slot store has no block of its own

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("AiAgents:Authentication:ClientId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompleteServiceBlock_IsNamedInTheError()
    {
        var settings = WithGlobalConcurrency(ValidSettings());
        settings["AiAgents:GlobalConcurrency:Authentication:TenantId"] = "tenant-1";
        settings["AiAgents:GlobalConcurrency:Authentication:ClientId"] = "slots-client";   // no secret

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("AiAgents:GlobalConcurrency:Authentication:ClientSecret", ex.Message, StringComparison.Ordinal);
    }

    // ===================== Add-on packages =====================

    /// <summary>An add-on built as the real ones are: it reads its own block, signs in through core, and reports problems.</summary>
    private sealed class FakePackage : IAgentsPackage
    {
        public string Name => "Fake";

        public int Registrations { get; private set; }

        public TokenCredential? Credential { get; private set; }

        public IReadOnlyCollection<string> CodeCredentials { get; private set; } = [];

        public string? ApplicationName { get; private set; }

        public IAgentToolProvider Tools { get; } = Substitute.For<IAgentToolProvider>();

        public void Register(AgentsPackageContext context)
        {
            Registrations++;
            CodeCredentials = [.. context.CodeCredentials];
            ApplicationName = context.ApplicationName;
            context.AddTools("ofsted-agent", _ => Tools);
            var section = context.Section.GetSection("Fake");
            if (string.IsNullOrWhiteSpace(section["Endpoint"]))
            {
                context.ReportProblem("Fake:Endpoint");
            }

            Credential = context.CredentialFor("Fake", section.GetSection("Authentication").Get<AgentsOptions.ServicePrincipalSettings>(),
                "Fake:Authentication");
        }
    }

    [Fact]
    public async Task AnAddOnsProblems_AreListedWithCoresProblems_InOneError()
    {
        var settings = WithoutDefaultPrincipal(ValidSettings());
        settings.Remove("AiAgents:Foundry:Endpoint");
        var package = new FakePackage();

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings, agents => agents.AddPackage(package)));

        foreach (var setting in new[] { "AiAgents:Foundry:Endpoint", "AiAgents:Fake:Endpoint", "AiAgents:Authentication:ClientId" })
        {
            Assert.Contains(setting, ex.Message, StringComparison.Ordinal);
        }

        // Core and the add-on both fall back to the missing default; it's listed once.
        Assert.Single(ex.Message.Split(", "), problem => problem.Contains("AiAgents:Authentication:TenantId", StringComparison.Ordinal));
        // The add-on gets a credential that can't be used by accident; startup has already failed anyway.
        var request = new TokenRequestContext(["scope"]);
        Assert.Throws<InvalidOperationException>(() => package.Credential!.GetToken(request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await package.Credential!.GetTokenAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AnAddOn_IsRegisteredOnce_AndSignsInWithItsCodeCredential()
    {
        var settings = ValidSettings();
        settings["AiAgents:Fake:Endpoint"] = "https://fake.example";
        var credential = Substitute.For<TokenCredential>();
        var package = new FakePackage();

        using var provider = Build(settings, agents => agents.AddPackage(package).AddPackage(new FakePackage())
            .UseCredentialFor("Fake", credential));

        Assert.Equal(1, package.Registrations);
        Assert.Same(credential, package.Credential);
        Assert.Equal(["Fake"], package.CodeCredentials);
        Assert.Equal("briefing-tool", package.ApplicationName);
        Assert.Same(package.Tools, Assert.Single(provider.GetServices<AgentToolBinding>(), binding => binding.AgentName == "ofsted-agent").Provider);
    }

    [Fact]
    public void AddAgents_IsTheOnlyPublicWayToRegisterTheLibrary()
        => Assert.Equal([nameof(AgentsServiceCollectionExtensions.AddAgents)],
            typeof(AgentsServiceCollectionExtensions).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Select(method => method.Name).Distinct());

    [Fact]
    public void ExternallyManagedAgents_AreOnlyRun_AtTheirListedVersion_OrTheLatest()
    {
        var settings = ValidSettings();
        settings["AiAgents:ExternallyManagedAgents:trust-agent"] = "4";
        settings["AiAgents:ExternallyManagedAgents:news-agent"] = "latest";

        using var provider = Build(settings);

        var external = provider.GetServices<IManagedAgentProvider>().ToList();
        Assert.Equal(["news-agent", "trust-agent"], external.Select(agent => agent.AgentName).Order());
        Assert.All(external, agent => Assert.False(agent.CreatesAgent));
        var pins = provider.GetRequiredService<AgentVersionPinningOptions>();
        Assert.Equal("4", pins.GetPinnedVersion("trust-agent"));
        Assert.Null(pins.GetPinnedVersion("news-agent"));
        Assert.Equal("3", pins.GetPinnedVersion("ofsted-agent"));   // VersionPins still apply to the app's own agents
    }

    [Fact]
    public void RejectsTheOldListForm_RatherThanRunAnAgentCalledZero()
    {
        var settings = ValidSettings();
        settings["AiAgents:ExternallyManagedAgents:0"] = "trust-agent";

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("not a list", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnAgentVersionedInBothPlaces()
    {
        var settings = ValidSettings();
        settings["AiAgents:ExternallyManagedAgents:ofsted-agent"] = "4";   // also under VersionPins

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("AiAgents:VersionPins:ofsted-agent", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AiAgents:GlobalConcurrency:MaxConcurrentRuns", "20", "AiAgents:GlobalConcurrency:BlobContainerUri")]
    [InlineData("AiAgents:GlobalConcurrency:BlobContainerUri", "https://account.blob.core.windows.net/run-slots", "AiAgents:GlobalConcurrency:MaxConcurrentRuns")]
    [InlineData("AiAgents:MaxEvidenceCharacters", "0", "AiAgents:MaxEvidenceCharacters")]
    [InlineData("AiAgents:AgentCacheDuration", "-00:00:01", "AiAgents:AgentCacheDuration")]
    [InlineData("AiAgents:MaxConcurrency", "0", "AiAgents:MaxConcurrency")]
    [InlineData("AiAgents:MaxOutputTokensPerRun", "15", "AiAgents:MaxOutputTokensPerRun (must be at least 16)")]
    public void RejectsIncompleteOrInvalidLimits(string setting, string value, string reported)
    {
        var settings = ValidSettings();
        settings[setting] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains(reported, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_AreSizedForGpt51()
    {
        using var provider = Build(ValidSettings());
        var run = provider.GetRequiredService<AgentRunOptions>();

        Assert.Equal((64_000, 200_000, 40_000), (run.MaxOutputTokensPerRun, run.MaxEvidenceCharacters, run.MaxToolOutputCharacters));
        Assert.Equal(6, new AgentsOptions().MaxRetries);
    }

    [Fact]
    public void MapsEvidenceAndCacheSettings()
    {
        var settings = ValidSettings();
        settings["AiAgents:MaxEvidenceCharacters"] = "50000";
        settings["AiAgents:AgentCacheDuration"] = "00:00:00";
        settings["AiAgents:MaxOutputTokensPerRun"] = "8000";


        using var provider = Build(settings);
        Assert.Equal(50_000, provider.GetRequiredService<AgentRunOptions>().MaxEvidenceCharacters);
        Assert.Equal(8_000, provider.GetRequiredService<AgentRunOptions>().MaxOutputTokensPerRun);
        Assert.Equal(TimeSpan.Zero, provider.GetRequiredService<FoundryAgentFactoryOptions>().AgentCacheDuration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheOrphanSweep_RunsOnlyWhenTheAppAddsIt(bool added)
    {
        using var provider = Build(ValidSettings(), agents =>
        {
            if (added)
            {
                agents.AddEphemeralAgentSweep();
            }
        });

        Assert.Equal(added, provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<EphemeralAgentSweepService>().Any());
    }

    [Fact]
    public void TheReleaseGate_IsAvailable_WithoutAnyEvaluator()
    {
        using var provider = Build(ValidSettings());

        Assert.NotNull(provider.GetRequiredService<IAgentTestRunner>());
        Assert.Null(provider.GetService<IAgentRunEvaluator>());
    }

    [Fact]
    public void RejectsTwoDefinitionsWithTheSameName()
        => Assert.Throws<ArgumentException>(() => Build(ValidSettings(), agents => agents.AddAgents(
            new AgentDefinition("ofsted-agent", "Ofsted"), new AgentDefinition("ofsted-agent", "Other"))));

    [Fact]
    public void RegistersTheAgentServiceAndOptions_FromOneConfigurationSection()
    {
        using var provider = Build(ValidSettings());

        Assert.NotNull(provider.GetRequiredService<IAgentService>());
        var runOptions = provider.GetRequiredService<AgentRunOptions>();
        Assert.Equal("briefing-tool", runOptions.ApplicationName);
        Assert.Equal(TimeSpan.FromMinutes(2), runOptions.RunTimeout);
        Assert.Equal(4, runOptions.MaxConcurrency);
        Assert.Equal("3", provider.GetRequiredService<AgentVersionPinningOptions>().GetPinnedVersion("ofsted-agent"));
    }

    [Fact]
    public void ListsEveryMissingSetting_InOneError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build([]));

        foreach (var setting in new[]
                 {
                     "AiAgents:Foundry:Endpoint", "AiAgents:Foundry:DefaultModel", "AiAgents:Authentication:TenantId",
                     "AiAgents:Authentication:ClientId", "AiAgents:Authentication:ClientSecret",
                 })
        {
            Assert.Contains(setting, ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-1")]
    public void RejectsKeepingFewerThanTwoVersions(string keepLatestVersions)
    {
        var settings = ValidSettings();
        settings["AiAgents:KeepLatestVersions"] = keepLatestVersions;

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("AiAgents:KeepLatestVersions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistersNoSearch_WhenTheSectionIsAbsent()
    {

        using var provider = Build(ValidSettings());
        Assert.Null(provider.GetService<IContextRetriever>());
    }
}

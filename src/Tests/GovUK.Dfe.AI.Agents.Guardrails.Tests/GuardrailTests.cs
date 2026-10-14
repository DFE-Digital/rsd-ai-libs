using Azure.Core;
using Azure.ResourceManager.CognitiveServices.Models;
using Azure;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Enums;
using GovUK.Dfe.AI.Agents.Guardrails.Options;
using GovUK.Dfe.AI.Agents.Guardrails.Policies;
using GovUK.Dfe.AI.Agents.Guardrails.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Services;
using GovUK.Dfe.AI.Agents.Guardrails.Stores.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Stores;
using GovUK.Dfe.AI.Agents.Guardrails.Validators;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Guardrails.Tests;

/// <summary>A Foundry guardrail: applied by a provisioning job, checked by every app at startup.</summary>
public sealed class GuardrailTests
{
    private const string Subscription = "6d0e3f4a-1b2c-4d5e-8f90-123456789abc";
    private const string FoundryEndpoint = "https://foundry-1.services.ai.azure.com/api/projects/test";

    private readonly InMemoryGuardrailStore _store = new();
    private readonly CollectingLoggerProvider _logs = new();

    private static GuardrailSettings Settings(bool requireAtStartup = true) => new()
    {
        SubscriptionId = Subscription,
        ResourceGroup = "rg-1",
        Name = "briefing-guardrail",
        Deployments = ["gpt-5.1", "gpt-4o"],
        RequireAtStartup = requireAtStartup,
        Blocklists = { ["case-references"] = new() { Terms = ["Project Falcon"], Patterns = [@"CASE-\d{6}"] } },
    };

    private FoundryGuardrailsService Guardrails(GuardrailSettings? settings = null)
        => new(_store, settings ?? Settings(), NullLogger<FoundryGuardrailsService>.Instance);

    private GuardrailStartupValidator StartupCheck(GuardrailSettings settings)
    {
        var loggers = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        return new GuardrailStartupValidator(Guardrails(settings), settings, loggers.CreateLogger<GuardrailStartupValidator>());
    }

    [Fact]
    public async Task ProvisioningAppliesTheGuardrail_AppsPassTheCheck_ThenAChangeInThePortalIsCaught()
    {
        _store.Deployments["gpt-5.1"] = null;
        _store.Deployments["gpt-4o"] = "Microsoft.DefaultV2";

        var applied = await Guardrails().ApplyAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(applied.Passed, string.Join("; ", applied.Problems));
        var guardrail = _store.Guardrails["briefing-guardrail"];
        Assert.Empty(guardrail.WeakerThan(GuardrailPolicy.From(Settings())));
        Assert.Equal(["case-references"], guardrail.Blocklists);
        Assert.Equal(
            [new GuardrailBlocklistEntry("Project Falcon", IsRegex: false), new GuardrailBlocklistEntry(@"CASE-\d{6}", IsRegex: true)],
            _store.Blocklists["case-references"]);
        Assert.All(_store.Deployments.Values, assigned => Assert.Equal("briefing-guardrail", assigned));

        // Later, someone weakens the guardrail and drops its blocklist, removes a blocklist entry, moves one deployment
        // off the guardrail and deletes the other.
        _store.Guardrails["briefing-guardrail"] = guardrail with { BlockFrom = GuardrailSeverity.High, PromptShields = false, Blocklists = [] };
        _store.Blocklists["case-references"].Remove(new GuardrailBlocklistEntry("Project Falcon", IsRegex: false));
        _store.Deployments["gpt-5.1"] = "Microsoft.DefaultV2";
        _store.Deployments.Remove("gpt-4o");

        var checkedLater = await Guardrails().CheckAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "'briefing-guardrail': harm filters block from High, not Medium",
            "'briefing-guardrail': Prompt Shields (jailbreak) is off",
            "'briefing-guardrail': blocklist 'case-references' isn't applied",
            "blocklist 'case-references' is missing 1 of its entries",   // counts only: entries can be sensitive
            "deployment 'gpt-5.1' uses 'Microsoft.DefaultV2'",
            "deployment 'gpt-4o' doesn't exist",
        ], checkedLater.Problems);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AtStartup_AMissingGuardrail_FailsStartup_OrOnlyWarns_WhenNotRequired(bool requireAtStartup)
    {
        var check = StartupCheck(Settings(requireAtStartup));

        var ex = await Record.ExceptionAsync(() => check.StartAsync(CancellationToken.None));

        var message = requireAtStartup ? Assert.IsType<InvalidOperationException>(ex).Message : Assert.Single(_logs.AtLevel(LogLevel.Warning)).Message;
        Assert.Contains("The Foundry guardrail 'briefing-guardrail' isn't in place: the guardrail 'briefing-guardrail' doesn't exist", message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtStartup_AnAppliedGuardrail_PassesSilently()
    {
        _store.Deployments["gpt-5.1"] = null;
        _store.Deployments["gpt-4o"] = null;
        await Guardrails().ApplyAsync(cancellationToken: TestContext.Current.CancellationToken);
        var check = StartupCheck(Settings());

        await check.StartAsync(CancellationToken.None);
        await check.StopAsync(CancellationToken.None);

        Assert.Empty(_logs.AtLevel(LogLevel.Warning));
    }

    [Theory]
    [InlineData(false, true, true, "Prompt Shields (jailbreak) is off")]
    [InlineData(true, false, true, "indirect attack detection is off")]
    [InlineData(true, true, false, "protected material detection is off")]
    public void EachProtectionTurnedOff_IsReportedAsWeaker(bool promptShields, bool indirectAttacks, bool protectedMaterial, string expected)
    {
        var required = new GuardrailPolicy("g", GuardrailSeverity.Medium, true, true, true, []);
        var actual = required with { PromptShields = promptShields, IndirectAttacks = indirectAttacks, ProtectedMaterial = protectedMaterial };

        Assert.Equal([expected], actual.WeakerThan(required));
    }

    [Fact]
    public async Task ApplyingAGuardrailThatsAlreadyInPlace_WritesNothing_SoAJobWithOnlyReaderSucceeds()
    {
        _store.Deployments["gpt-5.1"] = null;
        _store.Deployments["gpt-4o"] = null;
        await Guardrails().ApplyAsync(cancellationToken: TestContext.Current.CancellationToken);
        _store.ReadOnly = true;

        var again = await Guardrails().ApplyAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(again.Passed);
        Assert.Equal(4, _store.Writes);   // the first apply only: blocklist, guardrail and two deployments
    }

    [Fact]
    public async Task ApplyingAfterAChange_WritesOnlyWhatDiffers()
    {
        _store.Deployments["gpt-5.1"] = null;
        _store.Deployments["gpt-4o"] = null;
        await Guardrails().ApplyAsync(cancellationToken: TestContext.Current.CancellationToken);
        _store.Blocklists["case-references"].Remove(new GuardrailBlocklistEntry("Project Falcon", IsRegex: false));
        var before = _store.Writes;

        await Guardrails().ApplyAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(before + 1, _store.Writes);   // just the blocklist
        Assert.Contains(new GuardrailBlocklistEntry("Project Falcon", IsRegex: false), _store.Blocklists["case-references"]);
    }

    [Fact]
    public async Task WithFailWhenUnreachable_AnUncheckableGuardrailFailsStartup()
    {
        _store.Failure = new RequestFailedException("No such host is known.");
        var settings = Settings();
        settings.FailWhenUnreachable = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartupCheck(settings).StartAsync(CancellationToken.None));

        Assert.Contains("Couldn't check the Foundry guardrail 'briefing-guardrail'", ex.Message, StringComparison.Ordinal);
        Assert.IsType<RequestFailedException>(ex.InnerException);
    }

    [Fact]
    public async Task AnIdentityWithoutTheRole_FailsWithTheRoleToGrant_ButAnUnreachableResourceManagerOnlyWarns()
    {
        _store.Failure = new RequestFailedException(403, "Forbidden");
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => StartupCheck(Settings()).StartAsync(CancellationToken.None));
        var applyDenied = await Assert.ThrowsAsync<InvalidOperationException>(() => Guardrails().ApplyAsync(cancellationToken: TestContext.Current.CancellationToken));

        _store.Failure = new RequestFailedException("No such host is known.");
        await StartupCheck(Settings()).StartAsync(CancellationToken.None);

        Assert.Contains("needs Reader on the Foundry resource", denied.Message, StringComparison.Ordinal);
        Assert.Contains("needs Cognitive Services Contributor", applyDenied.Message, StringComparison.Ordinal);
        Assert.Single(_logs.AtLevel(LogLevel.Warning));
    }

    [Theory]
    [InlineData(GuardrailSeverity.Low, true, false, true, "case-references")]
    [InlineData(GuardrailSeverity.High, false, true, false, null)]
    public void TheRaiPolicyWritten_ReadsBackAsTheSameGuardrail(GuardrailSeverity blockFrom, bool promptShields, bool indirectAttacks,
        bool protectedMaterial, string? blocklist)
    {
        var guardrail = new GuardrailPolicy("briefing-guardrail", blockFrom, promptShields, indirectAttacks, protectedMaterial,
            blocklist is null ? [] : [blocklist]);

        var properties = ArmGuardrailStore.ToProperties(guardrail);
        var read = ArmGuardrailStore.ToGuardrail("briefing-guardrail", properties);

        Assert.Equal(guardrail with { Blocklists = read.Blocklists }, read);
        Assert.Equal(guardrail.Blocklists, read.Blocklists);
        Assert.Equal(8, properties.ContentFilters.Count(filter => filter.SeverityThreshold is not null));   // 4 harms, on prompts and answers
        Assert.Equal(guardrail.Blocklists.Count * 2, properties.CustomBlocklists.Count);   // each blocklist, on prompts and answers
    }

    [Fact]
    public void AHarmFilterTurnedOffInThePortal_MeansTheGuardrailBlocksNothing()
    {
        var properties = ArmGuardrailStore.ToProperties(new GuardrailPolicy("g", GuardrailSeverity.Medium, true, true, true, []));
        properties.ContentFilters.First(filter => filter.Name == "Violence" && filter.Source == RaiPolicyContentSource.Completion).IsBlocking = false;

        var read = ArmGuardrailStore.ToGuardrail("g", properties);

        Assert.Null(read.BlockFrom);
        Assert.Contains("harm filters block from nothing, not Medium", read.WeakerThan(GuardrailPolicy.From(Settings())));
    }

    [Fact]
    public async Task OnAgents_EachVersionInUseMustCarryTheGuardrail_AndDeploymentsArentLookedUp()
    {
        var agents = Substitute.For<IAgentFactory>();
        agents.GetGuardrailAsync("rsd-trust-agent", "4", Arg.Any<CancellationToken>()).Returns((string?)null);
        agents.GetGuardrailAsync("ofsted-agent", null, Arg.Any<CancellationToken>()).Returns("briefing-guardrail");
        agents.GetGuardrailAsync("academies-agent", "2", Arg.Any<CancellationToken>()).Returns("Microsoft.DefaultV2");
        agents.GetGuardrailAsync("old-agent", "9", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new InvalidOperationException("Agent 'old-agent' version '9' was not found.")));
        var settings = Settings();
        settings.Targets = GuardrailTargets.Agents;   // its Deployments aren't in the store: looking them up would report them
        settings.Agents = [new("rsd-trust-agent", "4", "briefing-guardrail"), new("ofsted-agent", null, "briefing-guardrail"),
            new("academies-agent", "2", "briefing-guardrail"), new("old-agent", "9", "briefing-guardrail")];

        var report = await new FoundryGuardrailsService(_store, settings, NullLogger<FoundryGuardrailsService>.Instance, agents)
            .ApplyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["agent 'rsd-trust-agent' version 4 uses no guardrail", "agent 'academies-agent' version 2 uses 'Microsoft.DefaultV2'",
            "agent 'old-agent' version 9 doesn't exist"], report.Problems);
        Assert.Empty(_store.Deployments);
    }

    [Fact]
    public void OnAgents_TheGuardrailIsEveryNewVersionsDefault_AndPinnedAndExternalVersionsAreChecked()
    {
        var guardrails = ValidGuardrail();
        guardrails.Remove("Deployments:0");
        guardrails["Targets"] = "Agents";

        var services = Build(guardrails, extra: new()
        {
            ["AiAgents:VersionPins:rsd-trust-agent"] = "4",
            ["AiAgents:ExternallyManagedAgents:ofsted-agent"] = "latest",
        });

        var settings = (GuardrailSettings)services.Single(service => service.ServiceType == typeof(GuardrailSettings)).ImplementationInstance!;
        Assert.Equal([new AgentInUse("rsd-trust-agent", "4", "briefing-guardrail"), new AgentInUse("ofsted-agent", null, "briefing-guardrail")],
            settings.Agents);
    }

    [Fact]
    public void OnDeployments_DeploymentsAreStillNeeded()
    {
        var guardrails = ValidGuardrail();
        guardrails.Remove("Deployments:0");

        var ex = Assert.Throws<InvalidOperationException>(() => Build(guardrails));

        Assert.Contains("AiAgents:Guardrails:Deployments", ex.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> ValidGuardrail() => new()
    {
        ["SubscriptionId"] = Subscription, ["ResourceGroup"] = "rg-1", ["Name"] = "briefing-guardrail", ["Deployments:0"] = "gpt-5.1",
    };

    /// <summary>Registers with these <c>AiAgents:Guardrails</c> settings; by default every service signs in with one code credential.</summary>
    /// <param name="extra">Other <c>AiAgents</c> settings, by full path.</param>
    private static IServiceCollection Build(Dictionary<string, string?> guardrails, Action<AgentsBuilder>? configure = null,
        Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>(extra ?? [])
        {
            ["AiAgents:Foundry:Endpoint"] = FoundryEndpoint,
            ["AiAgents:Foundry:DefaultModel"] = "myconnection/gpt-5.1",
        };
        foreach (var (key, value) in guardrails)
        {
            settings[$"AiAgents:Guardrails:{key}"] = value;
        }

        return new ServiceCollection().AddAgents(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            agents => (configure ?? (builder => builder.UseCredential(Substitute.For<TokenCredential>())))(agents.AddGuardrails()));
    }

    [Theory]
    [InlineData(new string[] { }, new string[] { }, "needs at least one term or pattern")]
    [InlineData(new[] { "Project Falcon", " " }, new string[] { }, "has an empty term or pattern")]
    [InlineData(new string[] { }, new[] { "CASE-(" }, "has a pattern that isn't a valid regular expression")]
    public void AnInvalidBlocklist_IsReported_WithWhatsWrong(string[] terms, string[] patterns, string expected)
    {
        var settings = new GuardrailSettings { SubscriptionId = Subscription, ResourceGroup = "rg-1", Name = "briefing-guardrail", Deployments = ["gpt-5.1"] };
        settings.Blocklists["case-references"] = new GuardrailBlocklistSettings { Terms = [.. terms], Patterns = [.. patterns] };

        Assert.Equal([$"Blocklists:case-references ({expected})"], settings.Problems(FoundryEndpoint));
    }

    [Theory]
    [InlineData(null, "https://foundry-1.services.ai.azure.com/api/projects/test", "foundry-1")]
    [InlineData("foundry-models", "https://foundry-1.services.ai.azure.com/api/projects/test", "foundry-models")]   // deployments on another resource
    public void TheFoundryResource_IsTheOneInFoundryEndpoint_UnlessAccountNameIsSet(string? accountName, string foundryEndpoint, string expected)
    {
        var settings = new GuardrailSettings { SubscriptionId = Subscription, ResourceGroup = "rg-1", AccountName = accountName };

        settings.ResolveResourceId(foundryEndpoint);

        Assert.Equal($"/subscriptions/{Subscription}/resourceGroups/rg-1/providers/Microsoft.CognitiveServices/accounts/{expected}", settings.ResourceId);
    }

    [Fact]
    public void AnEndpointThatDoesntNameTheResource_AsksForAccountName()
    {
        var settings = new GuardrailSettings { SubscriptionId = Subscription, ResourceGroup = "rg-1", Name = "g", Deployments = ["gpt-5.1"] };

        Assert.Equal(["AccountName (Foundry:Endpoint doesn't name the resource, so set it here)"], settings.Problems("https://localhost:5001/project"));
    }

    [Fact]
    public void AddGuardrails_CalledTwice_RegistersTheCheckOnce()
    {
        var services = Build(ValidGuardrail(), agents => agents.AddGuardrails().UseCredential(Substitute.For<TokenCredential>()));

        Assert.Single(services, service => service.ImplementationType == typeof(GuardrailStartupValidator));
    }

    [Fact]
    public void AddGuardrails_WithoutAGuardrailsSection_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build([]));

        Assert.Contains("AiAgents:Guardrails (agents.AddGuardrails() needs this section)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourceManagerSignIn_UsesTheGuardrailsCredential_OrFailsStartupWhenItsBlockIsIncomplete()
    {
        var guardrails = ValidGuardrail();
        guardrails["Authentication:TenantId"] = "tenant-1";   // no ClientId or ClientSecret
        Action<AgentsBuilder> foundryOnly = agents => agents.UseCredentialFor(AzureCredentialTarget.Foundry, Substitute.For<TokenCredential>());

        var ex = Assert.Throws<InvalidOperationException>(() => Build(guardrails, foundryOnly));
        var withCode = Build(guardrails, agents => foundryOnly(agents.UseGuardrailsCredential(Substitute.For<TokenCredential>())));

        Assert.Contains("AiAgents:Guardrails:Authentication:ClientSecret", ex.Message, StringComparison.Ordinal);
        Assert.Contains(withCode, service => service.ServiceType == typeof(IFoundryGuardrailsService));
    }

    [Fact]
    public void AddGuardrails_RegistersTheCheck_AndInvalidSettingsFailStartupInOneError()
    {
        var valid = Build(ValidGuardrail());
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["SubscriptionId"] = "sub-1",
            ["Blocklists:case refs:Terms:0"] = "Project Falcon",
            ["Blocklists:case-references:Patterns:0"] = "CASE-(",
        }));

        Assert.Contains(valid, service => service.ServiceType == typeof(IFoundryGuardrailsService));
        Assert.Contains(valid, service => service.ImplementationType == typeof(GuardrailStartupValidator));
        foreach (var problem in new[]
                 {
                     "AiAgents:Guardrails:SubscriptionId (the subscription's ID, a GUID)", "AiAgents:Guardrails:ResourceGroup",
                     "AiAgents:Guardrails:Name", "AiAgents:Guardrails:Deployments",
                     "AiAgents:Guardrails:Blocklists:case refs (a name of letters, digits",
                     "AiAgents:Guardrails:Blocklists:case-references (has a pattern that isn't a valid regular expression)",
                 })
        {
            Assert.Contains(problem, ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Guardrails and deployments in memory, as Resource Manager would hold them.</summary>
    private sealed class InMemoryGuardrailStore : IGuardrailStore
    {
        public Dictionary<string, GuardrailPolicy> Guardrails { get; } = [];

        /// <summary>Deployment name to the guardrail it carries.</summary>
        public Dictionary<string, string?> Deployments { get; } = [];

        public Dictionary<string, HashSet<GuardrailBlocklistEntry>> Blocklists { get; } = [];

        /// <summary>When set, every call fails with it.</summary>
        public Exception? Failure { get; set; }

        /// <summary>When set, reads work but writes are refused, as for an identity with only Reader.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>How many writes were made.</summary>
        public int Writes { get; private set; }

        public Task<IReadOnlySet<GuardrailBlocklistEntry>?> GetBlocklistAsync(string name, CancellationToken cancellationToken)
            => Run<IReadOnlySet<GuardrailBlocklistEntry>?>(() => Blocklists.TryGetValue(name, out var entries) ? entries.ToHashSet() : null);

        public Task SaveBlocklistAsync(string name, IReadOnlySet<GuardrailBlocklistEntry> entries, CancellationToken cancellationToken)
            => Write(() => Blocklists[name] = [.. entries]);

        public Task<GuardrailPolicy?> GetGuardrailAsync(string name, CancellationToken cancellationToken)
            => Run(() => Guardrails.GetValueOrDefault(name));

        public Task SaveGuardrailAsync(GuardrailPolicy guardrail, CancellationToken cancellationToken)
            => Write(() => Guardrails[guardrail.Name] = guardrail);

        public Task<(bool Exists, string? Guardrail)> GetDeploymentAsync(string deployment, CancellationToken cancellationToken)
            => Run(() => Deployments.TryGetValue(deployment, out var guardrail) ? (true, guardrail) : (false, null));

        public Task AssignAsync(string deployment, string guardrail, CancellationToken cancellationToken)
            => Write(() => Deployments[deployment] = guardrail);

        private Task<T> Run<T>(Func<T> action) => Failure is { } failure ? Task.FromException<T>(failure) : Task.FromResult(action());

        private Task<T> Write<T>(Func<T> action)
        {
            if (ReadOnly)
            {
                return Task.FromException<T>(new RequestFailedException(403, "Forbidden"));
            }

            Writes++;
            return Run(action);
        }
    }
}

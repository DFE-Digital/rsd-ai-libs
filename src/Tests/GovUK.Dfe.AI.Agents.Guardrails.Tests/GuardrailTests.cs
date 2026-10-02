using Azure.Core;
using Azure.ResourceManager.CognitiveServices.Models;
using Azure;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Enums;
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

    private static Dictionary<string, string?> ValidGuardrail() => new()
    {
        ["SubscriptionId"] = Subscription, ["ResourceGroup"] = "rg-1", ["Name"] = "briefing-guardrail", ["Deployments:0"] = "gpt-5.1",
    };

    /// <summary>Registers with these <c>AiAgents:Guardrails</c> settings; by default every service signs in with one code credential.</summary>
    private static IServiceCollection Build(Dictionary<string, string?> guardrails, Action<AgentsBuilder>? configure = null)
    {
        var settings = new Dictionary<string, string?>
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

        public Task<IReadOnlySet<GuardrailBlocklistEntry>?> GetBlocklistAsync(string name, CancellationToken cancellationToken)
            => Run<IReadOnlySet<GuardrailBlocklistEntry>?>(() => Blocklists.TryGetValue(name, out var entries) ? entries.ToHashSet() : null);

        public Task SaveBlocklistAsync(string name, IReadOnlySet<GuardrailBlocklistEntry> entries, CancellationToken cancellationToken)
            => Run(() => Blocklists[name] = [.. entries]);

        public Task<GuardrailPolicy?> GetGuardrailAsync(string name, CancellationToken cancellationToken)
            => Run(() => Guardrails.GetValueOrDefault(name));

        public Task SaveGuardrailAsync(GuardrailPolicy guardrail, CancellationToken cancellationToken)
            => Run(() => Guardrails[guardrail.Name] = guardrail);

        public Task<(bool Exists, string? Guardrail)> GetDeploymentAsync(string deployment, CancellationToken cancellationToken)
            => Run(() => Deployments.TryGetValue(deployment, out var guardrail) ? (true, guardrail) : (false, null));

        public Task AssignAsync(string deployment, string guardrail, CancellationToken cancellationToken)
            => Run(() => Deployments[deployment] = guardrail);

        private Task<T> Run<T>(Func<T> action) => Failure is { } failure ? Task.FromException<T>(failure) : Task.FromResult(action());
    }
}

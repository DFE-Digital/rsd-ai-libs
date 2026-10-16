using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Mocking;
using Azure.ResourceManager.CognitiveServices.Models;
using GovUK.Dfe.AI.Agents.Guardrails.Enums;
using GovUK.Dfe.AI.Agents.Guardrails.Policies;
using GovUK.Dfe.AI.Agents.Guardrails.Stores;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Guardrails.Tests;

/// <summary>
/// The Resource Manager side of guardrails, over substitutes of the Azure SDK's own clients: what's read back, what's
/// written, and that re-applying a blocklist changes only the entries that differ.
/// </summary>
public sealed class ArmGuardrailStoreTests
{
    private static readonly ResourceIdentifier AccountId =
        new("/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.CognitiveServices/accounts/foundry-1");

    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private readonly RaiPolicyCollection _policies = Substitute.For<RaiPolicyCollection>();
    private readonly CognitiveServicesAccountDeploymentCollection _deployments = Substitute.For<CognitiveServicesAccountDeploymentCollection>();
    private readonly RaiBlocklistCollection _blocklists = Substitute.For<RaiBlocklistCollection>();
    private readonly ArmGuardrailStore _store;

    public ArmGuardrailStoreTests()
    {
        var account = Substitute.For<CognitiveServicesAccountResource>();
        account.GetRaiPolicies().Returns(_policies);
        account.GetCognitiveServicesAccountDeployments().Returns(_deployments);
        account.GetRaiBlocklists().Returns(_blocklists);

        var mockable = Substitute.For<MockableCognitiveServicesArmClient>();
        mockable.GetCognitiveServicesAccountResource(AccountId).Returns(account);
        var arm = Substitute.For<ArmClient>();
        arm.GetCachedClient(Arg.Any<Func<ArmClient, MockableCognitiveServicesArmClient>>()).Returns(mockable);

        _store = new ArmGuardrailStore(arm, AccountId);
    }

    [Fact]
    public async Task Guardrail_Missing_IsNull_AndOneThatExists_ReadsBackAsWritten()
    {
        var guardrail = new GuardrailPolicy("briefing-guardrail", GuardrailSeverity.Low, PromptShields: true, IndirectAttacks: true,
            ProtectedMaterial: false, Blocklists: ["case-references"]);
        _policies.GetIfExistsAsync("missing", cancellationToken).Returns(NotFound<RaiPolicyResource>());
        var policy = Substitute.For<RaiPolicyResource>();
        policy.Data.Returns(ArmCognitiveServicesModelFactory.RaiPolicyData(properties: ArmGuardrailStore.ToProperties(guardrail)));
        _policies.GetIfExistsAsync("briefing-guardrail", cancellationToken).Returns(Found(policy));

        Assert.Null(await _store.GetGuardrailAsync("missing", cancellationToken));
        Assert.Equal(guardrail, await _store.GetGuardrailAsync("briefing-guardrail", cancellationToken), GuardrailComparer.Instance);
    }

    [Fact]
    public async Task SaveGuardrail_WritesTheRaiPolicyOnTopOfTheDefaultPolicy()
    {
        var guardrail = new GuardrailPolicy("briefing-guardrail", GuardrailSeverity.Medium, true, true, true, []);

        await _store.SaveGuardrailAsync(guardrail, cancellationToken);

        await _policies.Received(1).CreateOrUpdateAsync(WaitUntil.Completed, "briefing-guardrail",
            Arg.Is<RaiPolicyData>(data => data.Properties.BasePolicyName == "Microsoft.DefaultV2"
                && data.Properties.ContentFilters.Count(filter => filter.IsBlocking == true) == 12),   // 4 harms both ways, jailbreak, indirect, 2 protected
            cancellationToken);
    }

    [Fact]
    public async Task Deployment_ReportsWhetherItExists_AndItsGuardrail()
    {
        _deployments.GetIfExistsAsync("missing", cancellationToken).Returns(NotFound<CognitiveServicesAccountDeploymentResource>());
        var deployment = Deployment("Microsoft.DefaultV2");
        _deployments.GetIfExistsAsync("gpt-5.1", cancellationToken).Returns(Found(deployment));

        Assert.Equal((false, null), await _store.GetDeploymentAsync("missing", cancellationToken));
        Assert.Equal((true, "Microsoft.DefaultV2"), await _store.GetDeploymentAsync("gpt-5.1", cancellationToken));
    }

    [Fact]
    public async Task Assign_ChangesOnlyTheDeploymentsGuardrail_KeepingItsModel()
    {
        var deployment = Deployment("Microsoft.DefaultV2");
        _deployments.GetAsync("gpt-5.1", cancellationToken).Returns(Response.FromValue(deployment, Substitute.For<Response>()));

        await _store.AssignAsync("gpt-5.1", "briefing-guardrail", cancellationToken);

        await _deployments.Received(1).CreateOrUpdateAsync(WaitUntil.Completed, "gpt-5.1",
            Arg.Is<CognitiveServicesAccountDeploymentData>(data => data.Properties.RaiPolicyName == "briefing-guardrail"
                && data.Properties.Model.Name == "gpt-5.1"),
            cancellationToken);
    }

    [Fact]
    public async Task Blocklist_Missing_IsNull_AndEntriesReadBackWithTheirKind()
    {
        _blocklists.GetIfExistsAsync("missing", cancellationToken).Returns(NotFound<RaiBlocklistResource>());
        var blocklist = Blocklist(Item("Project Falcon", isRegex: false), Item(@"CASE-\d{6}", isRegex: true), Item(pattern: null, isRegex: false));
        _blocklists.GetIfExistsAsync("case-references", cancellationToken).Returns(Found(blocklist));

        Assert.Null(await _store.GetBlocklistAsync("missing", cancellationToken));
        Assert.Equal(
            [new GuardrailBlocklistEntry("Project Falcon", IsRegex: false), new GuardrailBlocklistEntry(@"CASE-\d{6}", IsRegex: true)],
            await _store.GetBlocklistAsync("case-references", cancellationToken));
    }

    [Fact]
    public async Task SaveBlocklist_DeletesOnlyRemovedEntries_AndAddsOnlyNewOnes()
    {
        var kept = Item("Project Falcon", isRegex: false);
        var removed = Item("Project Heron", isRegex: false);
        var blocklist = Blocklist(kept, removed);
        var operation = Substitute.For<ArmOperation<RaiBlocklistResource>>();
        operation.Value.Returns(blocklist);
        _blocklists.CreateOrUpdateAsync(WaitUntil.Completed, "case-references", Arg.Any<RaiBlocklistData>(), cancellationToken)
            .Returns(operation);
        var added = new GuardrailBlocklistEntry(@"CASE-\d{6}", IsRegex: true);

        await _store.SaveBlocklistAsync("case-references",
            new HashSet<GuardrailBlocklistEntry> { new("Project Falcon", IsRegex: false), added }, cancellationToken);

        await removed.Received(1).DeleteAsync(WaitUntil.Completed, cancellationToken);
        await kept.DidNotReceiveWithAnyArgs().DeleteAsync(default, cancellationToken);
        var items = blocklist.GetRaiBlocklistItems();
        await items.Received(1).CreateOrUpdateAsync(WaitUntil.Completed, ArmGuardrailStore.ItemName(added),
            Arg.Is<RaiBlocklistItemData>(data => data.Properties.Pattern == added.Pattern && data.Properties.IsRegex == true), cancellationToken);
        await items.ReceivedWithAnyArgs(1).CreateOrUpdateAsync(default, default!, default!, cancellationToken);
    }

    private static CognitiveServicesAccountDeploymentResource Deployment(string guardrail)
    {
        var deployment = Substitute.For<CognitiveServicesAccountDeploymentResource>();
        deployment.Data.Returns(ArmCognitiveServicesModelFactory.CognitiveServicesAccountDeploymentData(name: "gpt-5.1",
            properties: new CognitiveServicesAccountDeploymentProperties
            {
                Model = new CognitiveServicesAccountDeploymentModel { Name = "gpt-5.1", Format = "OpenAI" },
                RaiPolicyName = guardrail,
            }));
        return deployment;
    }

    private static RaiBlocklistItemResource Item(string? pattern, bool isRegex)
    {
        var name = pattern is null ? "item-empty" : ArmGuardrailStore.ItemName(new GuardrailBlocklistEntry(pattern, isRegex));
        var item = Substitute.For<RaiBlocklistItemResource>();
        item.Data.Returns(ArmCognitiveServicesModelFactory.RaiBlocklistItemData(name: name,
            properties: pattern is null ? null : new RaiBlocklistItemProperties { Pattern = pattern, IsRegex = isRegex }));
        return item;
    }

    private static RaiBlocklistResource Blocklist(params RaiBlocklistItemResource[] existing)
    {
        var items = Substitute.For<RaiBlocklistItemCollection>();
        items.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(AsyncPageable<RaiBlocklistItemResource>.FromPages([Page<RaiBlocklistItemResource>.FromValues(existing, null, Substitute.For<Response>())]));
        var blocklist = Substitute.For<RaiBlocklistResource>();
        blocklist.GetRaiBlocklistItems().Returns(items);
        return blocklist;
    }

    private static NullableResponse<T> Found<T>(T value) => Response.FromValue(value, Substitute.For<Response>());

    private static NullableResponse<T> NotFound<T>() => new Missing<T>();

    private sealed class Missing<T> : NullableResponse<T>
    {
        public override bool HasValue => false;

        public override T? Value => throw new InvalidOperationException("No value.");

        public override Response GetRawResponse() => Substitute.For<Response>();
    }

    /// <summary>Guardrails compare by value, blocklists included.</summary>
    private sealed class GuardrailComparer : IEqualityComparer<GuardrailPolicy?>
    {
        public static readonly GuardrailComparer Instance = new();

        public bool Equals(GuardrailPolicy? x, GuardrailPolicy? y)
            => x is not null && y is not null && (x with { Blocklists = [] }) == (y with { Blocklists = [] })
               && x.Blocklists.SequenceEqual(y.Blocklists);

        public int GetHashCode(GuardrailPolicy? obj) => 0;
    }
}

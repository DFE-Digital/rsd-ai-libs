using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Models;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Guardrails.Policies;
using GovUK.Dfe.AI.Agents.Guardrails.Stores.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace GovUK.Dfe.AI.Agents.Guardrails.Stores;

/// <summary>Guardrails (RAI policies) and deployments on one Foundry resource, through Azure Resource Manager.</summary>
internal sealed class ArmGuardrailStore(ArmClient arm, ResourceIdentifier accountId) : IGuardrailStore
{
    private static readonly string[] HarmFilters = ["Hate", "Sexual", "Selfharm", "Violence"];
    private const string Jailbreak = "Jailbreak";
    private const string IndirectAttack = "Indirect Attack";
    private static readonly string[] ProtectedMaterialFilters = ["Protected Material Text", "Protected Material Code"];

    private static readonly RaiPolicyContentSource[] BothWays = [RaiPolicyContentSource.Prompt, RaiPolicyContentSource.Completion];

    private CognitiveServicesAccountResource Account => arm.GetCognitiveServicesAccountResource(accountId);

    public async Task<GuardrailPolicy?> GetGuardrailAsync(string name, CancellationToken cancellationToken)
    {
        var found = await Account.GetRaiPolicies().GetIfExistsAsync(name, cancellationToken).ConfigureAwait(false);
        return found.HasValue ? ToGuardrail(name, found.Value!.Data.Properties) : null;
    }

    public async Task SaveGuardrailAsync(GuardrailPolicy guardrail, CancellationToken cancellationToken)
        => await Account.GetRaiPolicies()
            .CreateOrUpdateAsync(WaitUntil.Completed, guardrail.Name, new RaiPolicyData { Properties = ToProperties(guardrail) }, cancellationToken)
            .ConfigureAwait(false);

    public async Task<(bool Exists, string? Guardrail)> GetDeploymentAsync(string deployment, CancellationToken cancellationToken)
    {
        var found = await Account.GetCognitiveServicesAccountDeployments().GetIfExistsAsync(deployment, cancellationToken).ConfigureAwait(false);
        return found.HasValue ? (true, found.Value!.Data.Properties?.RaiPolicyName) : (false, null);
    }

    public async Task AssignAsync(string deployment, string guardrail, CancellationToken cancellationToken)
    {
        var deployments = Account.GetCognitiveServicesAccountDeployments();
        var data = (await deployments.GetAsync(deployment, cancellationToken).ConfigureAwait(false)).Value.Data;
        data.Properties.RaiPolicyName = guardrail;
        await deployments.CreateOrUpdateAsync(WaitUntil.Completed, deployment, data, cancellationToken).ConfigureAwait(false);
    }

    internal static RaiPolicyProperties ToProperties(GuardrailPolicy guardrail)
    {
        var properties = new RaiPolicyProperties { BasePolicyName = "Microsoft.DefaultV2", Mode = RaiPolicyMode.Default };
        var threshold = ToLevel(guardrail.BlockFrom ?? GuardrailSeverity.Medium);

        foreach (var harm in HarmFilters)
        {
            properties.ContentFilters.Add(Filter(harm, RaiPolicyContentSource.Prompt, on: true, threshold));
            properties.ContentFilters.Add(Filter(harm, RaiPolicyContentSource.Completion, on: true, threshold));
        }

        properties.ContentFilters.Add(Filter(Jailbreak, RaiPolicyContentSource.Prompt, guardrail.PromptShields));
        properties.ContentFilters.Add(Filter(IndirectAttack, RaiPolicyContentSource.Prompt, guardrail.IndirectAttacks));
        foreach (var name in ProtectedMaterialFilters)
        {
            properties.ContentFilters.Add(Filter(name, RaiPolicyContentSource.Completion, guardrail.ProtectedMaterial));
        }

        foreach (var blocklist in guardrail.Blocklists)
        {
            foreach (var source in BothWays)
            {
                properties.CustomBlocklists.Add(new CustomBlocklistConfig { BlocklistName = blocklist, Source = source, IsBlocking = true });
            }
        }

        return properties;
    }

    private static RaiPolicyContentFilter Filter(string name, RaiPolicyContentSource source, bool on, RaiPolicyContentLevel? threshold = null)
        => new() { Name = name, Source = source, IsEnabled = on, IsBlocking = on, SeverityThreshold = threshold };

    internal static GuardrailPolicy ToGuardrail(string name, RaiPolicyProperties? properties)
    {
        var filters = properties?.ContentFilters ?? [];
        bool Blocks(RaiPolicyContentFilter filter) => filter.IsEnabled == true && filter.IsBlocking == true;
        bool BlocksAll(IEnumerable<string> names) => names.All(wanted => filters.Any(filter =>
            string.Equals(filter.Name, wanted, StringComparison.OrdinalIgnoreCase) && Blocks(filter)));

        // Every harm filter, on prompts and answers, must block; the weakest threshold among them is what the guardrail blocks from.
        var harm = filters.Where(filter => HarmFilters.Contains(filter.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        GuardrailSeverity? blockFrom = harm.Count == HarmFilters.Length * 2 && harm.TrueForAll(Blocks)
            ? harm.Max(filter => ToSeverity(filter.SeverityThreshold))
            : null;

        // A blocklist counts only when it blocks both prompts and answers.
        var blocklists = (properties?.CustomBlocklists ?? []).Where(static blocklist => blocklist.IsBlocking == true)
            .GroupBy(static blocklist => blocklist.BlocklistName, StringComparer.Ordinal)
            .Where(static group => BothWays.All(source => group.Any(blocklist => blocklist.Source == source)))
            .Select(static group => group.Key).Order(StringComparer.Ordinal).ToList();

        return new GuardrailPolicy(name, blockFrom, BlocksAll([Jailbreak]), BlocksAll([IndirectAttack]), BlocksAll(ProtectedMaterialFilters),
            blocklists);
    }

    public async Task<IReadOnlySet<GuardrailBlocklistEntry>?> GetBlocklistAsync(string name, CancellationToken cancellationToken)
    {
        var found = await Account.GetRaiBlocklists().GetIfExistsAsync(name, cancellationToken).ConfigureAwait(false);
        if (!found.HasValue)
        {
            return null;
        }

        var entries = new HashSet<GuardrailBlocklistEntry>();
        await foreach (var item in found.Value!.GetRaiBlocklistItems().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (item.Data.Properties is { Pattern: { } pattern } properties)
            {
                entries.Add(new GuardrailBlocklistEntry(pattern, properties.IsRegex == true));
            }
        }

        return entries;
    }

    public async Task SaveBlocklistAsync(string name, IReadOnlySet<GuardrailBlocklistEntry> entries, CancellationToken cancellationToken)
    {
        var blocklist = (await Account.GetRaiBlocklists().CreateOrUpdateAsync(WaitUntil.Completed, name,
            new RaiBlocklistData { RaiBlocklistDescription = "Managed by GovUK.Dfe.AI.Agents.Guardrails" },
            cancellationToken).ConfigureAwait(false)).Value;

        // Items are named after their content, so re-applying adds only what's new and removes only what's gone.
        var wanted = entries.ToDictionary(ItemName);
        var items = blocklist.GetRaiBlocklistItems();
        await foreach (var item in items.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!wanted.Remove(item.Data.Name))
            {
                await item.DeleteAsync(WaitUntil.Completed, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var (itemName, entry) in wanted)
        {
            await items.CreateOrUpdateAsync(WaitUntil.Completed, itemName,
                new RaiBlocklistItemData { Properties = new RaiBlocklistItemProperties { Pattern = entry.Pattern, IsRegex = entry.IsRegex } },
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string ItemName(GuardrailBlocklistEntry entry)
        => "item-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{entry.IsRegex}:{entry.Pattern}")))[..24].ToLowerInvariant();

    private static RaiPolicyContentLevel ToLevel(GuardrailSeverity severity) => severity switch
    {
        GuardrailSeverity.Low => RaiPolicyContentLevel.Low,
        GuardrailSeverity.High => RaiPolicyContentLevel.High,
        _ => RaiPolicyContentLevel.Medium,
    };

    private static GuardrailSeverity ToSeverity(RaiPolicyContentLevel? level)
    {
        if (level == RaiPolicyContentLevel.Low)
        {
            return GuardrailSeverity.Low;
        }

        return level == RaiPolicyContentLevel.Medium ? GuardrailSeverity.Medium : GuardrailSeverity.High;
    }
}

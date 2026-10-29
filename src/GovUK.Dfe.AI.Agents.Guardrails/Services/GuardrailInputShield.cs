using System.Text.RegularExpressions;
using GovUK.Dfe.AI.Agents.Exceptions;
using GovUK.Dfe.AI.Agents.Guardrails.Constants;
using GovUK.Dfe.AI.Agents.Guardrails.Options;
using GovUK.Dfe.AI.Agents.Safety.Interfaces;

namespace GovUK.Dfe.AI.Agents.Guardrails.Services;

/// <summary>
/// The guardrail's controls the app runs itself, before anything is sent to the model: the blocklists, checked locally
/// (<see cref="GuardrailSettings.CheckBlocklists"/>), then Prompt Shields on the question
/// (<see cref="GuardrailSettings.ScanPrompts"/>) and the evidence (<see cref="GuardrailSettings.ScanEvidence"/>). A
/// Foundry guardrail doesn't run at all for a model behind your own gateway, and isn't relied on to scan evidence for any
/// agent. Tool output isn't checked here.
/// </summary>
internal sealed class GuardrailInputShield : IInputShield
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<(string Name, Regex Pattern)> _blocklists;
    private readonly PromptShieldsClient? _promptShields;
    private readonly bool _scanPrompts;
    private readonly bool _scanEvidence;

    public GuardrailInputShield(GuardrailSettings settings, PromptShieldsClient? promptShields)
    {
        _blocklists = settings.CheckBlocklists ? [.. settings.Blocklists.Select(static entry => (entry.Key, Matcher(entry.Value)))] : [];
        _promptShields = promptShields;
        _scanPrompts = settings.ScanPrompts && promptShields is not null;
        _scanEvidence = settings.ScanEvidence && promptShields is not null;
    }

    public async ValueTask<InputBlock?> CheckAsync(string prompt, string? evidence, CancellationToken cancellationToken)
    {
        // Local and free, so first: a match never reaches Azure.
        if (OnBlocklist(prompt) is { } questionList)
        {
            return new InputBlock(AgentGuardrailException.PromptStage, string.Format(BlockReasons.QuestionOnBlocklist, questionList));
        }

        if (evidence is not null && OnBlocklist(evidence) is { } evidenceList)
        {
            return new InputBlock(AgentGuardrailException.EvidenceStage, string.Format(BlockReasons.EvidenceOnBlocklist, evidenceList));
        }

        // Side by side: both are calls to Prompt Shields.
        var promptScan = _scanPrompts ? _promptShields!.PromptIsAttackAsync(prompt, cancellationToken) : Task.FromResult(false);
        var evidenceScan = _scanEvidence && evidence is not null
            ? _promptShields!.EvidenceHasAttackAsync(prompt, evidence, cancellationToken)
            : Task.FromResult(false);
        await Task.WhenAll(promptScan, evidenceScan).ConfigureAwait(false);

        if (await promptScan.ConfigureAwait(false))
        {
            return new InputBlock(AgentGuardrailException.PromptStage, BlockReasons.PromptAttack);
        }

        return await evidenceScan.ConfigureAwait(false)
            ? new InputBlock(AgentGuardrailException.EvidenceStage, BlockReasons.EvidenceAttack)
            : null;
    }

    /// <summary>The first blocklist the text matches, or null.</summary>
    private string? OnBlocklist(string text)
        => _blocklists.FirstOrDefault(list => list.Pattern.IsMatch(text)) is { Name: { } name } ? name : null;

    /// <summary>One expression for a blocklist: its terms as written, ignoring case, and its patterns as given.</summary>
    private static Regex Matcher(GuardrailBlocklistSettings blocklist)
    {
        var parts = blocklist.Terms.Select(static term => $"(?i:{Regex.Escape(term)})")
            .Concat(blocklist.Patterns.Select(static pattern => $"(?:{pattern})"));
        return new Regex(string.Join('|', parts), RegexOptions.CultureInvariant, MatchTimeout);
    }
}

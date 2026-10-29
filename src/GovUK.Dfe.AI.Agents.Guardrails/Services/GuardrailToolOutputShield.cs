using GovUK.Dfe.AI.Agents.Exceptions;
using GovUK.Dfe.AI.Agents.Guardrails.Constants;
using GovUK.Dfe.AI.Agents.Guardrails.Options;
using GovUK.Dfe.AI.Agents.Safety.Interfaces;

namespace GovUK.Dfe.AI.Agents.Guardrails.Services;

/// <summary>
/// Prompt Shields on the output of each tool this app runs (<see cref="GuardrailSettings.ScanToolOutput"/>), before the
/// model reads it. A Foundry guardrail scans tool responses only for Foundry's own tools, never these. Fails closed: if the
/// output can't be scanned, the run fails rather than sending it.
/// </summary>
internal sealed class GuardrailToolOutputShield(PromptShieldsClient promptShields) : IToolOutputShield
{
    public async ValueTask<InputBlock?> CheckAsync(string toolName, string output, CancellationToken cancellationToken)
        => !string.IsNullOrWhiteSpace(output) && await promptShields.ToolOutputHasAttackAsync(toolName, output, cancellationToken).ConfigureAwait(false)
            ? new InputBlock(AgentGuardrailException.ToolOutputStage, string.Format(BlockReasons.ToolOutputAttack, toolName))
            : null;
}

namespace GovUK.Dfe.AI.Agents.Guardrails.Constants;

/// <summary>This package's error messages.</summary>
internal static class ErrorMessages
{
    internal const string GuardrailAccessDenied = "This app's identity needs {0} on the Foundry resource {1} to use its guardrails.";
    internal const string GuardrailUncheckable = "Couldn't check the Foundry guardrail '{0}' at startup, and FailWhenUnreachable is on. See the inner exception.";
    internal const string EvidenceShieldFailed = "Prompt Shields couldn't scan the evidence ({0}), so it wasn't sent: {1}. The app's identity needs Cognitive Services User on the Foundry resource.";
    internal const string EvidenceShieldNoResult = "Prompt Shields returned no result for the evidence, so it wasn't sent.";
    internal const string GuardrailNotApplied = "The Foundry guardrail '{0}' isn't in place: {1}. Run IFoundryGuardrailsService.ApplyAsync from your provisioning job.";
}

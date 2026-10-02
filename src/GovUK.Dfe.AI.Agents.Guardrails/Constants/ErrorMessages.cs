namespace GovUK.Dfe.AI.Agents.Guardrails.Constants;

/// <summary>This package's error messages.</summary>
internal static class ErrorMessages
{
    internal const string GuardrailAccessDenied = "This app's identity needs {0} on the Foundry resource {1} to use its guardrails.";
    internal const string GuardrailNotApplied = "The Foundry guardrail '{0}' isn't in place: {1}. Run IFoundryGuardrailsService.ApplyAsync from your provisioning job.";
}

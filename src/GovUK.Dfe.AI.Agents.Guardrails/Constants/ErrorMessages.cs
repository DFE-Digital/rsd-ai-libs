namespace GovUK.Dfe.AI.Agents.Guardrails.Constants;

/// <summary>This package's error messages.</summary>
internal static class ErrorMessages
{
    internal const string GuardrailAccessDenied = "This app's identity needs {0} on the Foundry resource {1} to use its guardrails.";
    internal const string GuardrailUncheckable = "Couldn't check the Foundry guardrail '{0}' at startup, and FailWhenUnreachable is on. See the inner exception.";
    internal const string PromptShieldsFailed = "Prompt Shields couldn't scan the input ({0}), so it wasn't sent: {1}. The app's identity needs Cognitive Services User on the Foundry resource.";
    internal const string PromptShieldsNoResult = "Prompt Shields returned no result for the input, so it wasn't sent.";
    internal const string GuardrailNotApplied = "The Foundry guardrail '{0}' isn't in place: {1}. Run IFoundryGuardrailsService.ApplyAsync from your provisioning job.";

    /// <summary>The context sent with a tool's output: Prompt Shields scans a document alongside a user prompt.</summary>
    internal const string ToolOutputContext = "Output of the tool {0}, called by the assistant.";
}

/// <summary>Why the input shield blocked a run, as <c>InputBlock.Reason</c>.</summary>
internal static class BlockReasons
{
    internal const string PromptAttack = "Prompt Shields found a prompt attack in the question";
    internal const string EvidenceAttack = "Prompt Shields found instructions planted in the evidence";
    internal const string ToolOutputAttack = "Prompt Shields found instructions planted in the output of the tool {0}";
    internal const string QuestionOnBlocklist = "The question matched the blocklist '{0}'";
    internal const string EvidenceOnBlocklist = "The evidence matched the blocklist '{0}'";
}

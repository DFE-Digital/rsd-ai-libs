namespace GovUK.Dfe.AI.Agents.Prompts.Interfaces;

/// <summary>System and user prompts, by prompt type.</summary>
public interface IPromptProvider
{
    /// <summary>The system prompt (instructions) for <paramref name="promptType"/>.</summary>
    string GetSystemPrompt(string promptType);

    /// <summary>The user prompt template for <paramref name="promptType"/>.</summary>
    string GetUserPrompt(string promptType);
}

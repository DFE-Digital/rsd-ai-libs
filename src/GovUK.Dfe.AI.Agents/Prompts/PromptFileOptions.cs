namespace GovUK.Dfe.AI.Agents.Prompts;

/// <summary>The <c>PromptFiles</c> section: prompt file paths, by prompt type.</summary>
internal sealed class PromptFileOptions
{
    public Dictionary<string, string> SystemPrompts { get; set; } = [];
    public Dictionary<string, string> UserPrompts { get; set; } = [];
}

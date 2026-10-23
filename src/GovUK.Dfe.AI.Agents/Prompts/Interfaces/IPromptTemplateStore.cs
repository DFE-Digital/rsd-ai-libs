namespace GovUK.Dfe.AI.Agents.Prompts.Interfaces;

/// <summary>Prompt templates, by key.</summary>
internal interface IPromptTemplateStore
{
    /// <summary>The template for <paramref name="key"/>.</summary>
    string GetTemplate(string key);
}

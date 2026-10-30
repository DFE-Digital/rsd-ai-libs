namespace GovUK.Dfe.AI.Agents.Prompts.Interfaces;

/// <summary>Builds prompts from templates.</summary>
public interface IPromptTemplateBuilder
{
    /// <summary>Fills the template for <paramref name="key"/> with <paramref name="values"/>.</summary>
    string Build(string key, IReadOnlyDictionary<string, string> values);
}

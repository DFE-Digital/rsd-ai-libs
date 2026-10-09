using GovUK.Dfe.AI.Agents.Prompts.Interfaces;

namespace GovUK.Dfe.AI.Agents.Prompts;

/// <summary>Prompt templates from a function you supply, e.g. a database or Key Vault.</summary>
internal sealed class DelegatingPromptTemplateStore(Func<string, string> getTemplate) : IPromptTemplateStore
{
    public string GetTemplate(string key) => getTemplate(key);
}

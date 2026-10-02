using GovUK.Dfe.AI.Agents.Prompts.Interfaces;

namespace GovUK.Dfe.AI.Agents.Prompts;

/// <summary>Fills <c>{{Name}}</c> placeholders in a user prompt template. Values aren't fenced: put untrusted text in evidence.</summary>
public sealed class PromptTemplateBuilder(IPromptTemplateStore templateStore) : IPromptTemplateBuilder
{
    public string Build(string key, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var prompt = templateStore.GetTemplate(key);

        foreach (var (name, value) in values)
        {
            prompt = prompt.Replace($"{{{{{name}}}}}", value ?? string.Empty, StringComparison.Ordinal);
        }

        return prompt;
    }
}

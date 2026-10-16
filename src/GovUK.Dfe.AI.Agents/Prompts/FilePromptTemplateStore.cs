using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.Prompts.Interfaces;

namespace GovUK.Dfe.AI.Agents.Prompts;

/// <summary>Prompt templates read from the files configured under <c>PromptFiles</c>, by key.</summary>
internal sealed class FilePromptTemplateStore(IReadOnlyDictionary<string, string> paths, Func<string, string> readFile) : IPromptTemplateStore
{
    public string GetTemplate(string key)
        => paths.TryGetValue(key, out var path)
            ? readFile(path)
            : throw new InvalidOperationException(string.Format(ErrorMessages.NoPromptFileConfigured, key));
}

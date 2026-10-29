using System.Text.RegularExpressions;
using GovUK.Dfe.AI.Agents.Prompts.Interfaces;

namespace GovUK.Dfe.AI.Agents.Prompts;

/// <summary>
/// Builds prompt text from stored templates by replacing named placeholders with provided values.
/// </summary>
/// <param name="templateStore">Provides access to prompt templates identified by key.</param>
internal sealed class PromptTemplateBuilder(IPromptTemplateStore templateStore) : IPromptTemplateBuilder
{
    /// <summary>Far longer than any prompt takes; a bound, so a huge template can't hold a thread.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public string Build(string key, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var template = templateStore.GetTemplate(key);

        if (values.Count == 0)
        {
            return template;
        }

        var names = values.Keys
            .OrderByDescending(name => name.Length)
            .Select(Regex.Escape);
        var placeholders = new Regex($@"\{{\{{({string.Join("|", names)})\}}\}}", RegexOptions.CultureInvariant, MatchTimeout);

        return placeholders.Replace(template, match => values[match.Groups[1].Value] ?? string.Empty);
    }
}

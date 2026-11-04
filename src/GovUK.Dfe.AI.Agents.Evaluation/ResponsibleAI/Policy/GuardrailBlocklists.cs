using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;

/// <summary>
/// The guardrail's blocklists (<c>AiAgents:Guardrails:Blocklists</c>), read from settings so the quality gate scans
/// answers for the same terms and patterns the guardrail blocks: one list for the team to keep. Needs only the settings,
/// not the Guardrails package.
/// </summary>
/// <param name="Patterns">Each entry as a regular expression, by a name that never shows the term, e.g. "pupil-ids term 2".</param>
internal sealed record GuardrailBlocklists(IReadOnlyDictionary<string, string> Patterns)
{
    private const string SectionName = "Guardrails:Blocklists";

    /// <param name="aiAgents">The <c>AiAgents</c> section.</param>
    public static GuardrailBlocklists From(IConfigurationSection aiAgents)
    {
        var patterns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var blocklist in aiAgents.GetSection(SectionName).GetChildren())
        {
            var terms = blocklist.GetSection("Terms").Get<string[]>() ?? [];
            for (var i = 0; i < terms.Length; i++)
            {
                patterns[$"{blocklist.Key} term {i + 1}"] = $"(?i){Regex.Escape(terms[i])}";
            }

            var expressions = blocklist.GetSection("Patterns").Get<string[]>() ?? [];
            for (var i = 0; i < expressions.Length; i++)
            {
                patterns[$"{blocklist.Key} pattern {i + 1}"] = expressions[i];
            }
        }

        return new GuardrailBlocklists(patterns);
    }
}

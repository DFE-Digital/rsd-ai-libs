using System.Text.RegularExpressions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Privacy.Interfaces;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>
/// Masks sensitive data in text the gate keeps or shows: each value a test case planted (<c>"sensitiveData"</c>),
/// anything on the guardrail's blocklists (<c>AiAgents:Guardrails:Blocklists</c>), and anything the app's redactors
/// remove. Used before anything is logged, reported, written or published.
/// </summary>
internal sealed class SensitiveDataMasker
{
    private const string PlantedMask = "[sensitive value removed]";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly List<(string Mask, Regex Pattern)> _blocklisted;
    private readonly List<IAgentInputRedactor> _redactors;

    public SensitiveDataMasker(GuardrailBlocklists blocklists, IEnumerable<IAgentInputRedactor> redactors)
    {
        _blocklisted = [.. blocklists.Patterns.Select(static entry =>
            ($"[{entry.Key} removed]", new Regex(entry.Value, RegexOptions.CultureInvariant, MatchTimeout)))];
        _redactors = [.. redactors];
    }

    public async ValueTask<string> MaskAsync(string text, IReadOnlyList<string> planted, CancellationToken cancellationToken)
    {
        foreach (var value in planted)
        {
            text = text.Replace(value, PlantedMask, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var (mask, pattern) in _blocklisted)
        {
            text = pattern.Replace(text, mask);
        }

        foreach (var redactor in _redactors)
        {
            text = await redactor.RedactAsync(text, cancellationToken);
        }

        return text;
    }
}

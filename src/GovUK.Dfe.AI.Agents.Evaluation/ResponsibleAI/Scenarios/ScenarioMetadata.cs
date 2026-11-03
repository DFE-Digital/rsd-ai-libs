using System.Text.Json;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Quality;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;

/// <summary>
/// What a test case says about itself for the quality gate, from its folder and its own fields: the principle and risk it
/// tests, how much a failure matters, and any privacy or transparency expectations. A case is filed under a principle by
/// its folder, e.g. <c>evaluation/Fairness/special-school-outcomes.json</c>, or its <c>"principle"</c> field.
/// </summary>
/// <param name="Principle">The principle it tests.</param>
/// <param name="Severity">How much a failure matters: <c>"severity"</c>, or critical for guardrail cases and high for others.</param>
/// <param name="Method">Adversarial for guardrail cases; deterministic for others, whose facts are checked in code.</param>
/// <param name="Risk">The risk it tests, from <c>"risk"</c>, e.g. "Reports a closed concern as ongoing".</param>
/// <param name="ExpectedOutcome">What should happen, from <c>"expectedOutcome"</c>, in words.</param>
public sealed record ScenarioMetadata(ResponsibleAiPrinciple Principle, CheckSeverity Severity, EvaluationMethod Method,
    string? Risk, string? ExpectedOutcome)
{
    public const string PrincipleField = "principle";
    public const string SeverityField = "severity";
    public const string RiskField = "risk";
    public const string ExpectedOutcomeField = "expectedOutcome";
    public const string SensitiveDataField = "sensitiveData";
    public const string ExpectsUncertaintyField = Options.ReleaseGateSettings.ExpectsUncertaintyField;

    /// <summary>Values planted in the case's evidence or prompt that must never appear in the answer, from <c>"sensitiveData"</c>.</summary>
    public IReadOnlyList<string> SensitiveData { get; init; } = [];

    /// <summary>The answer must say the agent doesn't know or has no evidence, from <c>"expectsUncertainty": true</c>.</summary>
    public bool ExpectsUncertainty { get; init; }

    public static ScenarioMetadata For(AgentTestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var guardrail = ReleaseSections.IsGuardrailCase(testCase.Name);
        var principle = TryParsePrinciple(Text(testCase, PrincipleField), out var named) ? named
            : FolderPrinciple(testCase.Name) ?? (guardrail ? ResponsibleAiPrinciple.PrivacyAndSecurity : ResponsibleAiPrinciple.ReliabilityAndSafety);
        var severity = Enum.TryParse<CheckSeverity>(Text(testCase, SeverityField), ignoreCase: true, out var set) && Enum.IsDefined(set) ? set
            : guardrail ? CheckSeverity.Critical : CheckSeverity.High;

        return new ScenarioMetadata(principle, severity, guardrail ? EvaluationMethod.Adversarial : EvaluationMethod.Deterministic,
            Text(testCase, RiskField), Text(testCase, ExpectedOutcomeField))
        {
            SensitiveData = Texts(testCase, SensitiveDataField),
            ExpectsUncertainty = testCase.Extra.TryGetValue(ExpectsUncertaintyField, out var flag) && flag.ValueKind == JsonValueKind.True,
        };
    }

    /// <summary>
    /// How the case falls short of saying what it tests: an unknown principle or severity, a folder and field that
    /// disagree, and with <paramref name="requireMetadata"/>, no principle, no risk, or no explicit expectation. Empty: it's fine.
    /// </summary>
    public static IReadOnlyList<string> Problems(AgentTestCase testCase, bool requireMetadata)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var name = testCase.Name;
        var problems = new List<string>();
        var field = Text(testCase, PrincipleField);
        var folder = FolderPrinciple(name);
        var hasField = TryParsePrinciple(field, out var named);

        if (field is not null && !hasField)
        {
            problems.Add($"Test case {name} has \"{PrincipleField}\": \"{field}\", which isn't one of {string.Join(", ", Enum.GetNames<ResponsibleAiPrinciple>())}.");
        }
        else if (hasField && folder is { } inFolder && inFolder != named)
        {
            problems.Add($"Test case {name} is in the {inFolder} folder but its \"{PrincipleField}\" is {named}.");
        }

        if (Text(testCase, SeverityField) is { } severity && !(Enum.TryParse<CheckSeverity>(severity, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)))
        {
            problems.Add($"Test case {name} has \"{SeverityField}\": \"{severity}\", which isn't one of {string.Join(", ", Enum.GetNames<CheckSeverity>())}.");
        }

        if (!requireMetadata)
        {
            return problems;
        }

        if (!hasField && folder is null)
        {
            problems.Add($"Test case {name} must be in a principle's folder, e.g. evaluation/Fairness/, or set \"{PrincipleField}\".");
        }

        if (Text(testCase, RiskField) is null)
        {
            problems.Add($"Test case {name} must say what risk it tests in \"{RiskField}\".");
        }

        var metadata = For(testCase);
        var expects = testCase.MustMention.Count > 0 || testCase.MustNotMention.Count > 0 || testCase.GuardrailMustBlock
            || metadata.SensitiveData.Count > 0;
        if (!expects)
        {
            problems.Add($"Test case {name} has no explicit pass condition: give \"mustMention\", \"mustNotMention\" or \"{SensitiveDataField}\".");
        }

        if (metadata.ExpectsUncertainty && testCase.MustMention.Count == 0)
        {
            problems.Add($"Test case {name} sets \"{ExpectsUncertaintyField}\": put the sentence the agent should use, e.g. \"No information available\", in \"mustMention\".");
        }

        return problems;
    }

    /// <summary>
    /// A principle by name, ignoring case and anything but letters, so "ReliabilityAndSafety", "reliability-and-safety"
    /// and "Reliability and safety" all match.
    /// </summary>
    public static bool TryParsePrinciple(string? text, out ResponsibleAiPrinciple principle)
    {
        principle = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var letters = new string([.. text.Where(char.IsLetter)]);
        foreach (var candidate in Enum.GetValues<ResponsibleAiPrinciple>())
        {
            if (string.Equals(candidate.ToString(), letters, StringComparison.OrdinalIgnoreCase))
            {
                principle = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The principle folder under the case's section, e.g. Fairness in "evaluation/Fairness/x"; null when there's none.</summary>
    private static ResponsibleAiPrinciple? FolderPrinciple(string caseName)
    {
        var parts = caseName.Split('/');
        return parts.Length >= 3 && TryParsePrinciple(parts[1], out var principle) ? principle : null;
    }

    private static string? Text(AgentTestCase testCase, string field) =>
        testCase.Extra.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    private static IReadOnlyList<string> Texts(AgentTestCase testCase, string field) =>
        testCase.Extra.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!).Where(static text => !string.IsNullOrWhiteSpace(text))]
            : [];
}

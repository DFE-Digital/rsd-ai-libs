using System.Text.Json;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Quality;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;

/// <summary>
/// What a test case says about itself for the quality gate, from its file's <c>"type"</c> and its own fields: the
/// principle and risk it tests, how much a failure matters, and any privacy or transparency expectations. A test is filed
/// under the principle its file is for, e.g. <c>"type": "fairness"</c>, the second part of its name
/// (<c>evaluation/fairness/FAIR-001</c>).
/// </summary>
/// <param name="Principle">The principle it tests.</param>
/// <param name="Severity">How much a failure matters: <c>"severity"</c>, or critical for guardrail cases and high for others.</param>
/// <param name="Method">Adversarial for guardrail cases; deterministic for others, whose facts are checked in code.</param>
/// <param name="Risk">The risk it tests, from <c>"risk"</c>, else <c>"description"</c>, e.g. "Reports a closed concern as ongoing".</param>
/// <param name="ExpectedOutcome">What should happen, from <c>"expectedOutcome"</c>, in words.</param>
public sealed record ScenarioMetadata(ResponsibleAiPrinciple Principle, CheckSeverity Severity, EvaluationMethod Method,
    string? Risk, string? ExpectedOutcome)
{
    public const string SeverityField = "severity";
    public const string RiskField = "risk";

    /// <summary>A test's description; its risk when it has no <c>"risk"</c>.</summary>
    public const string DescriptionField = "description";

    public const string ExpectedOutcomeField = "expectedOutcome";
    public const string SensitiveDataField = "sensitiveData";
    public const string ExpectsUncertaintyField = Options.ReleaseGateSettings.ExpectsUncertaintyField;

    /// <summary>Each principle as a file's <c>"type"</c>, e.g. "reliability-and-safety", for messages.</summary>
    private static readonly string PrincipleTypes = string.Join(", ", Enum.GetNames<ResponsibleAiPrinciple>().Select(static name =>
        string.Concat(name.Select(static (c, i) => i > 0 && char.IsUpper(c) ? $"-{char.ToLowerInvariant(c)}" : char.ToLowerInvariant(c).ToString()))));

    /// <summary>Values planted in the case's evidence or prompt that must never appear in the answer, from <c>"sensitiveData"</c>.</summary>
    public IReadOnlyList<string> SensitiveData { get; init; } = [];

    /// <summary>The answer must say the agent doesn't know or has no evidence, from <c>"expectsUncertainty": true</c>.</summary>
    public bool ExpectsUncertainty { get; init; }

    public static ScenarioMetadata For(AgentTestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var guardrail = ReleaseSections.IsGuardrailCase(testCase.Name);
        var (sectionPrinciple, sectionSeverity, method) = guardrail
            ? (ResponsibleAiPrinciple.PrivacyAndSecurity, CheckSeverity.Critical, EvaluationMethod.Adversarial)
            : (ResponsibleAiPrinciple.ReliabilityAndSafety, CheckSeverity.High, EvaluationMethod.Deterministic);

        var principle = TryParsePrinciple(TypeOf(testCase.Name), out var typed) ? typed : sectionPrinciple;
        var severity = TryParseSeverity(Text(testCase, SeverityField), out var set) ? set : sectionSeverity;

        return new ScenarioMetadata(principle, severity, method, RiskOf(testCase), Text(testCase, ExpectedOutcomeField))
        {
            SensitiveData = Texts(testCase, SensitiveDataField),
            ExpectsUncertainty = testCase.Extra.TryGetValue(ExpectsUncertaintyField, out var flag) && flag.ValueKind == JsonValueKind.True,
        };
    }

    /// <summary>
    /// How the case falls short of saying what it tests: a file type that isn't a principle, or an unknown severity, and
    /// with <paramref name="requireMetadata"/>, no risk or no explicit expectation. Empty: it's fine.
    /// </summary>
    public static IReadOnlyList<string> Problems(AgentTestCase testCase, bool requireMetadata)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var name = testCase.Name;
        var problems = new List<string>();

        if (!TryParsePrinciple(TypeOf(name), out _))
        {
            problems.Add($"Test case {name} must be in a file whose \"type\" is a Responsible AI principle: one of {PrincipleTypes}.");
        }

        if (Text(testCase, SeverityField) is { } severity && !TryParseSeverity(severity, out _))
        {
            problems.Add($"Test case {name} has \"{SeverityField}\": \"{severity}\", which isn't one of {string.Join(", ", Enum.GetNames<CheckSeverity>())}.");
        }

        if (!requireMetadata)
        {
            return problems;
        }

        if (RiskOf(testCase) is null)
        {
            problems.Add($"Test case {name} must say what risk it tests in \"{DescriptionField}\" or \"{RiskField}\".");
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

        // Letters only, so it can only match a name, never a number or a list of names.
        var letters = new string([.. text.Where(char.IsLetter)]);
        return Enum.TryParse(letters, ignoreCase: true, out principle);
    }

    /// <summary>A severity by name, ignoring case; only the defined ones, so "7" isn't a severity.</summary>
    private static bool TryParseSeverity(string? text, out CheckSeverity severity) =>
        Enum.TryParse(text, ignoreCase: true, out severity) && Enum.IsDefined(severity);

    /// <summary>The type of the file the test is in, e.g. "fairness" in "evaluation/fairness/FAIR-001"; null when there's none.</summary>
    private static string? TypeOf(string caseName)
    {
        var parts = caseName.Split('/');
        return parts.Length >= 3 ? parts[1] : null;
    }

    /// <summary>The risk the case tests: its <c>"risk"</c>, else its <c>"description"</c>.</summary>
    private static string? RiskOf(AgentTestCase testCase) => Text(testCase, RiskField) ?? Text(testCase, DescriptionField);

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

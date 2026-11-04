using System.Text.Json;
using System.Text.Json.Serialization;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>A test for an agent: a prompt, optional evidence, and facts the answer must or mustn't contain (case-insensitive).</summary>
public sealed record AgentTestCase(string Name, string Prompt)
{
    /// <summary>A test's id in a file for one principle, e.g. "FAIR-001"; kept in <see cref="Extra"/>.</summary>
    private const string IdField = "id";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string? Evidence { get; init; }

    public IReadOnlyList<string> MustMention { get; init; } = [];

    public IReadOnlyList<string> MustNotMention { get; init; } = [];

    /// <summary>
    /// Optional: the group the case represents, e.g. "special-schools" or "rural", so the report can compare scores across
    /// groups and catch an agent that serves one group worse.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// A Foundry guardrail blocking the prompt or answer is a correct outcome, e.g. for a case with instructions injected
    /// into its evidence. Such a case passes when it's blocked, and is checked as usual when it isn't.
    /// </summary>
    public bool GuardrailMayBlock { get; init; }

    /// <summary>
    /// The Foundry guardrail must block the prompt or answer, e.g. for instructions planted in the evidence: the case
    /// passes only when it's blocked, so the release gate proves the guardrail catches the attack, not only that the
    /// agent ignored it.
    /// </summary>
    public bool GuardrailMustBlock { get; init; }

    /// <summary>Any other fields in the case's file, by name, for your own checks; the runner ignores them.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extra { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Loads every <c>*.json</c> file in <paramref name="directory"/> and its subfolders, each the tests for one
    /// Responsible AI principle, split by section: <c>{ "type": "privacy-and-security", "description": "...",
    /// "evaluation": [ { "id": "PRIV-001", "name": "...", "description": "...", "prompt": "...", "evidence": "...",
    /// "mustMention": [ ... ], "mustNotMention": [ ... ], "group": "..." } ], "guardrail": [ { "id": "PRIV-002",
    /// "prompt": "..." } ] }</c>. Every test in <c>"guardrail"</c> is an attack the guardrail must block, without
    /// <c>"guardrailMustBlock"</c>. A file may instead give all its tests in <c>"tests"</c>, each in the guardrail section
    /// when it sets <c>"guardrailMustBlock": true</c>. Each test is named <c>&lt;section&gt;/&lt;type&gt;/&lt;id&gt;</c>,
    /// e.g. "evaluation/fairness/FAIR-001". A test's other fields, its id, name and description included, are kept in
    /// <see cref="Extra"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A file has no type or tests, or mixes <c>"tests"</c> with the sections; a test has no id or prompt, or is in the
    /// wrong section for its <c>"guardrailMustBlock"</c>; or two tests share a name or id.
    /// </exception>
    public static async Task<IReadOnlyList<AgentTestCase>> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var cases = new List<AgentTestCase>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<TestsFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            cases.AddRange(FromTestsFile(path, file));
        }

        if (cases.GroupBy(static c => c.Name, StringComparer.Ordinal).FirstOrDefault(static g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidOperationException(string.Format(Constants.ErrorMessages.DuplicateTestCase, duplicate.Key));
        }

        // Names include the section and type, so the same id in two of them is caught here: an id is the agent's alone.
        if (cases.GroupBy(static c => c.Name[(c.Name.LastIndexOf('/') + 1)..], StringComparer.Ordinal)
            .FirstOrDefault(static g => g.Count() > 1) is { } reused)
        {
            throw new InvalidOperationException(string.Format(Constants.ErrorMessages.DuplicateTestId, reused.Key,
                string.Join(", ", reused.Select(static c => c.Name))));
        }

        return cases;
    }

    /// <summary>Each test in a file for one principle, named by its section, the file's type and its id.</summary>
    private static List<AgentTestCase> FromTestsFile(string path, TestsFile? file)
    {
        if (string.IsNullOrWhiteSpace(file?.Type) || (file.Tests is null && file.Evaluation is null && file.Guardrail is null))
        {
            throw new InvalidOperationException(string.Format(Constants.ErrorMessages.TestFileNotStandard, path));
        }

        if (file.Tests is not null && (file.Evaluation is not null || file.Guardrail is not null))
        {
            throw new InvalidOperationException(string.Format(Constants.ErrorMessages.TestFileMixesSections, path));
        }

        var type = file.Type.Trim();
        if (file.Tests is not null)
        {
            // One list: each test's flag says its section.
            return [.. file.Tests.Select((test, i) => ToCase(path, type, test, i, test.GuardrailMustBlock == true))];
        }

        var evaluation = file.Evaluation ?? [];
        var guardrail = file.Guardrail ?? [];
        return
        [
            .. evaluation.Select((test, i) => test.GuardrailMustBlock == true
                ? throw new InvalidOperationException(string.Format(Constants.ErrorMessages.AttackInEvaluationSection, IdOrPosition(test, i), path))
                : ToCase(path, type, test, i, isAttack: false)),
            .. guardrail.Select((test, i) => test.GuardrailMustBlock == false
                ? throw new InvalidOperationException(string.Format(Constants.ErrorMessages.NonAttackInGuardrailSection, IdOrPosition(test, i), path))
                : ToCase(path, type, test, i, isAttack: true)),
        ];
    }

    /// <summary>A test as a case, in the guardrail section when it's an attack.</summary>
    private static AgentTestCase ToCase(string path, string type, CaseFile test, int index, bool isAttack)
    {
        if (!test.Extra.TryGetValue(IdField, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } id || string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException(string.Format(Constants.ErrorMessages.TestWithoutId, index + 1, path));
        }

        if (string.IsNullOrWhiteSpace(test.Prompt))
        {
            throw new InvalidOperationException(string.Format(Constants.ErrorMessages.TestCaseWithoutPrompt, $"{id} in {path}"));
        }

        var section = isAttack ? Options.ReleaseSections.Guardrail : Options.ReleaseSections.Evaluation;
        return new AgentTestCase($"{section}/{type}/{id.Trim()}", test.Prompt)
        {
            Evidence = test.Evidence,
            MustMention = test.MustMention ?? [],
            MustNotMention = test.MustNotMention ?? [],
            Group = test.Group,
            GuardrailMayBlock = test.GuardrailMayBlock,
            GuardrailMustBlock = isAttack,
            Extra = test.Extra,
        };
    }

    /// <summary>A test's id for a message, or its position when it has none.</summary>
    private static string IdOrPosition(CaseFile test, int index) =>
        test.Extra.TryGetValue(IdField, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } id
            ? id
            : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One test in a file. <see cref="GuardrailMustBlock"/> is null when the test doesn't set it.</summary>
    private sealed record CaseFile(string? Prompt, string? Evidence, List<string>? MustMention, List<string>? MustNotMention,
        string? Group, bool GuardrailMayBlock, bool? GuardrailMustBlock)
    {
        /// <summary>Every other field in the file.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; init; } = [];
    }

    /// <summary>
    /// A file of tests for one Responsible AI principle: in <see cref="Evaluation"/> and <see cref="Guardrail"/>, or all in
    /// <see cref="Tests"/>. Its <c>description</c> is for people reading it.
    /// </summary>
    private sealed record TestsFile(string? Type, string? Description, List<CaseFile>? Tests, List<CaseFile>? Evaluation,
        List<CaseFile>? Guardrail);
}

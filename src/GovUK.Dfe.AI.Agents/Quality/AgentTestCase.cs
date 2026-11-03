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
    /// Responsible AI principle: <c>{ "type": "fairness", "description": "...", "tests": [ { "id": "FAIR-001",
    /// "name": "...", "description": "...", "prompt": "...", "evidence": "...", "mustMention": [ ... ],
    /// "mustNotMention": [ ... ], "group": "...", "guardrailMustBlock": false } ] }</c>. Each test is named
    /// <c>&lt;section&gt;/&lt;type&gt;/&lt;id&gt;</c>, e.g. "evaluation/fairness/FAIR-001", in the guardrail section when it
    /// sets <c>"guardrailMustBlock": true</c>. A test's other fields, its id, name and description included, are kept in
    /// <see cref="Extra"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A file has no type or tests, a test no id or prompt, or two tests share a name.
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

        return cases;
    }

    /// <summary>Each test in a file for one principle, named by its section, the file's type and its id.</summary>
    private static List<AgentTestCase> FromTestsFile(string path, TestsFile? file)
    {
        if (string.IsNullOrWhiteSpace(file?.Type) || file.Tests is null)
        {
            throw new InvalidOperationException(string.Format(Constants.ErrorMessages.TestFileNotStandard, path));
        }

        var cases = new List<AgentTestCase>(file.Tests.Count);
        for (var i = 0; i < file.Tests.Count; i++)
        {
            var test = file.Tests[i];
            if (!test.Extra.TryGetValue(IdField, out var value) || value.ValueKind != JsonValueKind.String
                || value.GetString() is not { } id || string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException(string.Format(Constants.ErrorMessages.TestWithoutId, i + 1, path));
            }

            if (string.IsNullOrWhiteSpace(test.Prompt))
            {
                throw new InvalidOperationException(string.Format(Constants.ErrorMessages.TestCaseWithoutPrompt, $"{id} in {path}"));
            }

            var section = test.GuardrailMustBlock ? Options.ReleaseSections.Guardrail : Options.ReleaseSections.Evaluation;
            cases.Add(new AgentTestCase($"{section}/{file.Type.Trim()}/{id.Trim()}", test.Prompt)
            {
                Evidence = test.Evidence,
                MustMention = test.MustMention ?? [],
                MustNotMention = test.MustNotMention ?? [],
                Group = test.Group,
                GuardrailMayBlock = test.GuardrailMayBlock,
                GuardrailMustBlock = test.GuardrailMustBlock,
                Extra = test.Extra,
            });
        }

        return cases;
    }

    /// <summary>One test in a file.</summary>
    private sealed record CaseFile(string? Prompt, string? Evidence, List<string>? MustMention, List<string>? MustNotMention,
        string? Group, bool GuardrailMayBlock, bool GuardrailMustBlock)
    {
        /// <summary>Every other field in the file.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; init; } = [];
    }

    /// <summary>A file of tests for one Responsible AI principle; its <c>description</c> is for people reading it.</summary>
    private sealed record TestsFile(string? Type, string? Description, List<CaseFile>? Tests);
}

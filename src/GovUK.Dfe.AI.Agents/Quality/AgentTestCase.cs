using System.Text.Json;
using System.Text.Json.Serialization;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>A test for an agent: a prompt, optional evidence, and facts the answer must or mustn't contain (case-insensitive).</summary>
public sealed record AgentTestCase(string Name, string Prompt)
{
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
    /// Loads every <c>*.json</c> file in <paramref name="directory"/> and its subfolders, named after the file and any
    /// subfolder, e.g. "guardrail/jailbreak-prompt", so cases can be sorted into sections such as <c>evaluation/</c> and
    /// <c>guardrail/</c>: <c>{ "prompt": "...", "evidence": "...", "mustMention": [ ... ], "mustNotMention": [ ... ],
    /// "group": "...", "guardrailMayBlock": false, "guardrailMustBlock": false }</c>. Other fields are kept in
    /// <see cref="Extra"/>.
    /// </summary>
    public static async Task<IReadOnlyList<AgentTestCase>> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var cases = new List<AgentTestCase>();
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .Select(path => (Path: path, Name: NameOf(directory, path)))
            .OrderBy(static file => file.Name, StringComparer.Ordinal);
        foreach (var (path, name) in files)
        {
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<CaseFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(file?.Prompt))
            {
                throw new InvalidOperationException(string.Format(Constants.ErrorMessages.TestCaseWithoutPrompt, path));
            }

            cases.Add(new AgentTestCase(name, file.Prompt)
            {
                Evidence = file.Evidence,
                MustMention = file.MustMention ?? [],
                MustNotMention = file.MustNotMention ?? [],
                Group = file.Group,
                GuardrailMayBlock = file.GuardrailMayBlock,
                GuardrailMustBlock = file.GuardrailMustBlock,
                Extra = file.Extra,
            });
        }

        return cases;
    }

    /// <summary>The file's path under <paramref name="directory"/>, without ".json" and with "/" between folders.</summary>
    private static string NameOf(string directory, string path)
        => Path.ChangeExtension(Path.GetRelativePath(directory, path), null).Replace(Path.DirectorySeparatorChar, '/');

    private sealed record CaseFile(string? Prompt, string? Evidence, List<string>? MustMention, List<string>? MustNotMention,
        string? Group, bool GuardrailMayBlock, bool GuardrailMustBlock)
    {
        /// <summary>Every other field in the file.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> Extra { get; init; } = [];
    }
}

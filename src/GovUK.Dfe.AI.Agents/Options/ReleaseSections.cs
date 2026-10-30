using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Quality;

namespace GovUK.Dfe.AI.Agents.Options;

/// <summary>
/// The two sections a release is judged in, so every app reports them alike. Test cases are sorted by folder: an agent's
/// <c>guardrail/</c> cases prove the guardrail blocks attacks, and every other case, e.g. under <c>evaluation/</c>, tests
/// its answers.
/// </summary>
public static class ReleaseSections
{
    /// <summary>The guardrail is in place, blocks every <c>guardrail/</c> case, and blocks no other case by mistake.</summary>
    public const string Guardrail = "guardrail";

    /// <summary>Every other case has the right facts, and the judge's scores meet the gate.</summary>
    public const string Evaluation = "evaluation";

    /// <summary>The start of a guardrail test case's name, e.g. <c>guardrail/jailbreak-prompt</c>.</summary>
    public const string GuardrailFolder = Guardrail + "/";

    /// <summary>The start of an evaluation test case's name, e.g. <c>evaluation/ks2-trend</c>.</summary>
    public const string EvaluationFolder = Evaluation + "/";

    /// <summary>Whether a test case is in the guardrail section, by its folder.</summary>
    public static bool IsGuardrailCase(string caseName)
    {
        ArgumentNullException.ThrowIfNull(caseName);
        return caseName.StartsWith(GuardrailFolder, StringComparison.Ordinal);
    }

    /// <summary>
    /// How a test case breaks the conventions every app's cases follow, so each section means the same everywhere: it's
    /// in a <c>guardrail/</c> or <c>evaluation/</c> folder; a guardrail case requires the block
    /// (<c>"guardrailMustBlock": true</c>), as one that merely allows it passes unprotected, and isn't in a scored group;
    /// an evaluation case allows no block, so a block is reported as the guardrail's mistake. Empty: it follows them.
    /// </summary>
    public static IReadOnlyList<string> Problems(AgentTestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var name = testCase.Name;
        if (IsGuardrailCase(name))
        {
            return
            [
                .. testCase.GuardrailMustBlock ? Array.Empty<string>()
                    : [$"Test case {name} must set \"guardrailMustBlock\": true, so it proves the guardrail blocks it."],
                .. testCase.Group is null ? Array.Empty<string>()
                    : [$"Test case {name} has a \"group\", but guardrail cases aren't scored."],
            ];
        }

        if (!name.StartsWith(EvaluationFolder, StringComparison.Ordinal))
        {
            return [$"Test case {name} must be in its agent's {GuardrailFolder} or {EvaluationFolder} folder."];
        }

        return testCase.GuardrailMayBlock || testCase.GuardrailMustBlock
            ? [$"Test case {name} is an evaluation case, so it can't allow a guardrail block: move it to {GuardrailFolder}."]
            : [];
    }

    /// <summary>
    /// The section a test result's failures belong to: a guardrail case's, or a case the guardrail blocked by mistake,
    /// are the guardrail's; any other case's are the evaluation's.
    /// </summary>
    public static string Of(AgentTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.BlockedByGuardrailInError || IsGuardrailCase(result.CaseName) ? Guardrail : Evaluation;
    }
}

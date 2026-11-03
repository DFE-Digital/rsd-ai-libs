using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Quality;

namespace GovUK.Dfe.AI.Agents.Options;

/// <summary>
/// The two sections a release is judged in, so every app reports them alike. A test case's section starts its name: a
/// test that sets <c>"guardrailMustBlock": true</c> is a <c>guardrail/</c> case, proving the guardrail blocks an attack, and
/// every other is an <c>evaluation/</c> case, testing the agent's answers.
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

    /// <summary>Whether a test case is in the guardrail section, by its name.</summary>
    public static bool IsGuardrailCase(string caseName)
    {
        ArgumentNullException.ThrowIfNull(caseName);
        return caseName.StartsWith(GuardrailFolder, StringComparison.Ordinal);
    }

    /// <summary>
    /// How a test case breaks the conventions every app's cases follow, so each section means the same everywhere: its name
    /// starts with <c>guardrail/</c> or <c>evaluation/</c>; a guardrail case requires the block
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
            return [$"Test case {name} must be named {GuardrailFolder}... or {EvaluationFolder}..., as its agent's test files name it."];
        }

        return testCase.GuardrailMayBlock || testCase.GuardrailMustBlock
            ? [$"Test case {name} allows a guardrail block but isn't an attack: set \"guardrailMustBlock\": true if it is one, or remove \"guardrailMayBlock\"."]
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

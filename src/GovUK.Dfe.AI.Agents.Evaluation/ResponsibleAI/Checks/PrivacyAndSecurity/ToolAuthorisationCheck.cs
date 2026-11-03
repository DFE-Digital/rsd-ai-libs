using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.PrivacyAndSecurity;

/// <summary>
/// The agent can't change data without a person's approval: every allowed tool whose name says it changes something
/// (e.g. <c>update_case</c>, by the policy's <c>ApprovalRequiredToolPrefixes</c>) is in its <c>ToolsRequiringApproval</c>.
/// Read from the agent's definition, so it's exact. Core already fails startup when a tool needing approval isn't
/// allowed or has no approver, so that isn't checked again here.
/// </summary>
public sealed class ToolAuthorisationCheck : QualityCheck
{
    private static readonly char[] Separators = ['_', '-', '.', ' '];

    public override string Id => QualityCheckIds.ToolAuthorisation;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.PrivacyAndSecurity;

    public override CheckSeverity DefaultSeverity => CheckSeverity.Critical;

    public override EvaluationMethod Method => EvaluationMethod.Deterministic;

    public override string Description => "Tools that change data need a person's approval before each call.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        var agent = context.Agent;
        if (agent.AllowedTools.Count == 0)
        {
            yield return Passed("The agent can't call tools");
            yield break;
        }

        var approved = agent.ToolsRequiringApproval.ToHashSet(StringComparer.Ordinal);
        var problems = agent.AllowedTools
            .Where(tool => !approved.Contains(tool) && ChangesData(tool, context.Policy.EffectiveApprovalRequiredToolPrefixes))
            .Select(static tool => $"{tool} changes data but runs without a person's approval")
            .ToList();

        var needApproval = approved.Count == 0 ? "none" : string.Join(", ", approved);
        yield return problems.Count > 0
            ? Failed($"{problems.Count} tool(s) change data without approval",
                "Add each tool that changes data to the agent's ToolsRequiringApproval, with an IToolCallApprover (agents.AddToolApprover<T>()).",
                problems)
            : Passed($"{agent.AllowedTools.Count} allowed tool(s); {approved.Count} need approval",
                [$"Allowed: {string.Join(", ", agent.AllowedTools)}", $"Need approval: {needApproval}"]);
    }

    /// <summary>Whether the name's first word is one of the prefixes, e.g. "update" in update_case, update-case or updateCase.</summary>
    internal static bool ChangesData(string tool, IReadOnlyList<string> prefixes) =>
        prefixes.Any(prefix => tool.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (tool.Length == prefix.Length || Separators.Contains(tool[prefix.Length]) || char.IsUpper(tool[prefix.Length])));
}

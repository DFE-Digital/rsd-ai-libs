# Responsible AI

Hooks for the [UK Government AI Playbook](https://www.gov.uk/government/publications/ai-playbook-for-the-uk-government).
Your app owns the decisions, the user interface, the DPIA and the ATRS record.

## Every model call goes through a model connection

Put `DefaultModel` on a connection you control, e.g. an API Management gateway with content safety, and every model
call goes through it:

```json
"Foundry": {
  "DefaultModel": "myconnection/gpt-5.1",
  "Guardrail": "/subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.CognitiveServices/accounts/<resource>/raiPolicies/briefing-guardrail"
}
```

- Every agent version, including test copies, temporary agents and the Evaluation judge, must use a model on that
  connection (`myconnection/<model>`); any other is refused. A `DefaultModel` without one, e.g. `gpt-5.1`, allows any.
- `Guardrail` (optional) goes on every new agent version; a version without it is replaced, not reused. Give its full
  Resource Manager ID: the Agent Service rejects a bare name. An agent can set its own (`AgentDefinition.Guardrail`).
  `AddGuardrails()` with `"Targets": "Agents"` sets it for you, from its `Name`.
- If the gateway blocks content itself, list its errors so they count as guardrail blocks:
  `"GatewayGuardrailErrors": [ { "Status": 403, "Code": "<its error code>" } ]`. Copy both from a real blocked call,
  and always set `Code`: a 403 alone also means a missing role.

## A person approves changes

```csharp
public static readonly AgentDefinition Case = new("case-agent", "Case")
{
    AllowedTools = ["get_case", "update_case"],
    ToolsRequiringApproval = ["update_case"],
};

builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddToolApprover<ManagerApprover>());

public sealed class ManagerApprover(IApprovalQueue queue) : IToolCallApprover   // your own service
{
    public async Task<ToolCallApproval> ApproveAsync(string agentName, ToolCallRequest call, CancellationToken ct)
        => await queue.AskAsync(agentName, call.FunctionName, call.Arguments, ct)
            ? ToolCallApproval.Approve()
            : ToolCallApproval.Deny("the manager declined");
}
```

> A denied call doesn't run, and the agent is told why. Waiting counts towards `RunTimeout`. Startup fails if a tool needs
approval but there's no approver.

## Personal data is removed before the model sees it

```csharp
agents.AddRedactor(new PatternRedactor(new Dictionary<string, string>
{
    ["UPN"] = @"\b[A-Z]\d{11}[0-9A-Z]\b",
    ["NI number"] = @"\b[A-CEGHJ-PR-TW-Z]{2}\d{6}[A-D]\b",
}));
```

- Prompts, evidence and tool output are redacted before they're sent (`Pupil [UPN removed]`), and so are the copies
  evaluators and observers get. If a redactor fails, the run fails.
- Patterns can't find names: leave name fields out of search results and tool output, or implement
  `IAgentInputRedactor`.

## Every answer can be traced

Each `AgentResult` has a `RunId` and `CompletedAt` (on the trace as `dfe.ai_agents.run_id`). Use them to label answers
as AI-generated, save an audit record from an `IAgentRunObserver`, and store user feedback against the run.

For fairness, give release-gate test cases a `group` (e.g. `"special-schools"`) and set `MaxGroupGap`.
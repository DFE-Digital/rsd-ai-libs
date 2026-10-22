# Telemetry

What GovUK.Dfe.AI.Agents records, and how to query it in Application Insights.


Token usage is billed, so startup fails unless these metrics are subscribed. For tests and local development only, set
`"RequireTokenUsageTelemetry": false`. Names follow the
[OpenTelemetry GenAI conventions](https://github.com/open-telemetry/semantic-conventions-genai) (`gen_ai.*`); the rest
use `dfe.ai_agents.*`.

| Metric                                        | Records                                                                          |
| --------------------------------------------- | -------------------------------------------------------------------------------- |
| `gen_ai.client.inference.usage.input_tokens`  | Input tokens per run, tool rounds and failed runs included                       |
| `gen_ai.client.inference.usage.output_tokens` | Output tokens per run, reasoning, tool rounds and failed runs included           |
| `gen_ai.invoke_agent.duration`                | Seconds per run; `error.type` set when it failed                                 |
| `gen_ai.client.operation.duration`            | Seconds per call to Foundry, by agent and model; `error.type` set when it failed |
| `gen_ai.invoke_agent.inference_calls`         | Model calls per run (over 1: tool rounds or a retry)                             |
| `gen_ai.invoke_agent.tool_calls`              | Tool calls per run                                                               |
| `gen_ai.execute_tool.duration`                | Seconds per tool call, by tool                                                   |
| `gen_ai.invoke_workflow.duration`             | Seconds per parallel or sequential run                                           |
| `dfe.ai_agents.workflow.tokens`               | Tokens per parallel or sequential run, by `gen_ai.token.type`                    |
| `dfe.ai_agents.cost`                          | Cost per run, by agent, model and `dfe.ai_agents.currency` (needs `Pricing`)     |
| `dfe.ai_agents.workflow.cost`                 | Cost per parallel or sequential run (needs `Pricing`)                            |
| `dfe.ai_agents.run_slot.wait.duration`        | Seconds waiting for a slot; if it keeps rising, limits are too low               |
| `dfe.ai_agents.time_to_first_token`           | Seconds until a streamed run shows its first text                                |
| `dfe.ai_agents.guardrail.blocks`              | Prompts and answers a Foundry guardrail blocked                                  |
| `dfe.ai_agents.tool.approvals`                | Approval decisions, by tool and `dfe.ai_agents.tool.approved`                    |
| `dfe.ai_agents.tokens.remaining`              | The deployment's remaining tokens per minute                                     |
| `dfe.ai_agents.tokens.low`                    | Responses below `LowRemainingTokensPercent` of the token limit                   |

- Metrics are tagged with `gen_ai.agent.name`, `gen_ai.response.model` and `dfe.ai_agents.application`. Spans are
  `invoke_workflow`, `invoke_agent {agent}` and `execute_tool {tool}`.
- Telemetry never contains prompts, evidence, tool arguments, tool output or exception messages; a failure is only its
  `error.type`.
- **Logs are different:** a failed run logs its exception, which can include parts of the request. Limit who can read
  logs and how long they're kept.
- A guardrail block fails the run with an inner `AgentGuardrailException`; in parallel and sequential runs, the agent
  gets a fallback instead.

## Release metrics

Every app records the same release metrics, and they differ only by app. Each one answers one question before an
agent goes live:

| Metric                                  | Package    | Answers                                                       | Tags                                                       |
| --------------------------------------- | ---------- | ------------------------------------------------------------- | ---------------------------------------------------------- |
| `dfe.ai_agents.guardrail.checks`        | Guardrails | Was the safety net in place for this agent?                   | `dfe.ai_agents.guardrail.name`, `.outcome`, `.applied`     |
| `dfe.ai_agents.release_gate.test_cases` | Evaluation | Which cases failed, and were the guardrail cases blocked?     | `dfe.ai_agents.release_gate.test_case`, `.group`, `.outcome` |
| `dfe.ai_agents.release_gate.score`      | Evaluation | Are its answers grounded and relevant enough?                 | `gen_ai.evaluation.name`                                   |
| `dfe.ai_agents.release_gate.agents`     | Evaluation | Did it pass the release gate?                                 | `dfe.ai_agents.release_gate.outcome`                       |
| `dfe.ai_agents.agents.provisioned`      | Core       | Which version went live? None means it was held back.        | `gen_ai.agent.version`                                     |

- **One release, one ID:** everything recorded inside `IAgentReleaseGate.BeginRun()` carries
  `dfe.ai_agents.release_gate.run_id`, so one query shows a whole release. The ID is logged when the run starts.
  `EvaluateAsync` called outside a run opens its own run.
- **Outcomes:** `passed`, `failed` or `blocked_by_guardrail` (`AgentOutcomes`).
- **Token and cost metrics don't carry the run ID.** A tag on every call would make too many series.

## Queries

Set `app` to the app's `AiAgents:ApplicationName`. It is `cloud_RoleName` when OpenTelemetry's service name is set to
the same value (`AddService(applicationName)`), as in the [set-up](../readme.md#3-register).

Release result per agent, newest release first:

```kusto
let app = "<ApplicationName>";
customMetrics
| where cloud_RoleName == app
| extend run = tostring(customDimensions["dfe.ai_agents.release_gate.run_id"]),
    agent = tostring(customDimensions["gen_ai.agent.name"]),
    outcome = tostring(coalesce(customDimensions["dfe.ai_agents.release_gate.outcome"], customDimensions["dfe.ai_agents.guardrail.outcome"])),
    testCase = tostring(customDimensions["dfe.ai_agents.release_gate.test_case"]),
    evaluation = tostring(customDimensions["gen_ai.evaluation.name"]),
    version = tostring(customDimensions["gen_ai.agent.version"])
| where isnotempty(run)
| summarize
    started = min(timestamp),
    guardrailPassed = countif(name == "dfe.ai_agents.guardrail.checks" and outcome == "passed"),
    guardrailChecks = countif(name == "dfe.ai_agents.guardrail.checks"),
    casesPassed = sumif(valueSum, name == "dfe.ai_agents.release_gate.test_cases" and outcome == "passed"),
    casesBlocked = sumif(valueSum, name == "dfe.ai_agents.release_gate.test_cases" and outcome == "blocked_by_guardrail"),
    failedCases = make_set_if(testCase, name == "dfe.ai_agents.release_gate.test_cases" and outcome == "failed"),
    groundedness = round(avgif(valueSum / valueCount, name == "dfe.ai_agents.release_gate.score" and evaluation == "Groundedness"), 2),
    relevance = round(avgif(valueSum / valueCount, name == "dfe.ai_agents.release_gate.score" and evaluation == "Relevance"), 2),
    releaseGate = take_anyif(outcome, name == "dfe.ai_agents.release_gate.agents"),
    provisionedVersion = take_anyif(version, name == "dfe.ai_agents.agents.provisioned")
    by run, agent
// Passed when a check in the run passed, e.g. once ApplyAsync put the guardrail in place.
| extend guardrail = case(guardrailPassed > 0, "passed", guardrailChecks > 0, "failed", "not checked"),
    released = isnotempty(provisionedVersion)
| project started, run, agent, guardrail, casesPassed, casesBlocked, failedCases, groundedness, relevance, releaseGate,
    released, provisionedVersion
| order by started desc, agent asc
```

Tokens and cost per agent per day:

```kusto
let app = "<ApplicationName>";
customMetrics
| where cloud_RoleName == app
| where name in ("gen_ai.client.inference.usage.input_tokens", "gen_ai.client.inference.usage.output_tokens", "dfe.ai_agents.cost")
| summarize total = sum(valueSum) by bin(timestamp, 1d), agent = tostring(customDimensions["gen_ai.agent.name"]), name
| evaluate pivot(name, sum(total))
| order by timestamp desc, agent asc
```

Times per hour the app ran low on tokens:

```kusto
let app = "<ApplicationName>";
customMetrics
| where cloud_RoleName == app and name == "dfe.ai_agents.tokens.low"
| summarize times = sum(valueSum) by bin(timestamp, 1h)
```

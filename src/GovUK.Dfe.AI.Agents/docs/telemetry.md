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

Tokens and cost per app and agent per day:

```kusto
customMetrics
| where name in ("gen_ai.client.inference.usage.input_tokens", "gen_ai.client.inference.usage.output_tokens", "dfe.ai_agents.cost")
| summarize total = sum(valueSum) by name, cloud_RoleName, agent = tostring(customDimensions["gen_ai.agent.name"]), bin(timestamp, 1d)
```

Times per hour an app ran low on tokens:

```kusto
customMetrics
| where name == "dfe.ai_agents.tokens.low"
| summarize times = sum(valueSum) by cloud_RoleName, bin(timestamp, 1h)
```

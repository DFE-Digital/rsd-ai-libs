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
| `dfe.ai_agents.guardrail.blocks`              | Guardrail blocks, by `dfe.ai_agents.guardrail.stage`: prompt, answer or evidence |
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

| Metric                                  | Package    | Answers                                                   | Tags                                                         |
| --------------------------------------- | ---------- | --------------------------------------------------------- | ------------------------------------------------------------ |
| `dfe.ai_agents.guardrail.checks`        | Guardrails | Was the safety net in place for this agent?               | `dfe.ai_agents.guardrail.name`, `.outcome`, `.applied`       |
| `dfe.ai_agents.release_gate.test_cases` | Evaluation | Which cases failed, and were the guardrail cases blocked? | `dfe.ai_agents.release_gate.test_case`, `.group`, `.outcome` |
| `dfe.ai_agents.release_gate.score`      | Evaluation | Are its answers grounded and relevant enough?             | `gen_ai.evaluation.name`                                     |
| `dfe.ai_agents.release_gate.agents`     | Evaluation | Did it pass the release gate?                             | `dfe.ai_agents.release_gate.outcome`                         |
| `dfe.ai_agents.agents.provisioned`      | Core       | Which version went live? None means it was held back.     | `gen_ai.agent.version`                                       |

- **One release, one ID:** everything recorded inside `IAgentReleaseGate.BeginRun()` carries
  `dfe.ai_agents.release_gate.run_id`, so one query shows a whole release. The ID is logged when the run starts.
  `CheckReleaseAsync` or `EvaluateAsync` called outside a run opens its own.
- **Outcomes:** `passed`, `failed` or `blocked_by_guardrail` (`AgentOutcomes`).
- **Token and cost metrics don't carry the run ID.** A tag on every call would make too many series.

## Your own metrics

Record your app's own events the way the library records its own, so they sit beside its metrics and are never mixed
with another app's. Define each metric once, and record it with `IAgentMetrics`, which `AddAgents` registers:

```csharp
public static readonly AgentCounter Exports = new("dfe.ai_agents.briefing.exports", "Briefings exported, by format.");
public static readonly AgentHistogram Pages = new("dfe.ai_agents.briefing.pages", "{page}", "Pages per exported briefing.");

metrics.Count(Exports, agent.Name, new KeyValuePair<string, object?>("dfe.ai_agents.briefing.format", "pdf"));
metrics.Record(Pages, agent.Name, pageCount);

// Tags everything recorded inside, including parallel work started inside, e.g. one batch's ID.
using (metrics.BeginScope(new KeyValuePair<string, object?>("dfe.ai_agents.briefing.batch_id", batchId))) { ... }
```

| Enforced                                                                                                                        | So that                                                                         |
| ------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------- |
| On the library's meter (`AgentTelemetry.SourceName`)                                                                            | Collecting the library's metrics collects yours                                 |
| Named `dfe.ai_agents.` then lower-case words joined by dots or underscores                                                      | Every app's metrics follow one naming standard                                  |
| Tagged with `dfe.ai_agents.application` (`AiAgents:ApplicationName`) and `gen_ai.agent.name`, which no tag or scope can replace | Apps sharing a workspace never mix; every metric filters by app and agent alike |
| A test copy recorded under its agent's name                                                                                     | Release-gate runs group with the agent                                          |
| One instrument per name                                                                                                         | Defining a name twice doesn't create a conflicting duplicate                    |

Keep tag values few, e.g. an outcome or a batch, never a user or a prompt: each new value is a new series to store.

## Queries

The standard queries for every app that uses the library: paste one into Application Insights (**Logs**) and set
`app` to the app's `AiAgents:ApplicationName`. Every query reads only that app's data, so teams sharing an Application
Insights resource each see their own.

- **Give each app its own name:** set `AiAgents:ApplicationName` (e.g. `briefing-tool`), and the same value as its
  OpenTelemetry service name (`AddService("briefing-tool")`, as in the [set-up](../src/GovUK.Dfe.AI.Agents/Readme.md#3-register)). Apps that
  share a name, or leave it unset and share an assembly name, see each other's data.
- **Metrics** are filtered on `dfe.ai_agents.application`, which the library sets on every metric.
- **Logs** (`traces`, `exceptions`) don't carry that tag, so log queries filter on `cloud_RoleName`, the service name.
- **The filter separates data; it doesn't restrict access.** Anyone who can read the Application Insights resource can
  query every app in it. Where that matters, give each app its own resource.
- **Counters** (tokens, cost, blocks) add up with `sum(valueSum)`. **Histograms** (durations, scores) average as
  `sum(valueSum) / sum(valueCount)`; Application Insights keeps each batch's minimum and maximum, not percentiles.
- Each query covers the last 30 days; change `ago(30d)` as needed.

### Cost and usage

**Tokens and cost per agent and model, per day:**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(30d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name in ("gen_ai.client.inference.usage.input_tokens", "gen_ai.client.inference.usage.output_tokens", "dfe.ai_agents.cost")
| summarize inputTokens = tolong(sumif(valueSum, name endswith "input_tokens")),
    outputTokens = tolong(sumif(valueSum, name endswith "output_tokens")),
    cost = round(sumif(valueSum, name == "dfe.ai_agents.cost"), 4)
    by day = bin(timestamp, 1d), agent = tostring(customDimensions["gen_ai.agent.name"]),
    model = tostring(customDimensions["gen_ai.response.model"])
| extend totalTokens = inputTokens + outputTokens
| order by day desc, cost desc
```

**Cost so far this month, per agent, to track against a budget:**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp >= startofmonth(now()) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name == "dfe.ai_agents.cost"
| summarize cost = round(sum(valueSum), 2)
    by agent = tostring(customDimensions["gen_ai.agent.name"]), currency = tostring(customDimensions["dfe.ai_agents.currency"])
| order by cost desc
```

**Parallel and sequential runs (e.g. one briefing) per hour: how many, and their average and highest cost:**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(30d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name in ("dfe.ai_agents.workflow.tokens", "dfe.ai_agents.workflow.cost")
| extend tokenType = tostring(customDimensions["gen_ai.token.type"])
| summarize runs = sumif(valueCount, name == "dfe.ai_agents.workflow.tokens" and tokenType == "input"),
    tokens = tolong(sumif(valueSum, name == "dfe.ai_agents.workflow.tokens")),
    cost = sumif(valueSum, name == "dfe.ai_agents.workflow.cost"),
    highestCost = maxif(valueMax, name == "dfe.ai_agents.workflow.cost")
    by hour = bin(timestamp, 1h), mode = tostring(customDimensions["dfe.ai_agents.workflow.mode"])
| extend averageCost = round(cost / iff(runs == 0, 1, runs), 4)
| project hour, mode, runs, tokens, averageCost, highestCost = round(highestCost, 4), cost = round(cost, 4)
| order by hour desc
```

### Reliability and speed

**Runs, failures and duration per agent, per day. `errors` lists why runs failed, e.g. `content_filter` or `System.TimeoutException`:**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(30d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name == "gen_ai.invoke_agent.duration"
| extend error = tostring(customDimensions["error.type"])
| summarize runs = sum(valueCount), failed = sumif(valueCount, isnotempty(error)), errors = make_set_if(error, isnotempty(error)),
    averageSeconds = round(sum(valueSum) / sum(valueCount), 1), longestSeconds = round(max(valueMax), 1)
    by day = bin(timestamp, 1d), agent = tostring(customDimensions["gen_ai.agent.name"])
| extend failedPercent = round(100.0 * failed / runs, 1)
| order by day desc, failed desc
```

**Model calls per hour, by model: how long Foundry takes, and how many calls failed:**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(7d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name == "gen_ai.client.operation.duration"
| summarize calls = sum(valueCount), failed = sumif(valueCount, isnotempty(tostring(customDimensions["error.type"]))),
    averageSeconds = round(sum(valueSum) / sum(valueCount), 2), longestSeconds = round(max(valueMax), 2)
    by hour = bin(timestamp, 1h), model = tostring(customDimensions["gen_ai.response.model"])
| order by hour desc, calls desc
```

**Streamed answers: seconds until users see the first text, per agent, per hour:**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(7d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name == "dfe.ai_agents.time_to_first_token"
| summarize streams = sum(valueCount), averageSeconds = round(sum(valueSum) / sum(valueCount), 2), longestSeconds = round(max(valueMax), 2)
    by hour = bin(timestamp, 1h), agent = tostring(customDimensions["gen_ai.agent.name"])
| order by hour desc
```

**Capacity per hour: waits for a run slot, and how close the deployment came to its token limit. Rising waits mean `MaxConcurrency` or `GlobalConcurrency` is too low; low remaining tokens mean the deployment's quota is:**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(7d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name in ("dfe.ai_agents.run_slot.wait.duration", "dfe.ai_agents.tokens.remaining", "dfe.ai_agents.tokens.low")
| summarize waits = sumif(valueCount, name == "dfe.ai_agents.run_slot.wait.duration"),
    waitSeconds = sumif(valueSum, name == "dfe.ai_agents.run_slot.wait.duration"),
    longestWaitSeconds = round(maxif(valueMax, name == "dfe.ai_agents.run_slot.wait.duration"), 2),
    lowestRemainingTokens = tolong(minif(valueMin, name == "dfe.ai_agents.tokens.remaining")),
    lowTokenResponses = tolong(sumif(valueSum, name == "dfe.ai_agents.tokens.low"))
    by hour = bin(timestamp, 1h)
| extend averageWaitSeconds = round(waitSeconds / iff(waits == 0, 1, waits), 2)
| project hour, waits, averageWaitSeconds, longestWaitSeconds, lowestRemainingTokens, lowTokenResponses
| order by hour desc
```

### Safety and oversight

**Guardrail blocks per agent, per day, by what was blocked (`prompt`, `answer` or `evidence`):**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(30d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name == "dfe.ai_agents.guardrail.blocks"
| summarize blocks = tolong(sum(valueSum))
    by day = bin(timestamp, 1d), agent = tostring(customDimensions["gen_ai.agent.name"]),
    stage = tostring(customDimensions["dfe.ai_agents.guardrail.stage"])
| order by day desc, blocks desc
```

**Tool calls a person approved or denied, per agent and tool:**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(30d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name == "dfe.ai_agents.tool.approvals"
| extend isApproved = tolower(tostring(customDimensions["dfe.ai_agents.tool.approved"])) == "true"
| summarize approved = tolong(sumif(valueSum, isApproved)), denied = tolong(sumif(valueSum, not(isApproved)))
    by agent = tostring(customDimensions["gen_ai.agent.name"]), tool = tostring(customDimensions["gen_ai.tool.name"])
| order by denied desc
```

### Quality

Live answer scores (Evaluation's `SampleRate`) per agent, per day. A rising `dfe.ai_agents.evaluation.samples_dropped` means the judge can't keep up: lower `SampleRate`.

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(30d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name == "dfe.ai_agents.evaluation.score"
| summarize scored = sum(valueCount), score = round(sum(valueSum) / sum(valueCount), 2), lowest = round(min(valueMin), 2)
    by day = bin(timestamp, 1d), agent = tostring(customDimensions["gen_ai.agent.name"]),
    metric = tostring(customDimensions["gen_ai.evaluation.name"])
| order by day desc, agent asc, metric asc
```

### Releases

Every release, newest first, one row per agent: whether its guardrail was in place, its test cases and scores, the
gate's decision, and the version created (none means it was held back):

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(30d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| extend run = tostring(customDimensions["dfe.ai_agents.release_gate.run_id"])
| where isnotempty(run)
| extend agent = tostring(customDimensions["gen_ai.agent.name"]),
    outcome = tostring(coalesce(customDimensions["dfe.ai_agents.release_gate.outcome"], customDimensions["dfe.ai_agents.guardrail.outcome"])),
    testCase = tostring(customDimensions["dfe.ai_agents.release_gate.test_case"]),
    metric = tostring(customDimensions["gen_ai.evaluation.name"]),
    version = tostring(customDimensions["gen_ai.agent.version"])
| summarize started = min(timestamp),
    guardrailPassed = countif(name == "dfe.ai_agents.guardrail.checks" and outcome == "passed"),
    guardrailChecks = countif(name == "dfe.ai_agents.guardrail.checks"),
    casesPassed = tolong(sumif(valueSum, name == "dfe.ai_agents.release_gate.test_cases" and outcome == "passed")),
    attacksBlocked = tolong(sumif(valueSum, name == "dfe.ai_agents.release_gate.test_cases" and outcome == "blocked_by_guardrail")),
    failedCases = make_set_if(testCase, name == "dfe.ai_agents.release_gate.test_cases" and outcome == "failed"),
    groundedness = round(avgif(valueSum / valueCount, name == "dfe.ai_agents.release_gate.score" and metric == "Groundedness"), 2),
    relevance = round(avgif(valueSum / valueCount, name == "dfe.ai_agents.release_gate.score" and metric == "Relevance"), 2),
    lowestScore = round(minif(valueMin, name == "dfe.ai_agents.release_gate.score"), 2),
    gate = take_anyif(outcome, name == "dfe.ai_agents.release_gate.agents"),
    createdVersion = take_anyif(version, name == "dfe.ai_agents.agents.provisioned")
    by run, agent
// Passed when a check in the run passed, e.g. once ApplyAsync put the guardrail in place.
| extend guardrail = case(guardrailPassed > 0, "passed", guardrailChecks > 0, "FAILED", "not checked"),
    releaseGate = case(gate == "passed", "PASSED", gate == "failed", "FAILED", "not finished"),
    created = iif(isnotempty(createdVersion), strcat("yes, version ", createdVersion), "no")
| project started, run, agent, guardrail, casesPassed, attacksBlocked, failedCases, groundedness, relevance, lowestScore,
    releaseGate, created
| order by started desc, agent asc
```

Why a **release failed, one row per reason**, marked `guardrail` or `evaluation`. Set `runId` from the query above, or from the log line `Release run <id> started`. Each reason is logged as an error with the fields `ReleaseRunId`, `AgentName`, `ReleaseSection` and `ReleaseProblem`, so the query never depends on a message's wording:

```kusto
let app = "<ApplicationName>";
let runId = "<run id>";
traces
| where timestamp > ago(30d) and cloud_RoleName == app
| where tostring(customDimensions.ReleaseRunId) == runId and isnotempty(customDimensions.ReleaseSection)
| project agent = tostring(customDimensions.AgentName), section = tostring(customDimensions.ReleaseSection),
    reason = tostring(customDimensions.ReleaseProblem)
| order by agent asc, section asc
```

**Tokens and cost of one release, per agent**. Test copies count under their agent's name, and scoring under `answer-judge-*`. Token metrics don't carry the run ID, so this takes everything the app recorded while the run was going: run the gate as its own instance and filter on it (e.g. `cloud_RoleInstance == "release-gate"`) if the app also serves users meanwhile.

```kusto
let app = "<ApplicationName>";
let runId = "<run id>";
let window = customMetrics
    | where timestamp > ago(30d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
    | where tostring(customDimensions["dfe.ai_agents.release_gate.run_id"]) == runId
    | summarize started = min(timestamp) - 5m, finished = max(timestamp) + 5m;
customMetrics
| where timestamp between (toscalar(window | project started) .. toscalar(window | project finished))
| where tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name in ("gen_ai.client.inference.usage.input_tokens", "gen_ai.client.inference.usage.output_tokens", "dfe.ai_agents.cost")
| summarize inputTokens = tolong(sumif(valueSum, name endswith "input_tokens")),
    outputTokens = tolong(sumif(valueSum, name endswith "output_tokens")),
    cost = round(sumif(valueSum, name == "dfe.ai_agents.cost"), 4)
    by agent = tostring(customDimensions["gen_ai.agent.name"])
| extend totalTokens = inputTokens + outputTokens
| order by cost desc
```

**Release scores per agent over time, to spot quality drifting between releases:**

```kusto
let app = "<ApplicationName>";
customMetrics
| where timestamp > ago(90d) and tostring(customDimensions["dfe.ai_agents.application"]) == app
| where name == "dfe.ai_agents.release_gate.score"
| summarize released = min(timestamp), score = round(sum(valueSum) / sum(valueCount), 2)
    by run = tostring(customDimensions["dfe.ai_agents.release_gate.run_id"]), agent = tostring(customDimensions["gen_ai.agent.name"]),
    metric = tostring(customDimensions["gen_ai.evaluation.name"])
| order by agent asc, metric asc, released desc
```

### Errors

Every error and exception the app logged, newest first:

```kusto
let app = "<ApplicationName>";
union traces, exceptions
| where timestamp > ago(30d) and cloud_RoleName == app and severityLevel >= 3
| project timestamp, error = coalesce(message, outerMessage), exception = innermostMessage, instance = cloud_RoleInstance
| order by timestamp desc
```

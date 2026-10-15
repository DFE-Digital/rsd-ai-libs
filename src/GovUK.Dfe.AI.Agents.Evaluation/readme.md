# GovUK.Dfe.AI.Agents.Evaluation

A judge model scores your
[GovUK.Dfe.AI.Agents](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md)
agents' answers from 1 to 5 for:

- **Groundedness:** is the answer supported by the evidence?
- **Relevance:** does it answer the prompt?

Use the scores to spot quality dropping after a prompt or model change, and to stop a worse agent in your release gate.

## Set up

```sh
dotnet add package GovUK.Dfe.AI.Agents.Evaluation
```

```json
"AiAgents": {
  "Evaluation": {
    "SampleRate": 0.05
  }
}
```

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddQualityEvaluation());
```

| Setting      | Default                | Purpose                                                                                           |
| ------------ | ---------------------- | ------------------------------------------------------------------------------------------------- |
| `JudgeModel` | `Foundry:DefaultModel` | The model that scores answers. On a connection, it must be the same one as `Foundry:DefaultModel` |
| `SampleRate` | `0.05`                 | Share of live runs scored, from 0 to 1. `0` scores only release-gate tests                        |

The whole section is optional. A `JudgeModel` on another connection, or a `SampleRate` outside 0 to 1, fails startup.

## What gets scored

- **Live runs:** a `SampleRate` share is scored in the background, so runs aren't slowed. If the judge falls behind,
  extra samples are dropped.
- **Release gate:** every test case is scored. Use the ready-made gate below, or core's `IAgentTestRunner` and
  `report.FailuresAgainst(gate, baseline)` directly
  ([Release gate](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md#release-gate)).

## Release gate

`AddReleaseGate()` tests agents before they're released, typically in a provisioning job or CI:

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddQualityEvaluation()
    .AddReleaseGate(gate => gate.RefusalPhrases.Add("outside what I can answer")));

var results = await services.GetRequiredService<IAgentReleaseGate>().EvaluateAsync(BriefingAgents.All, ct);
if (results.Any(r => !r.Passed)) { /* don't provision; each r.Problems line says why */ }
```

```json
"Evaluation": {
  "ReleaseGate": { "MinimumScore": 3.5, "Tolerance": 0.2, "MaxGroupGap": 0.5, "Repeats": 1 }
}
```

- **Test cases:** one folder per agent under `TestCasesDirectory` (default `TestCases`), one JSON file per case. Each
  runs on a temporary copy of the agent, up to `MaxParallelTestRuns` (default 4) at a time, `Repeats` times.
- **The gate:** an agent fails if a case fails, or, with an evaluator, a metric (`Metrics`, default groundedness and
  relevance) averages below `MinimumScore`, isn't scored, falls more than `Tolerance` below the agent's baseline
  (`BaselinesDirectory/<agent>.json`, a previous report), or one case `group` scores more than `MaxGroupGap` below another.
- **Refusals:** a case whose `mustMention` includes one of `RefusalPhrases` is checked by that sentence and left out of
  the scores, as relevance marks any refusal as irrelevant, however correct.
- **Reports:** `ReportsDirectory/<agent>.json` (default `evaluation-reports`), with every answer, score and failure. Copy
  a passing one into `BaselinesDirectory` to set the bar.
- **Clean-up:** leftover test agents are deleted afterwards, even when the gate throws.
- **Without an evaluator,** only `mustMention` and `mustNotMention` are checked.

### Results in Foundry

Off by default. With `"PublishToFoundry": true`, each release's answers are also scored by Foundry's built-in groundedness and relevance
evaluators, using the judge model (a `<connection>/<model>` works). Results are on the Foundry project's **Evaluations**
page: one evaluation per agent, `<agent> release gate (<model>)`, with one run per release. It doesn't affect the gate,
and a failure is only logged. The identity needs **Foundry User** on the Foundry resource, as Microsoft requires for
evaluations that call a model.

| Setting               | Default              | Purpose                                                                   |
| --------------------- | -------------------- | ------------------------------------------------------------------------- |
| `TestCasesDirectory`  | `TestCases`          | One folder of cases per agent, relative to the app's folder               |
| `ReportsDirectory`    | `evaluation-reports` | Where reports are written, relative to the working directory              |
| `BaselinesDirectory`  | `Baselines`          | Each agent's accepted report, relative to the app's folder                |
| `Metrics`             | Groundedness, Relevance | The metrics each agent must be scored on                               |
| `MinimumScore`        | `3.5`                | Lowest average per metric (1 to 5)                                        |
| `Tolerance`           | `0.2`                | How far a score may fall below the baseline                               |
| `MaxGroupGap`         | Off                  | Largest gap between case groups                                           |
| `Repeats`             | `1`                  | Runs per case; scores are averaged                                        |
| `MaxParallelTestRuns` | `4`                  | Cases run at once                                                         |
| `RefusalPhrases`      | None                 | Fixed refusals whose cases aren't scored                                  |
| `PublishToFoundry`    | `false`              | Also sends answers to a Foundry evaluation                                |

## Metrics

Recorded under `AgentTelemetry.SourceName`, so the core package's OpenTelemetry setup already collects them.

| Metric                                     | Records                                                                                  |
| ------------------------------------------ | ---------------------------------------------------------------------------------------- |
| `dfe.ai_agents.evaluation.score`           | Each score, by agent, version and `gen_ai.evaluation.name`                               |
| `dfe.ai_agents.evaluation.samples_dropped` | Sampled runs not scored because the judge fell behind. If this rises, lower `SampleRate` |
| `dfe.ai_agents.release_gate.score`         | Each agent's average score per release-gate run, by `gen_ai.evaluation.name`             |
| `dfe.ai_agents.release_gate.test_cases`    | Release-gate cases, by agent and outcome: `passed`, `failed`, `blocked_by_guardrail`     |
| `dfe.ai_agents.release_gate.agents`        | Agents through the release gate, by outcome: `passed` or `failed`                        |

## Good to know

- **Cost:** each scored answer costs one judge call.
- **On a model connection:** the judge runs as an agent with no tools, through the connection and with the guardrail.
  Each metric keeps one agent (`quality-judge-<id>`), created once and reused. Its tokens and cost appear in the usual
  telemetry.
- **Nothing stored:** like agent runs, judge calls store nothing in Foundry, though they carry the run's prompt,
  evidence and answer.
- **Reasoning models** work as judges: the judge sends only the model name and messages.
- **Missing scores:** if a metric can't be scored (e.g. a wrong model name), the reason is logged.
- **Custom metrics:** `agents.AddQualityEvaluation(evaluator: new MyEvaluator())` with any Microsoft.Extensions.AI
  `IEvaluator`.
- **Your own scorer:** `agents.AddCustomQualityEvaluation(sp => new MyEvaluator())` with any `IAgentRunEvaluator`; no
  `JudgeModel` needed, and `SampleRate` still applies.
- **No safety metrics** (Microsoft's are preview-only): use the Guardrails package to block harmful content.

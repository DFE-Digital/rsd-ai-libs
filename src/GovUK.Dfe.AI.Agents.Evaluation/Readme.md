# GovUK.Dfe.AI.Agents.Evaluation

A judge model scores your
[GovUK.Dfe.AI.Agents](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/Readme.md)
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

**You need** nothing beyond core: the judge uses the same Foundry project and identity. The section is optional. A
`JudgeModel` on another connection, or a `SampleRate` outside 0 to 1, fails startup.

## What gets scored

- **Live runs:** a `SampleRate` share, in the background, so runs aren't slowed. If the judge falls behind, extra
  samples are dropped.
- **Release gate:** every test case (below).

## Release gate

The standard release check, the same in every app, so steps, logs, metrics and queries match across teams:

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddGuardrails()                 // the guardrail the check requires
    .AddQualityEvaluation()          // optional: judge scores
    .AddReleaseGate(gate => gate.RefusalPhrases.Add("outside what I can answer")));

var releaseGate = services.GetRequiredService<IAgentReleaseGate>();
using var run = releaseGate.BeginRun();     // one id on the release's metrics, provisioning's included
var results = await releaseGate.CheckReleaseAsync(BriefingAgents.All, ct);
if (results.All(r => r.Passed)) { /* provision or deploy, inside the run */ }
```

`CheckReleaseAsync`:

1. **Checks the guardrail.** If it's missing, weakened or can't be checked, every agent fails and no case runs.
   Without `AddGuardrails()`, the same, unless `RequireGuardrail` is `false`.
2. **Runs every agent's test cases** on a temporary copy, `Repeats` times, `MaxParallelTestRuns` at a time, and
   reports each problem in its section: `GuardrailProblems` or `EvaluationProblems`.
3. **Logs the result:** each agent's sections and, when any fails, an error for each reason with the fields
   `ReleaseRunId`, `AgentName`, `ReleaseSection` and `ReleaseProblem`, which the
   [standard queries](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Telemetry.md#releases) filter on.
   `AgentGateResult.ProblemLines()` gives the same reasons as `- {agent} [{section}]: {problem}` lines for a console.

An agent fails if a case fails, or with a judge, a metric averages below `MinimumScore`, isn't scored, falls more than
`Tolerance` below its baseline, or one case `group` trails another by more than `MaxGroupGap`.

### Test cases

One folder per agent under `TestCasesDirectory`, with the cases in two folders:

| Folder | Each case | Passes when |
| --- | --- | --- |
| `guardrail/` | Sets `"guardrailMustBlock": true`; no `group` | The guardrail blocks it |
| `evaluation/` | No guardrail flag | The answer has the right facts and scores. A block fails it as the guardrail's mistake |

`CheckReleaseAsync` fails a case that breaks these rules. Check them in a unit test too, so a wrong file fails the build
(`GovUK.Dfe.AI.Agents.ReleaseTesting`): `Assert.Empty(cases.SelectMany(ReleaseSections.Problems))`.

- **Refusals:** a case whose `mustMention` includes one of `RefusalPhrases` is checked by that sentence and not scored,
  as relevance marks any refusal as irrelevant.
- **Reports and baselines:** each run writes `ReportsDirectory/<agent>.json`. Copy a passing one into
  `BaselinesDirectory` to set the bar for the next release.
- **Clean-up:** test agents are deleted afterwards, even when the check throws.
- **`EvaluateAsync`** runs the cases without the guardrail check, conventions or logs, for tools that only score agents.

### Results in Foundry

Off by default. With `"PublishToFoundry": true`, each release's answers are also scored by Foundry's built-in
groundedness and relevance evaluators, with the judge model, and shown on the project's **Evaluations** page: one
evaluation per agent (`<agent> release gate (<model>)`), one run per release.

- It doesn't affect the gate; a failure is only logged.
- It sends test prompts, evidence and answers to Foundry.
- The identity needs **Foundry User** on the Foundry **resource**, not just the project. Without `PublishToFoundry`,
  Foundry User on the project is enough.

Settings, under `AiAgents:Evaluation:ReleaseGate`, e.g. `{ "MinimumScore": 3.5, "Tolerance": 0.2, "MaxGroupGap": 0.5 }`:

| Setting               | Default                 | Purpose                                                      |
| --------------------- | ----------------------- | ------------------------------------------------------------ |
| `TestCasesDirectory`  | `TestCases`             | One folder of cases per agent, relative to the app's folder  |
| `ReportsDirectory`    | `evaluation-reports`    | Where reports are written, relative to the working directory |
| `BaselinesDirectory`  | `Baselines`             | Each agent's accepted report, relative to the app's folder   |
| `Metrics`             | Groundedness, Relevance | The metrics each agent must be scored on                     |
| `MinimumScore`        | `3.5`                   | Lowest average per metric (1 to 5)                           |
| `Tolerance`           | `0.2`                   | How far a score may fall below the baseline                  |
| `MaxGroupGap`         | Off                     | Largest gap between case groups                              |
| `Repeats`             | `1`                     | Runs per case; scores are averaged                           |
| `MaxParallelTestRuns` | `4`                     | Cases run at once                                            |
| `RefusalPhrases`      | None                    | Fixed refusals whose cases aren't scored                     |
| `PublishToFoundry`    | `false`                 | Also sends answers to a Foundry evaluation                   |
| `RequireGuardrail`    | `true`                  | `CheckReleaseAsync` fails every agent without a guardrail check |

## Metrics

Recorded under `AgentTelemetry.SourceName`, so the core package's OpenTelemetry setup already collects them.

| Metric                                     | Records                                                                                         |
| ------------------------------------------ | ----------------------------------------------------------------------------------------------- |
| `dfe.ai_agents.evaluation.score`           | Each score, by agent, version and `gen_ai.evaluation.name`                                      |
| `dfe.ai_agents.evaluation.samples_dropped` | Sampled runs not scored because the judge fell behind. If this rises, lower `SampleRate`        |
| `dfe.ai_agents.release_gate.score`         | Each agent's average score per release-gate run, by `gen_ai.evaluation.name`                    |
| `dfe.ai_agents.release_gate.test_cases`    | Release-gate cases, by test case, group and outcome: `passed`, `failed`, `blocked_by_guardrail` |
| `dfe.ai_agents.release_gate.agents`        | Agents through the release gate, by outcome: `passed` or `failed`                               |

The release-gate metrics carry the run's `dfe.ai_agents.release_gate.run_id`, as do the guardrail check and the versions
provisioned inside `BeginRun()`. See [Release metrics](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Telemetry.md#release-metrics)
for what each answers and the query that shows each release per agent.

## Good to know

- **Cost:** each scored answer costs one judge call.
- **On a model connection:** the judge runs as a tool-less agent through the connection, with the guardrail. Each
  metric keeps one agent named after what it scores (`answer-judge-groundedness`, `answer-judge-relevance`), created
  once and reused; a new version only when its instructions or the guardrail change. Its tokens and cost show in
  telemetry.
- **Nothing stored:** judge calls store nothing in Foundry, though they carry the run's prompt, evidence and answer.
- **Reasoning models** work as judges.
- **Missing scores:** if a metric can't be scored (e.g. a wrong model name), the reason is logged.
- **Custom metrics:** `agents.AddQualityEvaluation(evaluator: new MyEvaluator())` with any Microsoft.Extensions.AI
  `IEvaluator`.
- **Your own scorer:** `agents.AddCustomQualityEvaluation(sp => new MyEvaluator())` with any `IAgentRunEvaluator`;
  `SampleRate` still applies.
- **No safety metrics** (Microsoft's are preview-only): use the Guardrails package to block harmful content.

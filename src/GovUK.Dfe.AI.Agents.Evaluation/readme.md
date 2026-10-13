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
    "JudgeModel": "<connection>/gpt-5.1",
    "SampleRate": 0.05
  }
}
```

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddQualityEvaluation());
```

| Setting      | Default  | Purpose                                                                          |
| ------------ | -------- | -------------------------------------------------------------------------------- |
| `JudgeModel` | Required | Any model in your Foundry project. It's called with the app's Foundry credential |
| `SampleRate` | `0.05`   | Share of live runs scored, from 0 to 1. `0` scores only release-gate tests       |

A missing `JudgeModel` or a `SampleRate` outside 0 to 1 fails startup, listed with any other missing settings.

## What gets scored

- **Live runs:** a `SampleRate` share is scored in the background, so runs aren't slowed. If the judge falls behind,
  extra samples are dropped.
- **Release gate:** every test case is scored, so `report.FailuresAgainst(gate, baseline)` can fail the build. See
  [Release gate](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md#release-gate).

## Metrics

Recorded under `AgentTelemetry.SourceName`, so the core package's OpenTelemetry setup already collects them.

| Metric                                     | Records                                                                                  |
| ------------------------------------------ | ---------------------------------------------------------------------------------------- |
| `dfe.ai_agents.evaluation.score`           | Each score, by agent, version and `gen_ai.evaluation.name`                               |
| `dfe.ai_agents.evaluation.samples_dropped` | Sampled runs not scored because the judge fell behind. If this rises, lower `SampleRate` |

## Good to know

- **Cost:** each scored answer costs one judge call.
- **Nothing stored:** like agent runs, judge calls store nothing in Foundry, though they carry the run's prompt,
  evidence and answer.
- **Reasoning models** work as judges: the judge sends only the model name and messages.
- **Missing scores:** if a metric can't be scored (e.g. a wrong model name), the reason is logged.
- **Custom metrics:** `agents.AddQualityEvaluation(evaluator: new MyEvaluator())` with any Microsoft.Extensions.AI
  `IEvaluator`.
- **Your own scorer:** `agents.AddCustomQualityEvaluation(sp => new MyEvaluator())` with any `IAgentRunEvaluator`; no
  `JudgeModel` needed, and `SampleRate` still applies.
- **No safety metrics** (Microsoft's are preview-only): use the Guardrails package to block harmful content.

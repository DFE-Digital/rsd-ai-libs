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

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddQualityEvaluation(judgeModel: "<connection>/gpt-5.1", sampleRate: 0.05));
```

The judge can be any model in your Foundry project. It's called with the app's Foundry credential, so there's nothing
else to set up.

## What gets scored

- **Live runs:** a sample (`sampleRate`, default 5%) is scored in the background, so runs aren't slowed down. Scores are
  recorded as the `dfe.ai_agents.evaluation.score` metric, by agent, version and `gen_ai.evaluation.name`. Set
  `sampleRate: 0` to score only release-gate tests.
- **Release gate:** every `IAgentTestRunner` test case is scored, so `report.BelowMinimum(...)` and
  `report.RegressionsFrom(baseline)` can fail the build. See
  [Release gate](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md#release-gate).

## Good to know

- **Cost:** each scored answer costs one judge call.
- **Reasoning models** work as judges: the judge sends only the model name and messages.
- **Missing scores:** if a metric can't be scored (for example, the model name is wrong), the reason is logged.
- **Custom metrics:** pass your own `IEvaluator` as `evaluator`.
- **Safety metrics aren't included,** because Microsoft's safety evaluators are preview-only. Use the Guardrails
  package to block harmful content instead.

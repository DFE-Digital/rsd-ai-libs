# Test case templates

A complete set of quality gate test cases for one agent, in the standard format: one file per Responsible AI
principle. They pass the gate's conventions and dataset checks as they are (a unit test in this repository proves it),
so copy them and change the content, not the shape.

## Use them

1. Copy `my-agent/` to your app's `TestCases/<your agent's name>/`.
2. Replace each test's `prompt`, `evidence` and expected facts with your agent's own. Use made-up data only.
3. Keep every `id` unique for the agent, and never reuse one: reports, risk acceptances and baselines refer to it.
4. Check the files in a unit test ([Ship an agent](../../ShipAnAgent.md#write-test-cases)).

| File | Principle | What its tests prove |
| --- | --- | --- |
| `reliability-and-safety.json` | Reliability and safety | Answers have the right facts from the evidence |
| `fairness.json` | Fairness | Each user `group` is served as well, with at least 5 tests per group |
| `privacy-and-security.json` | Privacy and security | Planted personal data isn't repeated; attacks on the question and the evidence are blocked |
| `inclusiveness.json` | Inclusiveness | Answers work for every reader, e.g. acronyms explained |
| `transparency.json` | Transparency | The agent says when it doesn't know, and cites its evidence |

Accountability has no file: the gate proves it from the release itself (traceability) and from people's sign-offs in
the governance file ([Exceptions and approvals](../../QualityGate.md#exceptions-and-approvals)).

## The format

```json
{
  "type": "fairness",
  "description": "What the file's tests evaluate, for people reading it.",
  "tests": [
    {
      "id": "FAIR-001",
      "name": "A short name for reports",
      "description": "The risk the test covers, in a sentence.",
      "prompt": "The question.",
      "evidence": "[Evidence 1]\nWhat the agent is given to answer from.",
      "mustMention": [ "facts the answer must contain" ],
      "mustNotMention": [ "facts it mustn't" ],
      "group": "the user group it represents, for fairness"
    }
  ]
}
```

| Field | Needed | Means |
| --- | --- | --- |
| `type` | Yes | The principle: `reliability-and-safety`, `fairness`, `privacy-and-security`, `inclusiveness` or `transparency` |
| `id` | Yes | Unique for the agent, e.g. `FAIR-001`. The test is reported as `evaluation/fairness/FAIR-001` |
| `description` | Yes | The risk it tests (or give `risk`) |
| `prompt` | Yes | The question |
| A pass condition | Yes, one of | `mustMention`, `mustNotMention`, `sensitiveData`, or `"guardrailMustBlock": true` for an attack |
| `name`, `expectedOutcome` | No | For people reading the report |
| `evidence` | No | Numbered `[Evidence n]` when the agent must cite it |
| `group` | For fairness | The user group; each group needs at least 5 tests |
| `severity` | No | `Low` to `Critical`. Default High; Critical for attacks |
| `sensitiveData` | No | Made-up personal values in the evidence the answer must never repeat |
| `expectsUncertainty` | No | `true` when the agent should say it doesn't know, with the sentence it should use in `mustMention` |

An attack, `"guardrailMustBlock": true`, is reported in the guardrail section, e.g. `guardrail/privacy-and-security/PRIV-002`.

All fields: [Quality gate: test cases](../../QualityGate.md#test-cases).

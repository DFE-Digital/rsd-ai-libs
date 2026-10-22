# GovUK.Dfe.AI.Agents.Guardrails

Makes sure every model deployment, or agent version, your
[GovUK.Dfe.AI.Agents](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md)
agents use has an Azure AI Foundry guardrail. Foundry then blocks:

- harmful content (hate, sexual, violence, self-harm)
- jailbreak attempts
- instructions hidden in documents and tool output
- protected material

## Set up

```sh
dotnet add package GovUK.Dfe.AI.Agents.Guardrails
```

```json
"AiAgents": {
  "Guardrails": {
    "SubscriptionId": "<subscription id>",
    "ResourceGroup": "<resource group>",
    "Name": "briefing-guardrail",
    "Deployments": [ "gpt-5.1" ]
  }
}
```

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddGuardrails());
```

**You need** these roles, all on the Foundry **resource** (not the project), where guardrails live:

| Identity                                   | Role                           | For                                                       |
| ------------------------------------------ | ------------------------------ | --------------------------------------------------------- |
| Every app                                  | Reader                         | Checking the guardrail at startup                         |
| A provisioning job                         | Cognitive Services Contributor | `ApplyAsync`; not needed if Bicep or Terraform creates it |
| Apps using `ScanPrompts` or `ScanEvidence` | Cognitive Services User        | Scanning input with Prompt Shields                        |

## Apply once, check everywhere

- **A provisioning job** calls `await guardrails.ApplyAsync(ct)` (`IFoundryGuardrailsService`). It creates or updates
  the guardrail and assigns it to each deployment, writing only what differs.
- **Every app** does nothing: at startup it checks each deployment has the guardrail, unweakened.
- **Infrastructure as code instead:** create the guardrail in Bicep or Terraform and skip `ApplyAsync`; apps still check
  it, with only Reader.
- **Missing or weakened:** startup fails, saying what's wrong (`"RequireAtStartup": false` only warns).
- **Resource Manager unreachable:** startup warns, so an outage doesn't stop new instances
  (`"FailWhenUnreachable": true` fails instead).
- **Metric:** every check counts `dfe.ai_agents.guardrail.checks` once per agent the app runs, with its outcome and
  whether `ApplyAsync` made it. It shows whether an agent's safety net was in place when it was released. See
  [Release metrics](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/telemetry.md#release-metrics).

## Models through a connection

When `Foundry:DefaultModel` is `<connection>/<model>`, the deployments aren't in your Foundry resource, so put the
guardrail on the agents:

```json
"Guardrails": {
  "SubscriptionId": "<subscription id>",
  "ResourceGroup": "<resource group>",
  "Name": "briefing-guardrail",
  "Targets": "Agents"
}
```

- No `Deployments`; `ApplyAsync` creates or updates only the guardrail and blocklists.
- Every agent version the app creates gets the guardrail, by its full Resource Manager ID (it becomes `Foundry:Guardrail`).
- The startup check confirms each pinned or externally managed version has it, e.g.
  `agent 'rsd-trust-agent' version 4 uses no guardrail`. Fix by provisioning a new version.
- `"Targets": "Deployments, Agents"` does both.

## Settings

| Setting                           | Default                            | Purpose                                                                                     |
| --------------------------------- | ---------------------------------- | ------------------------------------------------------------------------------------------- |
| `SubscriptionId`, `ResourceGroup` | Required                           | Where the Foundry resource is                                                               |
| `AccountName`                     | The resource in `Foundry:Endpoint` | Set only if the deployments are on another Foundry resource                                 |
| `Name`                            | Required                           | The guardrail's name in Foundry                                                             |
| `Targets`                         | `Deployments`                      | Where the guardrail goes: `Deployments`, `Agents`, or both                                  |
| `Deployments`                     | Required for `Deployments`         | Every model deployment your agents use                                                      |
| `BlockFrom`                       | `Medium`                           | Lowest harm severity blocked in prompts and answers. `Low` blocks the most                  |
| `PromptShields`                   | `true`                             | Blocks jailbreak attempts                                                                   |
| `IndirectAttacks`                 | `true`                             | Blocks instructions hidden in documents and tool output                                     |
| `CheckBlocklists`                 | `false`                            | Checks each run's question and evidence against `Blocklists` in the app (see below)         |
| `ScanPrompts`                     | `false`                            | Scans each run's question with Prompt Shields before it's sent (see below)                  |
| `ScanEvidence`                    | `false`                            | Scans each run's evidence with Prompt Shields before it's sent (see below)                  |
| `ContentSafetyEndpoint`           | The Foundry resource's own         | The Content Safety endpoint `ScanPrompts` and `ScanEvidence` call                           |
| `ProtectedMaterial`               | `true`                             | Blocks answers that reproduce protected text or code                                        |
| `Blocklists`                      | None                               | Your own blocked terms and patterns (see below)                                             |
| `RequireAtStartup`                | `true`                             | Fails startup when a deployment or agent version lacks the guardrail                        |
| `FailWhenUnreachable`             | `false`                            | Fails startup when the guardrail can't be checked, e.g. Resource Manager is unreachable     |
| `Authentication`                  | The default identity               | A separate identity for Resource Manager (or `agents.UseGuardrailsCredential(...)` in code) |

## Blocklists

Block your own terms, such as project code names or case reference formats:

```json
"Blocklists": {
  "case-references": { "Terms": [ "Project Falcon" ], "Patterns": [ "CASE-\\d{6}" ] }
}
```

`ApplyAsync` creates each blocklist with exactly these entries, for prompts and answers. The startup check reports a
blocklist that's detached or missing entries, by count only. Load sensitive terms from Key Vault, not appsettings.json.

## When a guardrail blocks something

The run fails with an inner `AgentGuardrailException` (`Stage`: `prompt`, `answer` or `evidence`), counted by
`dfe.ai_agents.guardrail.blocks`. In parallel and sequential runs, the agent gets a fallback result instead. Core
reports blocks with or without this package.

## Checking input in the app

A Foundry guardrail doesn't scan evidence in an agent's input, and doesn't apply at all to a model behind your own
gateway ([Bring your own model](https://learn.microsoft.com/azure/foundry/agents/how-to/ai-gateway)). So the app can
check each run's input itself, after redaction and before anything is sent. A match blocks the run like a guardrail
block, with the reason in `AgentGuardrailException.Reason`:

| Setting           | Checks                                             | How                                                                                                                   | Blocks with `Stage`    |
| ----------------- | -------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- | ---------------------- |
| `CheckBlocklists` | The question and the evidence against `Blocklists` | In the app, no call to Azure; first                                                                                   | `prompt` or `evidence` |
| `ScanPrompts`     | The question, for jailbreak attempts               | [Prompt Shields](https://learn.microsoft.com/azure/ai-services/content-safety/concepts/jailbreak-detection), one call | `prompt`               |
| `ScanEvidence`    | The evidence, for planted instructions             | Prompt Shields, one call per 9,000 characters, up to 4 at a time                                                      | `evidence`             |

- **Endpoint:** `https://<resource>.cognitiveservices.azure.com/`, unless `ContentSafetyEndpoint` is set.
- **Cost:** the two scans run side by side, about 100–200 ms for a typical run; blocklists cost nothing.
- **Fails closed:** if input can't be scanned, e.g. because the role is missing, the run fails rather than sending it.
- **Release gate:** test cases with `"guardrailMustBlock": true` prove each check works.
- **Your own:** register an `IInputShield` instead, for other checks.

## Not included

**PII filtering:** Foundry's PII filter needs a preview API, and this package uses GA APIs only. If you need it, turn it
on in the Foundry portal; the startup check leaves it alone.

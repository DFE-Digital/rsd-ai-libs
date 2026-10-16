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

## Apply once, check everywhere

| Where              | What to do                                                                                                                                                               | Role on the Foundry resource                                 |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------ |
| A provisioning job | Call `await guardrails.ApplyAsync(ct)` (`IFoundryGuardrailsService`). This creates or updates the guardrail and assigns it to each deployment, writing only what differs | Cognitive Services Contributor (Reader when nothing differs) |
| Every app          | Nothing. At startup, the app checks each deployment has the guardrail and that it hasn't been weakened                                                                   | Reader                                                       |

- **Scope:** both roles are on the Foundry **resource**, not the project: guardrails and deployments belong to the
  resource. Agents still need only Foundry User on the project.
- **Infrastructure as code instead:** create the guardrail in Bicep or Terraform and skip `ApplyAsync`; apps still check
  it, with only Reader.
- **Missing or weakened:** startup fails, saying what's wrong (`"RequireAtStartup": false` only warns).
- **Resource Manager unreachable:** startup warns, so an outage doesn't stop new instances
  (`"FailWhenUnreachable": true` fails instead).

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
| `ScanEvidence`                    | `false`                            | Scans each run's evidence with Prompt Shields before it's sent (see below)                  |
| `ContentSafetyEndpoint`           | The Foundry resource's own         | The Content Safety endpoint `ScanEvidence` calls                                            |
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

## When Foundry blocks something

The run fails with an inner `AgentGuardrailException` (`Stage`: `prompt`, `answer` or `evidence`), counted by
`dfe.ai_agents.guardrail.blocks`. In parallel and sequential runs, the agent gets a fallback result instead. Core
reports blocks with or without this package.

## Scanning evidence

A Foundry guardrail doesn't scan evidence passed in an agent's input for planted instructions. It only scans documents
in Chat Completions requests and the output of Foundry's own tools (Azure AI Search, OpenAPI and others). With
`"ScanEvidence": true`, each run's evidence, already redacted and cut to size, is checked by
[Prompt Shields](https://learn.microsoft.com/azure/ai-services/content-safety/concepts/jailbreak-detection) before it's
sent. If it finds an attack, the run is blocked like a guardrail block (`Stage`: `evidence`).

- **Identity:** needs **Cognitive Services User** on the Foundry resource, at
  `https://<resource>.cognitiveservices.azure.com/`.
- **Calls:** one call per 9,000 characters of evidence, up to 4 at a time, so about 100–200 ms for typical evidence.
- **Fails closed:** if the evidence can't be scanned, e.g. because the role is missing, the run fails rather than
  sending it unscanned.
- **Release gate:** a test case with `"guardrailMustBlock": true` proves the scan catches planted instructions.

## Not included

**PII filtering:** Foundry's PII filter needs a preview API, and this package uses GA APIs only. If you need it, turn it
on in the Foundry portal; the startup check leaves it alone.

# GovUK.Dfe.AI.Agents.Guardrails

Makes sure every model deployment your
[GovUK.Dfe.AI.Agents](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md)
agents use has an Azure AI Foundry guardrail. Before the model answers, Foundry blocks:

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
    "AccountResourceId": "/subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.CognitiveServices/accounts/<foundry>",
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

| Where | What to do | Role on the Foundry resource |
| --- | --- | --- |
| A provisioning job | Call `await guardrails.ApplyAsync(ct)` (`IFoundryGuardrailsService`). This creates or updates the guardrail and assigns it to each deployment | Cognitive Services Contributor |
| Every app | Nothing. At startup, the app checks each deployment has the guardrail and that it hasn't been weakened | Reader |

If a guardrail is missing or weakened, startup fails and says what's wrong. Set `"RequireAtStartup": false` to log a
warning instead. If Azure Resource Manager can't be reached, startup only logs a warning.

## Settings

| Setting | Default | Purpose |
| --- | --- | --- |
| `BlockFrom` | `Medium` | Lowest harm severity blocked in prompts and answers. `Low` blocks the most |
| `PromptShields` | `true` | Blocks jailbreak attempts |
| `IndirectAttacks` | `true` | Blocks instructions hidden in documents and tool output |
| `ProtectedMaterial` | `true` | Blocks answers that reproduce protected text or code |
| `Blocklists` | None | Your own blocked terms and patterns (see below) |
| `RequireAtStartup` | `true` | Fails startup when a deployment lacks the guardrail |
| `Authentication` | The default identity | A separate identity for Resource Manager (or `agents.UseGuardrailsCredential(...)` in code) |

## Blocklists

Block your own terms, such as project code names or case reference formats:

```json
"Blocklists": {
  "case-references": { "Terms": [ "Project Falcon" ], "Patterns": [ "CASE-\\d{6}" ] }
}
```

`ApplyAsync` creates each blocklist, keeps its entries exactly as configured, and applies it to prompts and answers. The
startup check reports a blocklist that's detached or missing entries, showing counts only, never the entries
themselves. Load sensitive terms from Key Vault, not appsettings.json.

## When Foundry blocks something

The run fails, and its inner exception is an `AgentGuardrailException` whose `Stage` is `prompt` or `answer`. The
`dfe.ai_agents.guardrail.blocks` metric counts each block. In parallel and sequential runs, the agent gets a fallback
result instead. Core does this reporting, with or without this package.

## Not included

**PII filtering:** Foundry's PII filter needs a preview API, and this package uses GA APIs only. If you need it, turn it
on in the Foundry portal; the startup check leaves it alone.

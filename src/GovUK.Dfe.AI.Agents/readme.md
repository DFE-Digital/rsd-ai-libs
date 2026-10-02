# GovUK.Dfe.AI.Agents

Run Azure AI Foundry agents from .NET: one at a time, in parallel or in sequence. Every run is checked, size-limited,
concurrency-limited and records its token usage.

## Packages

Install the core package, plus any add-ons you need. Each add-on depends on a compatible version of the core package.

| Package | Adds | Register with |
| --- | --- | --- |
| `GovUK.Dfe.AI.Agents` | Agents, versions, runs, answer checks, limits, telemetry | `AddAgents(...)` |
| [`GovUK.Dfe.AI.Agents.Mcp`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Mcp/readme.md) | Tools from your own MCP servers | `.AddMcpServers()` |
| [`GovUK.Dfe.AI.Agents.AISearch`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.AISearch/readme.md) | Evidence from Azure AI Search | `.AddAISearch()` |
| [`GovUK.Dfe.AI.Agents.Evaluation`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Evaluation/readme.md) | A judge model that scores answers | `.AddQualityEvaluation(judgeModel)` |
| [`GovUK.Dfe.AI.Agents.Guardrails`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Guardrails/readme.md) | Foundry guardrails on your model deployments | `.AddGuardrails()` |

## Quick start

### 1. Install

```sh
dotnet add package GovUK.Dfe.AI.Agents
dotnet add package Azure.Monitor.OpenTelemetry.AspNetCore   # token usage must be recorded
```

### 2. Configure

```json
{
  "AiAgents": {
    "ApplicationName": "briefing-tool",
    "Foundry": {
      "Endpoint": "https://<resource>.services.ai.azure.com/api/projects/<project>",
      "DefaultModel": "<connection>/gpt-5.1"
    },
    "Authentication": { "TenantId": "<tenant>", "ClientId": "<client id>" },
    "PromptFiles": { "SystemPrompts": { "Ofsted": "Prompts/Ofsted.md", "Synthesis": "Prompts/Synthesis.md" } },
    "RunTimeout": "00:02:00",
    "MaxConcurrency": 4
  }
}
```

- Give the identity the **Azure AI User** role on the Foundry project.
- Load `Authentication:ClientSecret` from Key Vault, never from appsettings.json. To use managed identities instead,
  see [Credentials](#credentials).
- Set each prompt file to *Copy to output directory*.

### 3. Register

```csharp
using GovUK.Dfe.AI.Agents.Diagnostics;   // AgentTelemetry

builder.Services.AddAgents(builder.Configuration, agents => agents.AddAgents(BriefingAgents.All));

// Required: startup fails unless token usage is recorded.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("briefing-tool"))
    .UseAzureMonitor()
    .WithTracing(t => t.AddSource(AgentTelemetry.SourceName))
    .WithMetrics(m => m.AddMeter(AgentTelemetry.SourceName));
```

If any setting is missing or invalid, startup fails with a single error that lists every problem.

### 4. Define agents

```csharp
using GovUK.Dfe.AI.Agents.ValueObjects;

public sealed record OfstedFindings(string Rating, IReadOnlyList<string> Strengths);

public static class BriefingAgents
{
    public static readonly AgentDefinition Ofsted = new("ofsted-agent", SystemPromptKey: "Ofsted")
    {
        OutputSchema = AgentOutputSchema.For<OfstedFindings>("ofsted_findings"),   // optional typed answer
    };

    public static readonly AgentDefinition Synthesis = new("synthesis-agent", "Synthesis");

    public static AgentDefinition[] All => [Ofsted, Synthesis];
}
```

### 5. Run

```csharp
using GovUK.Dfe.AI.Agents.Extensions;            // ReadOutputAs
using GovUK.Dfe.AI.Agents.Services.Interfaces;   // IAgentService

public sealed class BriefingService(IAgentService agents)
{
    public async Task<string> CreateAsync(string urn, string report, CancellationToken ct)
    {
        var ofsted = await agents.RunAsync(BriefingAgents.Ofsted, $"Summarise the latest inspection for URN {urn}.",
            evidence: report, cancellationToken: ct);
        var findings = ofsted.ReadOutputAs<OfstedFindings>();

        var briefing = await agents.RunAsync(BriefingAgents.Synthesis, $"Write a one-page briefing. Rating: {findings.Rating}.",
            evidence: ofsted.Output, cancellationToken: ct);
        return briefing.Output!;
    }
}
```

Put your instructions in `prompt`. Put untrusted material (documents, search results, other agents' output) in
`evidence`. Evidence is fenced as data, so text inside it can't act as an instruction.

### Namespaces

| `using GovUK.Dfe.AI.Agents…` | For |
| --- | --- |
| `.Builders` | `AgentsBuilder` |
| `.ValueObjects` | `AgentDefinition`, `AgentOutputSchema`, `AgentResult`, `AgentSpec`, `AgentRunSample` |
| `.Services.Interfaces` | `IAgentService`, `IAgentRunnerService`, `IAgentRuntimeService` |
| `.Extensions` | `ReadOutputAs<T>()`, `ToTokenUsageSummary()` |
| `.Context` / `.Context.Interfaces` | `AgentContext` / `IContextRetriever` |
| `.Filters` | `ODataFilter` |
| `.Quality` / `.Quality.Interfaces` | `AgentTestCase`, `AgentEvaluationReport` / `IAgentTestRunner`, `IAgentRunEvaluator` |
| `.Enums` | `AzureCredentialTarget`, `AgentTestTarget`, `GuardrailSeverity` |
| `.Diagnostics` | `AgentTelemetry` |
| `.Exceptions` | `AgentGuardrailException` |

`AddAgents` and every add-on's `Add…()` method are in `Microsoft.Extensions.DependencyInjection`, so registering needs no
extra usings.

## What happens in a run

1. **Version:** each definition becomes an agent version in Foundry. An unchanged definition reuses its version; a
   changed one creates a new version.
2. **Slot:** the run waits for a free concurrency slot.
3. **Prompt:** the prompt is sent, with evidence cut to size and fenced.
4. **Tools:** tool calls run in your app with your credential. Foundry never sees a tool's address or credential.
5. **Checks:** the answer is checked. If a check fails, the agent gets one retry.
6. **Record:** tokens, duration, call counts and any error type are recorded, and the conversation is deleted.

## Running agents

| `IAgentService` method | Use for | When an agent fails |
| --- | --- | --- |
| `RunAsync(definition, prompt, evidence)` | One agent | Throws |
| `RunParallelAsync(definitions, resolvePrompt, context, resolveEvidence: ...)` | Independent agents | That agent gets a fallback result; the others carry on |
| `RunSequentialAsync(definitions, resolvePrompt, initialInput, context)` | A chain, e.g. draft then review | That agent gets a fallback; the next agent gets the last good output |
| `ProvisionAsync(definitions)` | A [provisioning job](#centrally-managed-agents) | Throws |

- Results come back in the same order as the definitions. To total their tokens, call `results.ToTokenUsageSummary()`.
- To throw instead of returning fallbacks, pass `shouldSuppress: _ => false`. This also cancels the other agents.

| `AgentDefinition` property | Meaning |
| --- | --- |
| `Name` | The agent's name in Foundry. Must be unique per app if the project is shared |
| `SystemPromptKey` | Its key under `PromptFiles:SystemPrompts` |
| `IsManagedAgent` | `true` (default): kept and reused. `false`: created and deleted on every run |
| `AllowedTools` | The only tools the agent may call. Empty (the default) means no tools |
| `OutputSchema` | A JSON schema for a typed answer; read it with `ReadOutputAs<T>()` |
| `Validate`, `RequireCitations` | [Answer checks](#answer-checks) |

Other extension points:

- **Agents built in code:** subclass `ManagedAgentProviderBase` and add it with `agents.AddAgentProvider<T>()`.
- **Non-MCP tools:** `agents.AddTools("news-agent", new WebSearchToolProvider())`.
- **User prompt templates:** add them under `PromptFiles:UserPrompts` with `{{Name}}` placeholders, then fill them with
  `IPromptTemplateBuilder`.

## Answer checks

- **Citations (on by default):** if the evidence is numbered (as search results are), the answer must cite it as
  `[Evidence n]` and may only cite evidence that exists. Unnumbered evidence isn't checked. Set
  `RequireCitations = false` when the answer has nowhere to put citations.
- **Your own rules:** `Validate` returns a reason the answer is wrong, or `null` if it's fine.
- **Retry:** a failed check is sent back once, in the same conversation. If the answer fails again, the run fails.

```csharp
public static readonly AgentDefinition Ofsted = new("ofsted-agent", "Ofsted")
{
    OutputSchema = AgentOutputSchema.For<OfstedFindings>("ofsted_findings"),
    Validate = r => r.ReadOutputAs<OfstedFindings>().Rating is "Outstanding" or "Good" or "Requires improvement" or "Inadequate"
        ? null : "Rating must be an Ofsted grade.",
};
```

## Release gate

Run fixed test cases in CI and fail the build if an agent got worse:

```csharp
var cases = await AgentTestCase.LoadAsync("tests/ofsted-agent");   // JSON files: prompt, evidence, mustMention, mustNotMention
var report = await tests.RunAsync(BriefingAgents.Ofsted, cases);    // IAgentTestRunner
var baseline = JsonSerializer.Deserialize<AgentEvaluationReport>(await File.ReadAllTextAsync("baseline.json"))!;

if (!report.Passed
    || report.BelowMinimum(3.5, "Groundedness", "Relevance").Any()
    || report.RegressionsFrom(baseline, tolerance: 0.2).Any())
{
    throw new InvalidOperationException("ofsted-agent got worse; not publishing it.");
}
```

- Test cases run against a temporary copy of the agent, so a failed gate publishes nothing. To test the deployed
  version instead, pass `AgentTestTarget.Deployed`.
- Scores need an evaluator: add the Evaluation package, or your own with `agents.AddQualityEvaluation(sp => ...)`.
  Without an evaluator, only `mustMention` and `mustNotMention` are checked.
- Name the metrics you require in `BelowMinimum`, so the gate fails if the judge returns no scores.
- After a release passes, save its report as the new `baseline.json`.

## Environments and versions

Choose one setup:

- **One Foundry project per environment (simplest).** Leave versions unset. Each environment builds its agents from its
  own prompt files. Production needs write access to its project.
- **One shared project.** Dev creates versions. Staging and production pin the tested version with
  `"VersionPins": { "ofsted-agent": "3" }`. A pinned agent never creates or prunes versions. Set
  `"EnableDriftDetection": true` to log a warning when a pin no longer matches its definition.

In both setups:

- **Fix the model version in production.** An agent version fixes the prompt and tools, but not the model behind the
  deployment. Each result's `Model` property shows which model answered.
- **Prune old versions** with `"KeepLatestVersions": 3` (minimum 2). Pinned versions and `ProtectedVersions` are never
  deleted.

### Centrally managed agents

1. A provisioning job calls `await agents.ProvisionAsync(BriefingAgents.All)`. This creates or reuses each version.
2. Each app lists the agents and versions it runs, and needs no prompt files for them. `"latest"` follows the newest
   version (use it in dev only).

   ```json
   "ExternallyManagedAgents": { "ofsted-agent": "4", "trust-agent": "2" }
   ```

3. If the agents are in another Foundry project, add its `Endpoint`, and optionally an `Authentication` block.
4. Apps still run the tools. At startup, each app checks it can run every tool its agent versions call.

An agent can be in `VersionPins` or `ExternallyManagedAgents`, but not both.

## Scaling and cost

- **Concurrency:** `MaxConcurrency` limits runs on one instance. `GlobalConcurrency` limits runs across all instances that
  share a blob container. A run waits up to `MaxWaitForRunSlot`, then throws `TimeoutException`.

  ```json
  "GlobalConcurrency": { "MaxConcurrentRuns": 20, "BlobContainerUri": "https://<account>.blob.core.windows.net/aiagents-run-slots" }
  ```

  The identity needs **Storage Blob Data Contributor**: on the storage account if the library should create the
  container, or on the container if you create it yourself. Only HTTPS and Entra ID are supported; a SAS URI fails startup.
  To use another store, such as Redis, implement `IRunSlotStore`.
- **Cost per run:** `MaxOutputTokensPerRun` caps output tokens across tool rounds and the retry, including reasoning
  tokens. A run that reaches the cap fails.
- **Orphaned agents:** a crash can leave temporary agents behind. Add `agents.AddEphemeralAgentSweep()` to delete them
  every 30 minutes. The sweep only deletes this app's agents, and only ones older than any run could be, so it's safe to
  run on every instance. Keep `ApplicationName` unique per app.

## Telemetry

Token usage is billed, so it must be recorded: startup fails unless the metrics below are subscribed. In tests and local
development only, you can set `"RequireTokenUsageTelemetry": false`.

Names follow the [OpenTelemetry generative AI conventions](https://github.com/open-telemetry/semantic-conventions-genai)
(`gen_ai.*`), so standard GenAI dashboards understand them. Signals the conventions don't cover use `dfe.ai_agents.*`.

| Metric | Records |
| --- | --- |
| `gen_ai.client.inference.usage.input_tokens` | Input tokens per run, including tool rounds and failed runs |
| `gen_ai.client.inference.usage.output_tokens` | Output tokens per run, including reasoning, tool rounds and failed runs |
| `gen_ai.invoke_agent.duration` | Seconds per run; `error.type` is set when it failed |
| `gen_ai.invoke_agent.inference_calls` | Model calls per run (more than 1 means tool rounds or a retry) |
| `gen_ai.invoke_agent.tool_calls` | Tool calls per run |
| `gen_ai.execute_tool.duration` | Seconds per tool call, by tool |
| `gen_ai.invoke_workflow.duration` | Seconds per parallel or sequential run |
| `dfe.ai_agents.workflow.input_tokens` / `.output_tokens` | Total tokens per parallel or sequential run, e.g. one briefing |
| `dfe.ai_agents.run_slot.wait.duration` | Seconds spent waiting for a slot. If this keeps rising, the limits are too low |
| `dfe.ai_agents.evaluation.score` | Judge scores of sampled answers, by `gen_ai.evaluation.name` (Evaluation package) |
| `dfe.ai_agents.guardrail.blocks` | Prompts and answers a Foundry guardrail blocked |

Metrics are tagged with `gen_ai.agent.name`, the model (`gen_ai.response.model`) and the application
(`dfe.ai_agents.application`). The spans are `invoke_workflow` (a parallel or sequential run), `invoke_agent {agent}` and
`execute_tool {tool}`. Telemetry never contains prompts, evidence, tool arguments, tool output or exception messages:
a failure is recorded only as its `error.type`.

Tokens per app and agent per day:

```kusto
customMetrics
| where name in ("gen_ai.client.inference.usage.input_tokens", "gen_ai.client.inference.usage.output_tokens")
| summarize tokens = sum(valueSum) by cloud_RoleName, agent = tostring(customDimensions["gen_ai.agent.name"]), bin(timestamp, 1d)
```

When a guardrail blocks a prompt or answer, the run fails and the inner exception is an `AgentGuardrailException`. In
parallel and sequential runs, the agent gets a fallback result instead.

## Credentials

`Authentication` is the default identity. `Foundry`, `Search`, each MCP server, `GlobalConcurrency`, `Guardrails` and
`ExternallyManagedAgents` can each have their own `Authentication` block. Load each secret from Key Vault, e.g. the
secret `AiAgents--Foundry--Authentication--ClientSecret` (or the environment variable
`AiAgents__Foundry__Authentication__ClientSecret`).

To use managed identities, set credentials in code:

```csharp
agents.UseCredential(new ManagedIdentityCredential())                  // default for every service
      .UseCredentialFor(AzureCredentialTarget.Search, searchCredential) // one service
      .UseMcpCredential("school-performance", partnerCredential)       // one MCP server
      .UseExternallyManagedAgentsCredential(centralCredential);        // the central agents' project
```

Each service uses, in order: its code credential, its own `Authentication` block, then the default. Give each identity
only the role its service needs.

## Options

All options sit under `AiAgents`. You can also change them in code with `agents.Configure(o => ...)`.

| Setting | Default | Purpose |
| --- | --- | --- |
| `ApplicationName` | Entry assembly name | Telemetry tag, and scope of the orphan sweep |
| `RunTimeout` | None | Longest time one run may take |
| `MaxOutputTokensPerRun` | 32000 | Output tokens one run may use (minimum 16) |
| `MaxEvidenceCharacters` | 100000 | Longer evidence is cut, keeping the start |
| `MaxToolOutputCharacters` | 20000 | Longer tool output is cut |
| `FenceToolOutput` | `true` | Fences tool output as data |
| `DeleteConversationsAfterRun` | `true` | Keeps prompts and evidence out of Foundry |
| `MaxConcurrency` | None | Runs at once on one instance |
| `GlobalConcurrency` | Off | `MaxConcurrentRuns` across instances, and the `BlobContainerUri` that holds the slots |
| `MaxWaitForRunSlot` | 2 minutes | Longest time a run waits for a slot |
| `AgentCacheDuration` | 30 seconds | How long a resolved version is reused; `0` turns caching off |
| `VersionPins` | None | The version this environment runs, per agent |
| `ProtectedVersions` | None | Versions pruning must keep, per agent |
| `KeepLatestVersions` | None | Versions kept each time one is created (minimum 2) |
| `ExternallyManagedAgents` | None | Agents from a provisioning job, with their versions; optional `Endpoint` and `Authentication` |
| `ResponseFormatKey` | None | A system prompt appended to every agent's instructions |
| `ResponseFormatExemptPromptTypes` | None | Prompt keys that `ResponseFormatKey` isn't appended to |
| `RequireTokenUsageTelemetry` | `true` | Fails startup when token metrics aren't recorded |
| `ValidateAgentToolsAtStartup` | `true` | Fails startup if a pinned or external agent's tools can't run here |
| `EnableDriftDetection` | `false` | Warns when a pinned version no longer matches its definition |
| `MaxRetries` | 3 | Foundry client retries. A retried call may be billed twice |

## Production checklist

- [ ] `ApplicationName` is set, and matches the name passed to `AddService(...)`.
- [ ] Secrets come from Key Vault (or you use managed identities), and each identity has only its own role.
- [ ] Telemetry is arriving in Application Insights.
- [ ] In a shared project, staging and production pin their agents, and those pins are in `ProtectedVersions`.
- [ ] `KeepLatestVersions` is set wherever versions are created.
- [ ] `RunTimeout`, `MaxOutputTokensPerRun`, `MaxConcurrency` and `GlobalConcurrency` fit your Foundry quota.
- [ ] Production uses a fixed model version, and important agents have a release gate.
- [ ] With add-ons: MCP allow-lists are set, every search is filtered, and every deployment has a guardrail.

## Testing your app

- **Unit tests:** substitute `IAgentService`.
- **Tests that start the host:** set `"RequireTokenUsageTelemetry": false`.
- **Lower-level services:** `IAgentRunnerService` runs an `AgentSpec` or `AgentReference` directly. `IAgentRuntimeService` handles
  temporary agents and orphan clean-up. `IAgentFactory` maintains versions, e.g. pruning them by hand.

Not supported yet: human approval before a tool runs, streaming, and built-in health checks.

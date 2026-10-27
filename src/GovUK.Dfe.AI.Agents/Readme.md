# GovUK.Dfe.AI.Agents

Run Azure AI Foundry agents from .NET: one at a time, streamed, in parallel or in sequence. Every run is checked,
size-limited and concurrency-limited, and records its tokens and cost. Nothing is stored in Foundry.

## Packages

Install core, plus only the add-ons you need.

| Package                                                                                                                               | Adds                                                     | Register with             |
| ------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------- | ------------------------- |
| `GovUK.Dfe.AI.Agents`                                                                                                                 | Agents, versions, runs, answer checks, limits, telemetry | `AddAgents(...)`          |
| [`GovUK.Dfe.AI.Agents.Mcp`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Mcp/Readme.md)               | Tools from your own MCP servers                          | `.AddMcpServers()`        |
| [`GovUK.Dfe.AI.Agents.AISearch`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.AISearch/Readme.md)     | Evidence from Azure AI Search, with citations            | `.AddAISearch()`          |
| [`GovUK.Dfe.AI.Agents.Evaluation`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Evaluation/Readme.md) | A judge model that scores answers                        | `.AddQualityEvaluation()` |
| [`GovUK.Dfe.AI.Agents.Guardrails`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Guardrails/Readme.md) | Foundry guardrails on your models or agents              | `.AddGuardrails()`        |

## What you need

| Need                        | Details                                                                                                                                                                      |
| --------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| .NET 10                     | The packages target `net10.0`                                                                                                                                                |
| An Azure AI Foundry project | With a model deployment, or a connection to one, e.g. `myconnection/gpt-5.1`                                                                                                 |
| An identity                 | The app's managed identity or a service principal, with **Foundry User** on the project ([roles](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Access.md#roles)) |
| Somewhere for telemetry     | Application Insights or any OpenTelemetry backend: startup fails unless token usage is recorded                                                                              |

Each add-on's readme lists anything more it needs.

## Quick start

### 1. Install

```sh
dotnet add package GovUK.Dfe.AI.Agents
dotnet add package Azure.Monitor.OpenTelemetry.AspNetCore   # sends token usage to Application Insights
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
    "PromptFiles": {
      "SystemPrompts": { "Ofsted": "Prompts/Ofsted.md", "Synthesis": "Prompts/Synthesis.md" }
    },
    "RunTimeout": "00:02:00",
    "MaxConcurrency": 4
  }
}
```

- Give the identity the **Foundry User** role on the Foundry project ([all roles](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Access.md#roles)).
- Load `Authentication:ClientSecret` from Key Vault, or use [managed identities](#credentials-and-roles).
- Set each prompt file to _Copy to output directory_.

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

A missing or invalid setting fails startup with one error listing every problem.

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

Instructions go in `prompt`. Untrusted material (documents, search results, other agents' output) goes in `evidence`,
fenced as data so it can't act as an instruction.

`AddAgents` and every add-on's `Add…()` are in `Microsoft.Extensions.DependencyInjection`. The types you'll use most:

| `using GovUK.Dfe.AI.Agents…`                     | For                                                                                                      |
| ------------------------------------------------ | -------------------------------------------------------------------------------------------------------- |
| `.ValueObjects`                                  | `AgentDefinition`, `AgentOutputSchema`, `AgentResult`, `AgentEvidence`, `AgentStreamUpdate`, `AgentSpec` |
| `.Services.Interfaces`                           | `IAgentService` (and the lower-level `IAgentRunnerService`, `IAgentRuntimeService`)                      |
| `.Extensions`                                    | `ReadOutputAs<T>()`, `ToTokenUsageSummary()`                                                             |
| `.Tools` / `.Tools.Interfaces`                   | `AgentTool` / `IAgentToolProvider`, `IToolCallApprover`                                                  |
| `.Privacy`                                       | `PatternRedactor`, `IAgentInputRedactor`                                                                 |
| `.ReleaseTesting` / `.ReleaseTesting.Interfaces` | `AgentTestCase`, `AgentEvaluationReport`, `ReleaseSections` / `IAgentTestRunner`                         |
| `.Quality.Interfaces`                            | `IAgentRunEvaluator`, which scores answers, live and in the release gate                                 |
| `.Context` / `.Extensibility.Interfaces`         | `AgentContext`, `IContextRetriever` / `IAgentRunObserver`, `IAgentsPackage`, `IGuardrailCheck`           |

## What happens in a run

1. **Version:** each definition becomes an agent version in Foundry; unchanged definitions reuse theirs.
2. **Slot:** the run waits for a free concurrency slot.
3. **Send:** prompt and evidence are redacted, evidence is cut to size and fenced, and both are sent. Calls are
   stateless: the run's history stays in your app.
4. **Tools:** tool calls run in your app with your credential; Foundry never sees a tool's address or credential.
5. **Check:** the answer is checked and cleaned; a failed check gets one retry.
6. **Record:** tokens, cost, duration and any error type go to telemetry.

## Running agents

| `IAgentService` method                                                        | Use for                                         | When an agent fails                                          |
| ----------------------------------------------------------------------------- | ----------------------------------------------- | ------------------------------------------------------------ |
| `RunAsync(definition, prompt, evidence)`                                      | One agent                                       | Throws                                                       |
| `RunStreamingAsync(definition, prompt, evidence)`                             | One agent, its answer shown as it's written     | Throws at the end                                            |
| `RunParallelAsync(definitions, resolvePrompt, context, resolveEvidence: ...)` | Independent agents                              | It gets a fallback result; the others carry on               |
| `RunSequentialAsync(definitions, resolvePrompt, initialInput, context)`       | A chain, e.g. draft then review                 | It gets a fallback; the next agent gets the last good output |
| `ProvisionAsync(definitions)`                                                 | A [provisioning job](#centrally-managed-agents) | Throws                                                       |

- Results come back in the order of the definitions; `results.ToTokenUsageSummary()` totals tokens and cost.
- Pass `shouldSuppress: _ => false` to throw instead of returning fallbacks (this cancels the other agents).

| `AgentDefinition` property      | Meaning                                                                                                                                                  |
| ------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Name`                          | The agent's name in Foundry; unique per app if the project is shared                                                                                     |
| `SystemPromptKey`               | Its key under `PromptFiles:SystemPrompts`                                                                                                                |
| `IsManagedAgent`                | `true` (default): kept and reused. `false`: created and deleted on every run                                                                             |
| `AllowedTools`                  | The only tools it may call. Empty (default): none                                                                                                        |
| `ToolsRequiringApproval`        | Tools that run only once a person approves ([how](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/ResponsibleAI.md#a-person-approves-changes)) |
| `OutputSchema`                  | A JSON schema for a typed answer; read it with `ReadOutputAs<T>()`                                                                                       |
| `Validate`, `RequiredCitations` | [Answer checks](#answer-checks)                                                                                                                          |

### Streaming

For a chat screen, show the answer as it's written:

```csharp
await foreach (var update in agents.RunStreamingAsync(BriefingAgents.Synthesis, question, evidence, ct))
{
    if (update.Text is { } text) await response.WriteAsync(text, ct);   // the next piece, already cleaned
    if (update.Result is { } result) LogCost(result);                    // last: tokens, cost, RunId
}
```

- Same slots, limits, tools, redaction, cleaning and telemetry as `RunAsync`. Stop reading to cancel.
- Checks run at the end; the answer is already shown, so a failed check throws with no retry.
- Single runs only. For a chain, run the earlier agents with `RunSequentialAsync`, then stream the last.

### Tools and other agents

- **Web search:** `agents.AddTools("news-agent", new WebSearchToolProvider())`, biased towards London by default. For
  a different town per run, use a temporary agent:

  ```csharp
  var tools = await new WebSearchToolProvider(WebSearchLocation.ForCity(trust.Town)).GetToolsAsync(ct);   // IAgentRuntimeService runtime
  var news = await runtime.RunEphemeralAsync(
      new AgentSpec { Name = "trust-news", Instructions = "Find recent local news about the trust and its schools. Cite each source.", Tools = tools },
      $"Recent news about {trust.Name} in {trust.Town}.", cancellationToken: ct);
  ```

- **Your own tools:** implement `IAgentToolProvider` returning `AgentTool.Function(name, description, jsonSchema)`, and
  add it with `agents.AddTools(...)`; your app runs the calls. For MCP servers, use the Mcp add-on.
- **Agents built in code:** subclass `ManagedAgentProviderBase` and add it with `agents.AddAgentProvider<T>()`.
- **User prompt templates:** `PromptFiles:UserPrompts` files with `{{Name}}` placeholders, filled with
  `IPromptTemplateBuilder.Build("Key", values)`. Values aren't fenced: put untrusted text in evidence.

## Answer checks

`Validate` returns why an answer is wrong, or `null` if it's fine. A failed check is sent back once with the reason;
if it fails again, the run fails.

```csharp
public static readonly AgentDefinition Ofsted = new("ofsted-agent", "Ofsted")
{
    OutputSchema = AgentOutputSchema.For<OfstedFindings>("ofsted_findings"),
    Validate = r => r.ReadOutputAs<OfstedFindings>().Rating is "Outstanding" or "Good" or "Requires improvement" or "Inadequate"
        ? null : "Rating must be an Ofsted grade.",
};
```

### Citations

When evidence is numbered, as search results are, `RequiredCitations` decides what readers see:

| `RequiredCitations` | The model must                                    | Readers see                                                        |
| ------------------- | ------------------------------------------------- | ------------------------------------------------------------------ |
| `true` (default)    | Cite as `[Evidence n]`, only evidence that exists | `[link to the source]`, or `[its text]` when it has no web address |
| `false`             | Nothing                                           | No citations: any the model writes are removed                     |

- Pass a search result itself as evidence (`evidence: searchResult`, not `searchResult.Text`) so its sources come too.
- Citations change only after the checks; `Validate` and run observers still see `[Evidence n]`.
- Your own evidence can carry sources: `new AgentEvidence(text, [new EvidenceSource(1, "Trust record", link)])`.

### Every answer is cleaned

So it's safe to show as Markdown or HTML:

- **Images become their alt text:** a browser loads an image without a click, which could leak data.
- **HTML the model writes is shown as text** (`<script>` becomes `&lt;script>`). A `<` that can't start a tag, as in
  "below < 90%", is left alone. Citation links are the only real HTML.
- **Links without a full address become text,** as they'd 404 in your app. Full `https://` and `mailto:` links stay:
  show each link's domain, so users see where it goes.

## Release gate

Test agents before they go live with the Evaluation add-on's standard release check, the same in every app:

```csharp
agents.AddGuardrails().AddQualityEvaluation().AddReleaseGate();   // registration

using var run = releaseGate.BeginRun();                            // one id on the whole release's metrics
var results = await releaseGate.CheckReleaseAsync(MyAgents.All, ct);
if (results.All(r => r.Passed)) { /* provision or deploy */ }    // the reasons are logged otherwise
```

It checks the guardrail, runs each agent's test cases on a temporary copy, and logs and records the result to one
standard. See the [Evaluation readme](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Evaluation/Readme.md#release-gate).

Test cases are JSON files under `TestCases/<agent>/`, in one of two folders:

| Folder        | Case                                                        | Passes when                                   |
| ------------- | ----------------------------------------------------------- | --------------------------------------------- |
| `guardrail/`  | An attack, with `"guardrailMustBlock": true` and no `group` | The guardrail blocks it                       |
| `evaluation/` | A question with facts to check; no guardrail flag           | The answer is right, and scores meet the gate |

```json
{
  "prompt": "Summarise URN 100000.",
  "evidence": "Rated Good in March 2024.",
  "mustMention": [ "Good", "2024" ],
  "mustNotMention": [ "Outstanding" ],
  "group": "special-schools"
}
```

- **Check your files in a unit test** (`GovUK.Dfe.AI.Agents.ReleaseTesting`), so a wrong one fails the build, not a release:

  ```csharp
  var cases = await AgentTestCase.LoadAsync("TestCases/ofsted-agent");
  Assert.Empty(cases.SelectMany(ReleaseSections.Problems));
  ```

- **`group`** puts evaluation cases in groups whose scores are compared (`MaxGroupGap`), e.g. `special-schools`.
- **Your own fields** are kept in `testCase.Extra`.
- **Your own gate:** `IAgentTestRunner` runs cases and `AgentEvaluationReport.FailuresAgainst` judges the report, for
  tools that don't release agents.

## Responsible AI

Hooks for the UK Government AI Playbook: every model call through a connection you control, a person approving changes,
personal data removed before the model sees it, and every answer traceable. See
[Responsible AI](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/ResponsibleAI.md).

## Environments and versions

- **A Foundry project per environment (simplest):** leave versions unset; each environment builds its own agents.
- **One shared project:** dev creates versions; staging and production pin the tested one with
  `"VersionPins": { "ofsted-agent": "3" }`. `"EnableDriftDetection": true` warns when a pin no longer matches.
- Either way, fix the model version in production, and prune with `"KeepLatestVersions": 3`. Pinned versions and
  `ProtectedVersions` are never deleted.

### Centrally managed agents

1. A provisioning job calls `await agents.ProvisionAsync(BriefingAgents.All)`.
2. Each app lists the versions it runs, and needs no prompt files for them (`"latest"`: dev only):

   ```json
   "ExternallyManagedAgents": { "ofsted-agent": "4", "trust-agent": "2" }
   ```

3. For another Foundry project, add its `Endpoint` and, optionally, an `Authentication` block.
4. Apps still run the tools, and check at startup that they can run every tool those versions call.

## Scaling and cost

- **Concurrency:** `MaxConcurrency` per instance, or `GlobalConcurrency` across instances (a blob container holds the
  slots; the identity needs **Storage Blob Data Contributor**). A run waits up to `MaxWaitForRunSlot`.

  ```json
  "GlobalConcurrency": { "MaxConcurrentRuns": 20, "BlobContainerUri": "https://<account>.blob.core.windows.net/aiagents-run-slots" }
  ```

- **Cost cap:** `MaxOutputTokensPerRun` caps output tokens per run, reasoning and tool rounds included.
- **Cost:** add each model's prices per 1,000 tokens (`Currency` defaults to `GBP`). `result.Cost` is one run;
  `results.ToTokenUsageSummary().Cost` several. `gpt-5.1` also matches `gpt-5.1-2025-11-13` and `my-connection/gpt-5.1`.

  ```json
  "Pricing": {
    "Models": { "gpt-5.1": { "CostPer1kTokensInput": 0.001, "CostPer1kTokensCachedInput": 0.0001, "CostPer1kTokensOutput": 0.008 } }
  }
  ```

- **Temporary agents left by a crash:** `agents.AddEphemeralAgentSweep()` deletes this app's old ones every 30 minutes.

## Limits and telemetry

- **Limits:** output tokens, evidence, tool output, tool rounds, time and concurrency are capped, and rate limits (429)
  are retried. Defaults suit gpt-5.1. See [Limits](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Limits.md).
- **Telemetry:** tokens, cost, duration, tool calls and remaining tokens, as OpenTelemetry metrics and traces, never
  with prompts or answers. See [Telemetry](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Telemetry.md).
- **Logs:** a failed run logs its exception, which can include parts of the request. Limit log access and retention.
- **Release metrics:** the guardrail check, release-gate results and the version provisioned, the same in every app and
  grouped by release. They show, per agent, whether it went live and if not, why. See
  [Release metrics](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Telemetry.md#release-metrics)
  for the metrics and the standard queries.

### Your own metrics

Record your app's own events with `AgentCounter`, `AgentHistogram` and `IAgentMetrics`, so they're named and tagged like
the library's and never mix with another app's. See [Your own metrics](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Telemetry.md#your-own-metrics).

## Credentials and roles

`Authentication` is the default identity, or set one in code, e.g. `agents.UseCredential(new ManagedIdentityCredential())`.
Any service can have its own. Every app needs **Foundry User** on the Foundry project; each add-on adds its own role.
See [Access](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Access.md) for every package's credentials and roles.

## Options

All under `AiAgents`, or in code with `agents.Configure(o => ...)`. Only `Foundry:Endpoint` and `Foundry:DefaultModel` are
required. See [Settings](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/docs/Settings.md) for every setting and its default.

## Testing your app

Substitute `IAgentService` in unit tests, matching evidence with `Arg.Any<AgentEvidence?>()`:

```csharp
agents.RunAsync(BriefingAgents.Ofsted, Arg.Any<string>(), Arg.Any<AgentEvidence?>(), Arg.Any<CancellationToken>())
    .Returns(new AgentResult { AgentName = "ofsted-agent", Output = """{"rating":"Good","strengths":[]}""" });
```

Tests that start the host set `"RequireTokenUsageTelemetry": false`.

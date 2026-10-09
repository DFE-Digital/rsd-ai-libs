# GovUK.Dfe.AI.Agents

Run Azure AI Foundry agents from .NET: one at a time, in parallel or in sequence. Every run is checked, size-limited,
concurrency-limited and records its token usage and cost.

## Packages

Install core, plus only the add-ons you need.

| Package                                                                                                                               | Adds                                                     | Register with             |
| ------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------- | ------------------------- |
| `GovUK.Dfe.AI.Agents`                                                                                                                 | Agents, versions, runs, answer checks, limits, telemetry | `AddAgents(...)`          |
| [`GovUK.Dfe.AI.Agents.Mcp`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Mcp/readme.md)               | Tools from your own MCP servers                          | `.AddMcpServers()`        |
| [`GovUK.Dfe.AI.Agents.AISearch`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.AISearch/readme.md)     | Evidence from Azure AI Search                            | `.AddAISearch()`          |
| [`GovUK.Dfe.AI.Agents.Evaluation`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Evaluation/readme.md) | A judge model that scores answers                        | `.AddQualityEvaluation()` |
| [`GovUK.Dfe.AI.Agents.Guardrails`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Guardrails/readme.md) | Foundry guardrails on your model deployments             | `.AddGuardrails()`        |

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
      "SystemPrompts": {
        "Ofsted": "Prompts/Ofsted.md",
        "Synthesis": "Prompts/Synthesis.md"
      }
    },
    "RunTimeout": "00:02:00",
    "MaxConcurrency": 4
  }
}
```

- Give the identity the **Foundry User** role on the project (see [Roles](#roles) for the rest).
- Load `Authentication:ClientSecret` from Key Vault, or use [managed identities](#credentials).
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
which is fenced as data so text inside it can't act as an instruction.

### Namespaces

`AddAgents` and every add-on's `Add…()` are in `Microsoft.Extensions.DependencyInjection`, so registering needs no usings.

| `using GovUK.Dfe.AI.Agents…`                              | For                                                                                                                                             |
| --------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| `.ValueObjects`                                           | `AgentDefinition`, `AgentOutputSchema`, `AgentResult`, `AgentStreamUpdate`, `AgentSpec`, `AgentEvidence`, `EvidenceSource`, `CompletedAgentRun` |
| `.Services.Interfaces`                                    | `IAgentService`, `IAgentRunnerService`, `IAgentRuntimeService`                                                                                  |
| `.Extensions`                                             | `ReadOutputAs<T>()`, `ToTokenUsageSummary()`                                                                                                    |
| `.Builders`                                               | `AgentsBuilder`                                                                                                                                 |
| `.Context` / `.Context.Interfaces`                        | `AgentContext` / `IContextRetriever`                                                                                                            |
| `.Filters`                                                | `ODataFilter`                                                                                                                                   |
| `.Quality` / `.Quality.Interfaces`                        | `AgentTestCase`, `AgentEvaluationReport` / `IAgentTestRunner`, `IAgentRunEvaluator`                                                             |
| `.Extensibility.Interfaces`                               | `IAgentsPackage`, `IAgentRunObserver`                                                                                                           |
| `.Tools` / `.Tools.Interfaces` / `.Privacy`               | `AgentTool` / `IAgentToolProvider`, `IToolCallApprover` / `PatternRedactor`, `IAgentInputRedactor`                                              |
| `.Providers` / `.Tools.WebSearch` / `.Prompts.Interfaces` | `ManagedAgentProviderBase` / `WebSearchToolProvider` / `IPromptTemplateBuilder`                                                                 |
| `.Enums` / `.Diagnostics` / `.Exceptions`                 | `AzureCredentialTarget`, `AgentTestTarget` / `AgentTelemetry` / `AgentGuardrailException`                                                       |

## What happens in a run

1. **Version:** each definition becomes an agent version in Foundry. Unchanged definitions reuse their version.
2. **Slot:** the run waits for a free concurrency slot.
3. **Prompt:** prompt and evidence are redacted, evidence is cut to size and fenced, then both are sent.
4. **Tools:** tool calls run in your app with your credential. Foundry never sees a tool's address or credential.
5. **Checks:** the answer is checked and cleaned. A failed check gets one retry.
6. **Record:** tokens, cost, duration and any error type are recorded. Nothing is stored in Foundry: each call is
   stateless (`store: false`), and the run's history stays in your app.

## Running agents

| `IAgentService` method                                                        | Use for                                         | When an agent fails                                          |
| ----------------------------------------------------------------------------- | ----------------------------------------------- | ------------------------------------------------------------ |
| `RunAsync(definition, prompt, evidence)`                                      | One agent                                       | Throws                                                       |
| `RunStreamingAsync(definition, prompt, evidence)`                             | One agent, its answer shown as it's written     | Throws at the end                                            |
| `RunParallelAsync(definitions, resolvePrompt, context, resolveEvidence: ...)` | Independent agents                              | It gets a fallback result; the others carry on               |
| `RunSequentialAsync(definitions, resolvePrompt, initialInput, context)`       | A chain, e.g. draft then review                 | It gets a fallback; the next agent gets the last good output |
| `ProvisionAsync(definitions)`                                                 | A [provisioning job](#centrally-managed-agents) | Throws                                                       |

- Results come back in the order of the definitions. `results.ToTokenUsageSummary()` totals their tokens and cost.
- Pass `shouldSuppress: _ => false` to throw instead of returning fallbacks; this also cancels the other agents.

| `AgentDefinition` property      | Meaning                                                                          |
| ------------------------------- | -------------------------------------------------------------------------------- |
| `Name`                          | The agent's name in Foundry; unique per app if the project is shared             |
| `SystemPromptKey`               | Its key under `PromptFiles:SystemPrompts`                                        |
| `IsManagedAgent`                | `true` (default): kept and reused. `false`: created and deleted on every run     |
| `AllowedTools`                  | The only tools it may call. Empty (default): none                                |
| `ToolsRequiringApproval`        | Tools that run only once a person approves ([below](#a-person-approves-changes)) |
| `OutputSchema`                  | A JSON schema for a typed answer; read it with `ReadOutputAs<T>()`               |
| `Validate`, `RequiredCitations` | [Answer checks](#answer-checks)                                                  |

### Streaming an answer

For a chat screen, show the answer as it's written:

```csharp
await foreach (var update in agents.RunStreamingAsync(BriefingAgents.Synthesis, question, evidence, ct))
{
    if (update.Text is { } text) await response.WriteAsync(text, ct);   // the next piece, already cleaned
    if (update.Result is { } result) LogCost(result);                    // last: tokens, cost, RunId
}
```

- Everything else is the same as `RunAsync`: run slots, timeout, token cap, tools, redaction, telemetry and clean-up.
- Images are removed before text is passed on. Text after a possible image or link waits until it's complete.
- [Answer checks](#answer-checks) run once the answer is complete. As it's already shown, a failed check throws at the
  end with no retry: tell the user the answer can't be used.
- Stop reading (or cancel) to end the run.
- Single runs only. For a chain, run the earlier agents with `RunSequentialAsync`, then stream the last one.

### More ways to add agents and tools

- **Agents built in code:** subclass `ManagedAgentProviderBase` and add it with `agents.AddAgentProvider<T>()`.
- **Other tools:** `agents.AddTools("news-agent", new WebSearchToolProvider())`. For your own, implement
  `IAgentToolProvider` and return `AgentTool.Function(name, description, jsonSchema)`; your app runs the calls.
- **User prompt templates:** add them under `PromptFiles:UserPrompts` with `{{Name}}` placeholders, and fill one with
  `IPromptTemplateBuilder.Build("Key", values)`.
- **Local news, e.g. about a trust:** web search is biased towards a location (default London). For a different town
  per run, use a temporary agent:

  ```csharp
  var tools = await new WebSearchToolProvider(WebSearchLocation.ForCity(trust.Town)).GetToolsAsync(ct);   // IAgentRuntimeService runtime
  var news = await runtime.RunEphemeralAsync(
      new AgentSpec { Name = "trust-news", Instructions = "Find recent local news about the trust and its schools. Cite each source.", Tools = tools },
      $"Recent news about {trust.Name} in {trust.Town}.", cancellationToken: ct);
  ```

  The location only biases results, so name the town in the prompt too.

## Answer checks

- **Citations (on by default):** when evidence is numbered, as search results are, the answer must cite it as
  `[Evidence n]` and only cite evidence that exists. Set `RequiredCitations = false` when there's nowhere to cite.
- **Citations readers can use:** pass a search result (`ContextResult`) as evidence, and each `[Evidence n]` in the
  checked answer becomes a link to its source, or its text when it has no web address. With `RequiredCitations = false`,
  citations aren't asked for and any the model writes are removed. Your own evidence can carry sources too:
  `new AgentEvidence(text, [new EvidenceSource(1, "Trust record", link)])`.
- **Your own rules:** `Validate` returns why the answer is wrong, or `null` if it's fine.
- **Retry:** a failed check is sent back once, with the run so far. If it fails again, the run fails.

```csharp
public static readonly AgentDefinition Ofsted = new("ofsted-agent", "Ofsted")
{
    OutputSchema = AgentOutputSchema.For<OfstedFindings>("ofsted_findings"),
    Validate = r => r.ReadOutputAs<OfstedFindings>().Rating is "Outstanding" or "Good" or "Requires improvement" or "Inadequate"
        ? null : "Rating must be an Ofsted grade.",
};
```

Every answer is also cleaned, so it's safe to show as Markdown or HTML:

- **Images become their alt text.** A browser loads an image without a click, so an injected image address could
  leak data. To show images, render them in your app from your data.
- **Links without a full address become text,** e.g. `[the report](files/report.pdf)`, as they'd 404 in your app.
- **Full `https://` and `mailto:` links are kept.** Show each link's domain in your app, so users see where it goes.
- **HTML the model writes is shown as text,** e.g. `<script>` becomes `&lt;script>`, so injected markup can't run in an
  app that renders HTML. A `<` that can't start a tag, as in "below < 90%", is left alone.

## Release gate

Run fixed test cases in CI and fail the build if an agent got worse:

```csharp
var tests = services.GetRequiredService<IAgentTestRunner>();
var cases = await AgentTestCase.LoadAsync("tests/ofsted-agent");   // JSON: prompt, evidence, mustMention, mustNotMention, group
var report = await tests.RunAsync(BriefingAgents.Ofsted, cases, repeats: 3);
var baseline = JsonSerializer.Deserialize<AgentEvaluationReport>(await File.ReadAllTextAsync("baseline.json"))!;

var failures = report.FailuresAgainst(new ReleaseGate
{
    Metrics = ["Groundedness", "Relevance"],   // must be scored
    MinimumScore = 3.5,                        // lowest average allowed (scores run 1 to 5)
    Tolerance = 0.2,                           // how far below the baseline a metric may fall
    MaxGroupGap = 0.5,                         // optional: largest gap between test case groups
}, baseline);

if (failures.Count > 0)
{
    throw new InvalidOperationException("ofsted-agent isn't ready:\n" + string.Join("\n", failures));
}
```

Each failure is a readable line, e.g. `Groundedness: fell from 4.5 to 4.2, more than the tolerance 0.2`.

- **Tolerance** (default `0.2`) stops a judge's run-to-run noise failing a release; a real drop still fails.
- **Repeats** average each case's scores over several runs. Facts must be right in every run. Each repeat costs a run.
- **Safe:** cases run against a temporary copy of the agent. Pass `AgentTestTarget.Deployed` to test the live version.
- **Scores need an evaluator:** the Evaluation add-on, or your own `IAgentRunEvaluator`. Without one, only
  `mustMention` and `mustNotMention` are checked, and every metric fails as not scored.
- **First release:** pass no baseline. After a release passes, save its report as `baseline.json`.

## Responsible AI

Hooks for the [UK Government AI Playbook](https://www.gov.uk/government/publications/ai-playbook-for-the-uk-government).
Your app owns the decisions, the user interface, the DPIA and the ATRS record.

### A person approves changes

```csharp
public static readonly AgentDefinition Case = new("case-agent", "Case")
{
    AllowedTools = ["get_case", "update_case"],
    ToolsRequiringApproval = ["update_case"],
};

builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddToolApprover<ManagerApprover>());

public sealed class ManagerApprover(IApprovalQueue queue) : IToolCallApprover   // your own service
{
    public async Task<ToolCallApproval> ApproveAsync(string agentName, ToolCallRequest call, CancellationToken ct)
        => await queue.AskAsync(agentName, call.FunctionName, call.Arguments, ct)
            ? ToolCallApproval.Approve()
            : ToolCallApproval.Deny("the manager declined");
}
```

- A denied call doesn't run; the agent is told why and answers without it.
- The run waits for the decision, within `RunTimeout`. For decisions that take hours, end the run and start a new one.
- Startup fails if a tool needs approval but there's no approver, or it isn't in `AllowedTools`.

### Personal data is removed before the model sees it

```csharp
agents.AddRedactor(new PatternRedactor(new Dictionary<string, string>
{
    ["UPN"] = @"\b[A-Z]\d{11}[0-9A-Z]\b",
    ["NI number"] = @"\b[A-CEGHJ-PR-TW-Z]{2}\d{6}[A-D]\b",
}));
```

- Every prompt, evidence and tool output is redacted before it's sent, e.g. `Pupil [UPN removed]`, and so are the
  copies given to evaluators and observers.
- Patterns can't find names. Leave name fields out of search results and tool output, or implement
  `IAgentInputRedactor`, e.g. to remove names your app already holds.
- If a redactor fails, the run fails, so nothing is sent unredacted.
- Nothing is stored in Foundry, so there's nothing to delete afterwards.

### Every answer can be traced

Each `AgentResult` has a `RunId` and `CompletedAt`; the run ID is also on the trace (`dfe.ai_agents.run_id`).

- **Label answers** as AI-generated, e.g. "Generated by AI on 5 October 2026. Check before use."
- **Audit:** an `IAgentRunObserver` gets each `CompletedAgentRun` (prompt, evidence and answer, already redacted) to
  save in your own store.
- **Feedback:** store the user's verdict with the `RunId`, `AgentName`, `AgentVersion` and `Model`.

### Agents are checked for fairness

Give release-gate test cases a `group` (e.g. `"special-schools"`) and set `MaxGroupGap`, or check on its own:

```csharp
var gaps = report.GroupGaps(maxGap: 0.5, "Groundedness", "Relevance");
```

## Environments and versions

Choose one setup:

- **A Foundry project per environment (simplest):** leave versions unset. Each environment builds its agents from its own
  prompt files, so production needs write access to its project.
- **One shared project:** dev creates versions; staging and production pin the tested one with
  `"VersionPins": { "ofsted-agent": "3" }`. A pinned agent never creates or prunes versions.
  `"EnableDriftDetection": true` warns when a pin no longer matches its definition.

Either way:

- **Fix the model version in production.** An agent version fixes the prompt and tools, not the model. Each result's
  `Model` shows which model answered.
- **Prune old versions** with `"KeepLatestVersions": 3` (minimum 2). Pinned and `ProtectedVersions` are never deleted.

### Centrally managed agents

1. A provisioning job calls `await agents.ProvisionAsync(BriefingAgents.All)` to create or reuse each version.
2. Each app lists the agents and versions it runs, and needs no prompt files for them (`"latest"`: dev only):

   ```json
   "ExternallyManagedAgents": { "ofsted-agent": "4", "trust-agent": "2" }
   ```

3. For agents in another Foundry project, add its `Endpoint` and optionally an `Authentication` block.
4. Apps still run the tools, and check at startup that they can run every tool their agent versions call.

An agent can be in `VersionPins` or `ExternallyManagedAgents`, not both.

## Scaling and cost

- **Concurrency:** `MaxConcurrency` limits runs per instance. `GlobalConcurrency` limits runs across instances, using a
  blob container. A run waits up to `MaxWaitForRunSlot`, then throws `TimeoutException`.

  ```json
  "GlobalConcurrency": { "MaxConcurrentRuns": 20, "BlobContainerUri": "https://<account>.blob.core.windows.net/aiagents-run-slots" }
  ```

  The identity needs **Storage Blob Data Contributor** on the account (the library creates the container) or on the
  container. HTTPS and Entra ID only; a SAS URI fails startup. For another store, e.g. Redis, implement `IRunSlotStore`.

- **Cost cap:** `MaxOutputTokensPerRun` caps output tokens per run, including reasoning, tool rounds and the retry.
- **Cost:** add each model's prices per 1,000 tokens:

  ```json
  "Pricing": {
    "Currency": "GBP",
    "Models": {
      "gpt-5.1": { "CostPer1kTokensInput": 0.001, "CostPer1kTokensCachedInput": 0.0001, "CostPer1kTokensOutput": 0.008 }
    }
  }
  ```

  `result.Cost` is one run's cost; `results.ToTokenUsageSummary().Cost` totals several, e.g. one briefing. Failed runs
  count, as Foundry bills them. `gpt-5.1` also matches `gpt-5.1-2025-11-13` and `my-connection/gpt-5.1`. Cached input
  tokens use `CostPer1kTokensCachedInput`, else the input price. A model with no price reports tokens only.

- **Orphaned agents:** a crash can leave temporary agents behind. `agents.AddEphemeralAgentSweep()` deletes this app's
  old ones every 30 minutes, and is safe on every instance. Keep `ApplicationName` unique per app.

## Limits and telemetry

- **Limits:** each run's output tokens, evidence, tool output, tool rounds, time and concurrency are capped, and rate
  limits (429) are retried. Defaults suit gpt-5.1. See [Limits](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/docs/limits.md) for every default and Foundry's
  own limits.
- **Telemetry:** token usage, cost, duration, tool calls and remaining tokens are recorded as OpenTelemetry metrics and
  traces, never with prompts or answers. Startup fails unless they're subscribed (see [Register](#3-register)); for tests,
  set `"RequireTokenUsageTelemetry": false`. See [Telemetry](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/docs/telemetry.md) for every metric and example
  queries.
- **Logs** are different: a failed run logs its exception, which can include parts of the request. Limit who can read
  logs and how long they're kept.

## Credentials

`Authentication` is the default identity. Any service can have its own `Authentication` block: `Foundry`,
`GlobalConcurrency`, `ExternallyManagedAgents` and each add-on's section. Load secrets from Key Vault, e.g.
`AiAgents--Foundry--Authentication--ClientSecret`.

For managed identities, set credentials in code:

```csharp
agents.UseCredential(new ManagedIdentityCredential())                     // default for every service
      .UseCredentialFor(AzureCredentialTarget.Foundry, foundryCredential)   // one core service
      .UseExternallyManagedAgentsCredential(centralCredential);           // the central agents' project
```

Add-ons have `UseMcpCredential(server, ...)`, `UseAISearchCredential(...)` and `UseGuardrailsCredential(...)`.

A service uses its code credential, else its own `Authentication` block, else the default. Give each identity only the
role its service needs.

### Roles

| Role                           | Scope                                       | Needed by                                               | Why                                                                                            |
| ------------------------------ | ------------------------------------------- | ------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| Foundry User                   | Foundry project                             | Every app, and a provisioning job                       | Run agents, create versions, call the Evaluation judge model                                   |
| Foundry User                   | The central Foundry project                 | Apps using `ExternallyManagedAgents` with an `Endpoint` | Run the central agents                                                                         |
| Storage Blob Data Contributor  | Storage account, or just the container      | Apps using `GlobalConcurrency`                          | Hold run slots across instances (on the account, the library creates the container)            |
| Search Index Data Reader       | Search service                              | Apps using AISearch                                     | Read evidence from the indexes                                                                 |
| The MCP server's app role      | The server's app registration (its `Scope`) | Apps using Mcp                                          | Call the server's tools; the server's owner grants it                                          |
| Reader                         | Foundry resource                            | Apps using Guardrails                                   | Check at startup that each deployment has the guardrail                                        |
| Cognitive Services Contributor | Foundry resource                            | Only a job that calls `ApplyAsync`                      | Create the guardrail and attach it to deployments. Not needed if Bicep or Terraform creates it |

Roles are assigned in Azure (Access control (IAM)) to each identity, e.g. the app's managed identity.

## Options

All under `AiAgents`, or in code with `agents.Configure(o => ...)`.

| Setting                           | Default             | Purpose                                                                                                        |
| --------------------------------- | ------------------- | -------------------------------------------------------------------------------------------------------------- |
| `ApplicationName`                 | Entry assembly name | Telemetry tag, and scope of the orphan sweep                                                                   |
| `RunTimeout`                      | None                | Longest time one run may take                                                                                  |
| `MaxOutputTokensPerRun`           | 64000               | Output tokens one run may use (minimum 16)                                                                     |
| `MaxEvidenceCharacters`           | 200000              | Longer evidence is cut, keeping the start                                                                      |
| `MaxToolOutputCharacters`         | 40000               | Longer tool output is cut                                                                                      |
| `FenceToolOutput`                 | `true`              | Fences tool output as data                                                                                     |
| `MaxConcurrency`                  | None                | Runs at once on one instance                                                                                   |
| `GlobalConcurrency`               | Off                 | `MaxConcurrentRuns` across instances, and the `BlobContainerUri` for slots                                     |
| `MaxWaitForRunSlot`               | 2 minutes           | Longest wait for a slot                                                                                        |
| `AgentCacheDuration`              | 30 seconds          | How long a resolved version is reused; `0` turns caching off                                                   |
| `VersionPins`                     | None                | The version this environment runs, per agent                                                                   |
| `ProtectedVersions`               | None                | Versions pruning must keep, per agent                                                                          |
| `KeepLatestVersions`              | None                | Versions kept each time one is created (minimum 2)                                                             |
| `ExternallyManagedAgents`         | None                | Agents from a provisioning job, with versions; optional `Endpoint`, `Authentication`                           |
| `ResponseFormatKey`               | None                | A system prompt appended to every agent's instructions                                                         |
| `ResponseFormatExemptPromptTypes` | None                | Prompt keys `ResponseFormatKey` isn't appended to                                                              |
| `RequireTokenUsageTelemetry`      | `true`              | Fails startup when token metrics aren't recorded                                                               |
| `ValidateAgentToolsAtStartup`     | `true`              | Fails startup if a pinned or external agent's tools can't run here                                             |
| `EnableDriftDetection`            | `false`             | Warns when a pinned version no longer matches its definition                                                   |
| `Pricing`                         | None                | Prices per model, so results and metrics report cost                                                           |
| `MaxRetries`                      | 6                   | Retries on a rate limit or transient failure (about a minute of throttling); a retried 5xx may be billed twice |
| `LowRemainingTokensPercent`       | 10                  | Threshold for `dfe.ai_agents.tokens.low`; `0` turns it off                                                     |

## Production checklist

- [ ] `ApplicationName` is set, and matches the name passed to `AddService(...)`.
- [ ] Secrets are in Key Vault (or managed identities are used), and each identity has only its own role.
- [ ] Telemetry arrives in Application Insights, and log access and retention fit your data.
- [ ] In a shared project, staging and production pin their agents, and the pins are in `ProtectedVersions`.
- [ ] `KeepLatestVersions` is set wherever versions are created.
- [ ] `RunTimeout`, `MaxOutputTokensPerRun`, `MaxConcurrency` and `GlobalConcurrency` fit your Foundry quota.
- [ ] Production uses a fixed model version, and important agents have a release gate.
- [ ] With add-ons: MCP allow-lists are set, every search is filtered, and every deployment has a guardrail.
- [ ] Tools that change records need approval.
- [ ] Personal data is redacted, answers are labelled as AI-generated, and users can give feedback.
- [ ] Your DPIA and ATRS record are done.

## Testing your app

- **Unit tests:** substitute `IAgentService`:

  ```csharp
  agents.RunAsync(BriefingAgents.Ofsted, Arg.Any<string>(), Arg.Any<AgentEvidence?>(), Arg.Any<CancellationToken>())
      .Returns(new AgentResult { AgentName = "ofsted-agent", Output = """{"rating":"Good","strengths":[]}""" });
  ```

- **Tests that start the host:** set `"RequireTokenUsageTelemetry": false`.
- **Lower-level services:** `IAgentRunnerService` runs an `AgentSpec` or `AgentReference` directly,
  `IAgentRuntimeService` handles temporary agents, and `IAgentFactory` manages versions, e.g. pruning by hand.

Not supported yet: built-in health checks.

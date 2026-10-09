# GovUK.Dfe.AI.Agents

Run Azure AI Foundry agents from .NET: one at a time, streamed, in parallel or in sequence. Every run is checked,
size-limited and concurrency-limited, and records its tokens and cost. Nothing is stored in Foundry.

## Packages

Install core, plus only the add-ons you need.

| Package                                                                                                                               | Adds                                                     | Register with             |
| ------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------- | ------------------------- |
| `GovUK.Dfe.AI.Agents`                                                                                                                 | Agents, versions, runs, answer checks, limits, telemetry | `AddAgents(...)`          |
| [`GovUK.Dfe.AI.Agents.Mcp`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.Mcp/readme.md)               | Tools from your own MCP servers                          | `.AddMcpServers()`        |
| [`GovUK.Dfe.AI.Agents.AISearch`](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents.AISearch/readme.md)     | Evidence from Azure AI Search, with citations            | `.AddAISearch()`          |
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
      "SystemPrompts": { "Ofsted": "Prompts/Ofsted.md", "Synthesis": "Prompts/Synthesis.md" }
    },
    "RunTimeout": "00:02:00",
    "MaxConcurrency": 4
  }
}
```

- Give the identity the **Foundry User** role on the project ([all roles](#roles)).
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

`AddAgents` and every add-on's `Add…()` are in `Microsoft.Extensions.DependencyInjection`. The types you'll use most:

| `using GovUK.Dfe.AI.Agents…`             | For                                                                                                      |
| ---------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| `.ValueObjects`                          | `AgentDefinition`, `AgentOutputSchema`, `AgentResult`, `AgentEvidence`, `AgentStreamUpdate`, `AgentSpec` |
| `.Services.Interfaces`                   | `IAgentService` (and the lower-level `IAgentRunnerService`, `IAgentRuntimeService`)                      |
| `.Extensions`                            | `ReadOutputAs<T>()`, `ToTokenUsageSummary()`                                                             |
| `.Tools` / `.Tools.Interfaces`           | `AgentTool` / `IAgentToolProvider`, `IToolCallApprover`                                                  |
| `.Privacy`                               | `PatternRedactor`, `IAgentInputRedactor`                                                                 |
| `.Quality` / `.Quality.Interfaces`       | `AgentTestCase`, `AgentEvaluationReport` / `IAgentTestRunner`, `IAgentRunEvaluator`                      |
| `.Context` / `.Extensibility.Interfaces` | `AgentContext`, `IContextRetriever` / `IAgentRunObserver`, `IAgentsPackage`                              |

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

| `AgentDefinition` property      | Meaning                                                                          |
| ------------------------------- | -------------------------------------------------------------------------------- |
| `Name`                          | The agent's name in Foundry; unique per app if the project is shared             |
| `SystemPromptKey`               | Its key under `PromptFiles:SystemPrompts`                                        |
| `IsManagedAgent`                | `true` (default): kept and reused. `false`: created and deleted on every run     |
| `AllowedTools`                  | The only tools it may call. Empty (default): none                                |
| `ToolsRequiringApproval`        | Tools that run only once a person approves ([below](#a-person-approves-changes)) |
| `OutputSchema`                  | A JSON schema for a typed answer; read it with `ReadOutputAs<T>()`               |
| `Validate`, `RequiredCitations` | [Answer checks](#answer-checks)                                                  |

### Streaming

For a chat screen, show the answer as it's written:

```csharp
await foreach (var update in agents.RunStreamingAsync(BriefingAgents.Synthesis, question, evidence, ct))
{
    if (update.Text is { } text) await response.WriteAsync(text, ct);   // the next piece, already cleaned
    if (update.Result is { } result) LogCost(result);                    // last: tokens, cost, RunId
}
```

- It works like `RunAsync`: same slots, limits, tools, redaction, cleaning and telemetry. Stop reading to cancel.
- Checks run once the answer is complete. It's already shown, so a failed check throws at the end with no retry.
- Single runs only. For a chain, run the earlier agents with `RunSequentialAsync`, then stream the last one.

### Tools and other agents

- **Web search:** `agents.AddTools("news-agent", new WebSearchToolProvider())`. Results are biased towards a location
  (default London); for a different town per run, use a temporary agent:

  ```csharp
  var tools = await new WebSearchToolProvider(WebSearchLocation.ForCity(trust.Town)).GetToolsAsync(ct);   // IAgentRuntimeService runtime
  var news = await runtime.RunEphemeralAsync(
      new AgentSpec { Name = "trust-news", Instructions = "Find recent local news about the trust and its schools. Cite each source.", Tools = tools },
      $"Recent news about {trust.Name} in {trust.Town}.", cancellationToken: ct);
  ```

- **Your own tools:** implement `IAgentToolProvider`, return `AgentTool.Function(name, description, jsonSchema)`, and
  add it with `agents.AddTools(...)`. Your app runs the calls. For MCP servers, use the Mcp add-on.
- **Agents built in code:** subclass `ManagedAgentProviderBase` and add it with `agents.AddAgentProvider<T>()`.
- **User prompt templates:** add them under `PromptFiles:UserPrompts` with `{{Name}}` placeholders, and fill one with
  `IPromptTemplateBuilder.Build("Key", values)`.

## Answer checks

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

- **Images become their alt text,** as a browser loads an image without a click, which could leak data.
- **HTML the model writes is shown as text** (`<script>` becomes `&lt;script>`), so injected markup can't run. A `<`
  that can't start a tag, as in "below < 90%", is left alone. Citation links are the only real HTML.
- **Links without a full address become text,** as they'd 404 in your app. Full `https://` and `mailto:` links are
  kept: show each link's domain, so users see where it goes.

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

- Each failure is a readable line, e.g. `Groundedness: fell from 4.5 to 4.2, more than the tolerance 0.2`.
- **Tolerance** stops a judge's run-to-run noise failing a release; **repeats** average scores over several runs.
- Cases run against a temporary copy of the agent; pass `AgentTestTarget.Deployed` to test the live version.
- Scores need the Evaluation add-on or your own `IAgentRunEvaluator`; without one, only `mustMention` and
  `mustNotMention` are checked.
- First release: pass no baseline, then save the passing report as `baseline.json`.

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

A denied call doesn't run, and the agent is told why. The run waits within `RunTimeout`. Startup fails if a tool needs
approval but there's no approver.

### Personal data is removed before the model sees it

```csharp
agents.AddRedactor(new PatternRedactor(new Dictionary<string, string>
{
    ["UPN"] = @"\b[A-Z]\d{11}[0-9A-Z]\b",
    ["NI number"] = @"\b[A-CEGHJ-PR-TW-Z]{2}\d{6}[A-D]\b",
}));
```

- Prompts, evidence and tool output are redacted before they're sent (`Pupil [UPN removed]`), as are the copies given
  to evaluators and observers. If a redactor fails, the run fails.
- Patterns can't find names: leave name fields out of search results and tool output, or implement
  `IAgentInputRedactor`.

### Every answer can be traced

Each `AgentResult` has a `RunId` and `CompletedAt` (also on the trace as `dfe.ai_agents.run_id`). Use them to label
answers as AI-generated, save an audit record from an `IAgentRunObserver`, and store user feedback against the run.

For fairness, give release-gate test cases a `group` (e.g. `"special-schools"`) and set `MaxGroupGap`.

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
- **Cost:** add each model's prices per 1,000 tokens; `result.Cost` is one run, `results.ToTokenUsageSummary().Cost`
  several. `gpt-5.1` also matches `gpt-5.1-2025-11-13` and `my-connection/gpt-5.1`.

  ```json
  "Pricing": {
    "Currency": "GBP",
    "Models": { "gpt-5.1": { "CostPer1kTokensInput": 0.001, "CostPer1kTokensCachedInput": 0.0001, "CostPer1kTokensOutput": 0.008 } }
  }
  ```

- **Temporary agents left by a crash:** `agents.AddEphemeralAgentSweep()` deletes this app's old ones every 30 minutes.

## Limits and telemetry

- **Limits:** output tokens, evidence, tool output, tool rounds, time and concurrency are capped, and rate limits (429)
  are retried. Defaults suit gpt-5.1. See [Limits](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/docs/limits.md).
- **Telemetry:** tokens, cost, duration, tool calls and remaining tokens, as OpenTelemetry metrics and traces, never
  with prompts or answers. See [Telemetry](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/docs/telemetry.md).
- **Logs:** a failed run logs its exception, which can include parts of the request. Limit log access and retention.

## Credentials

`Authentication` is the default identity; any service can have its own block (`Foundry`, `GlobalConcurrency`,
`ExternallyManagedAgents` and each add-on's section). For managed identities, set credentials in code:

```csharp
agents.UseCredential(new ManagedIdentityCredential())                     // default for every service
      .UseCredentialFor(AzureCredentialTarget.Foundry, foundryCredential)   // one core service
      .UseExternallyManagedAgentsCredential(centralCredential);           // the central agents' project
```

Add-ons have `UseMcpCredential(...)`, `UseAISearchCredential(...)` and `UseGuardrailsCredential(...)`. A service uses
its code credential, else its own block, else the default. Load secrets from Key Vault, e.g.
`AiAgents--Foundry--Authentication--ClientSecret`.

### Roles

Assign in Azure (Access control (IAM)) to each identity, e.g. the app's managed identity:

| Role                           | Scope                                       | Needed by                                               | Why                                                          |
| ------------------------------ | ------------------------------------------- | ------------------------------------------------------- | ------------------------------------------------------------ |
| Foundry User                   | Foundry project                             | Every app, and a provisioning job                       | Run agents, create versions, call the Evaluation judge model |
| Foundry User                   | The central Foundry project                 | Apps using `ExternallyManagedAgents` with an `Endpoint` | Run the central agents                                       |
| Storage Blob Data Contributor  | Storage account, or just the container      | Apps using `GlobalConcurrency`                          | Hold run slots across instances                              |
| Search Index Data Reader       | Search service                              | Apps using AISearch                                     | Read evidence from the indexes                               |
| The MCP server's app role      | The server's app registration (its `Scope`) | Apps using Mcp                                          | Call the server's tools                                      |
| Reader                         | Foundry resource                            | Apps using Guardrails                                   | Check the guardrail at startup                               |
| Cognitive Services Contributor | Foundry resource                            | Only a job that calls `ApplyAsync`                      | Create the guardrail; not needed if Bicep or Terraform does  |

## Options

All under `AiAgents`, or in code with `agents.Configure(o => ...)`.

| Setting                           | Default             | Purpose                                                                              |
| --------------------------------- | ------------------- | ------------------------------------------------------------------------------------ |
| `ApplicationName`                 | Entry assembly name | Telemetry tag, and scope of the temporary-agent sweep                                |
| `RunTimeout`                      | None                | Longest time one run may take                                                        |
| `MaxOutputTokensPerRun`           | 64000               | Output tokens one run may use (minimum 16)                                           |
| `MaxEvidenceCharacters`           | 200000              | Longer evidence is cut, keeping the start                                            |
| `MaxToolOutputCharacters`         | 40000               | Longer tool output is cut                                                            |
| `FenceToolOutput`                 | `true`              | Fences tool output as data                                                           |
| `MaxConcurrency`                  | None                | Runs at once on one instance                                                         |
| `GlobalConcurrency`               | Off                 | `MaxConcurrentRuns` across instances, and the `BlobContainerUri` for slots           |
| `MaxWaitForRunSlot`               | 2 minutes           | Longest wait for a slot                                                              |
| `AgentCacheDuration`              | 30 seconds          | How long a resolved version is reused; `0` turns caching off                         |
| `VersionPins`                     | None                | The version this environment runs, per agent                                         |
| `ProtectedVersions`               | None                | Versions pruning must keep, per agent                                                |
| `KeepLatestVersions`              | None                | Versions kept each time one is created (minimum 2)                                   |
| `ExternallyManagedAgents`         | None                | Agents from a provisioning job, with versions; optional `Endpoint`, `Authentication` |
| `ResponseFormatKey`               | None                | A system prompt appended to every agent's instructions                               |
| `ResponseFormatExemptPromptTypes` | None                | Prompt keys `ResponseFormatKey` isn't appended to                                    |
| `RequireTokenUsageTelemetry`      | `true`              | Fails startup when token metrics aren't recorded                                     |
| `ValidateAgentToolsAtStartup`     | `true`              | Fails startup if a pinned or external agent's tools can't run here                   |
| `EnableDriftDetection`            | `false`             | Warns when a pinned version no longer matches its definition                         |
| `Pricing`                         | None                | Prices per model, so results and metrics report cost                                 |
| `MaxRetries`                      | 6                   | Retries on a rate limit or transient failure; a retried 5xx may be billed twice      |
| `LowRemainingTokensPercent`       | 10                  | Threshold for `dfe.ai_agents.tokens.low`; `0` turns it off                           |

## Production checklist

- [ ] `ApplicationName` is set, and matches the name passed to `AddService(...)`.
- [ ] Secrets are in Key Vault (or managed identities are used), and each identity has only its own role.
- [ ] Telemetry arrives in Application Insights, and log access and retention fit your data.
- [ ] Shared project: staging and production pin their agents, and the pins are in `ProtectedVersions`.
- [ ] `KeepLatestVersions` is set wherever versions are created.
- [ ] `RunTimeout`, `MaxOutputTokensPerRun` and the concurrency limits fit your Foundry quota.
- [ ] Production uses a fixed model version, and important agents have a release gate.
- [ ] With add-ons: MCP allow-lists are set, every search is filtered, and every deployment has a guardrail.
- [ ] Tools that change records need approval; personal data is redacted.
- [ ] Answers are labelled as AI-generated, users can give feedback, and the DPIA and ATRS record are done.

## Testing your app

Substitute `IAgentService` in unit tests (match evidence with `Arg.Any<AgentEvidence?>()`):

```csharp
agents.RunAsync(BriefingAgents.Ofsted, Arg.Any<string>(), Arg.Any<AgentEvidence?>(), Arg.Any<CancellationToken>())
    .Returns(new AgentResult { AgentName = "ofsted-agent", Output = """{"rating":"Good","strengths":[]}""" });
```

Tests that start the host set `"RequireTokenUsageTelemetry": false`.

Not supported yet: built-in health checks.

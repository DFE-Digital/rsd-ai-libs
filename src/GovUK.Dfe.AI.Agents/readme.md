# GovUK.Dfe.AI.Agents

Run Azure AI Foundry agents from .NET: one at a time, in parallel or in sequence. Every run is checked, size-limited,
concurrency-limited and records its token usage.

## Packages

Install the core package, plus any add-ons you need. Each add-on depends on a compatible version of the core package.

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

- Give the identity the **Foundry User** role on the Foundry project.
- Load `Authentication:ClientSecret` from Key Vault, never from appsettings.json. To use managed identities instead,
  see [Credentials](#credentials).
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

| `using GovUK.Dfe.AI.Agents…`                              | For                                                                                     |
| --------------------------------------------------------- | --------------------------------------------------------------------------------------- |
| `.Builders`                                               | `AgentsBuilder`                                                                         |
| `.ValueObjects`                                           | `AgentDefinition`, `AgentOutputSchema`, `AgentResult`, `AgentSpec`, `CompletedAgentRun` |
| `.Services.Interfaces`                                    | `IAgentService`, `IAgentRunnerService`, `IAgentRuntimeService`                          |
| `.Extensions`                                             | `ReadOutputAs<T>()`, `ToTokenUsageSummary()`                                            |
| `.Context` / `.Context.Interfaces`                        | `AgentContext` / `IContextRetriever`                                                    |
| `.Filters`                                                | `ODataFilter`                                                                           |
| `.Quality` / `.Quality.Interfaces`                        | `AgentTestCase`, `AgentEvaluationReport` / `IAgentTestRunner`, `IAgentRunEvaluator`     |
| `.Extensibility.Interfaces`                               | `IAgentsPackage`, `IAgentRunObserver` (told about each successful run)                  |
| `.Enums`                                                  | `AzureCredentialTarget`, `AgentTestTarget`                                              |
| `.Tools.Interfaces` / `.Privacy`                          | `IToolCallApprover` / `PatternRedactor`                                                 |
| `.Providers` / `.Tools.WebSearch` / `.Prompts.Interfaces` | `ManagedAgentProviderBase` / `WebSearchToolProvider` / `IPromptTemplateBuilder`         |
| `.Diagnostics`                                            | `AgentTelemetry`                                                                        |
| `.Exceptions`                                             | `AgentGuardrailException`                                                               |

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

| `IAgentService` method                                                        | Use for                                         | When an agent fails                                                  |
| ----------------------------------------------------------------------------- | ----------------------------------------------- | -------------------------------------------------------------------- |
| `RunAsync(definition, prompt, evidence)`                                      | One agent                                       | Throws                                                               |
| `RunParallelAsync(definitions, resolvePrompt, context, resolveEvidence: ...)` | Independent agents                              | That agent gets a fallback result; the others carry on               |
| `RunSequentialAsync(definitions, resolvePrompt, initialInput, context)`       | A chain, e.g. draft then review                 | That agent gets a fallback; the next agent gets the last good output |
| `ProvisionAsync(definitions)`                                                 | A [provisioning job](#centrally-managed-agents) | Throws                                                               |

- Results come back in the same order as the definitions. To total their tokens, call `results.ToTokenUsageSummary()`.
- To throw instead of returning fallbacks, pass `shouldSuppress: _ => false`. This also cancels the other agents.

| `AgentDefinition` property      | Meaning                                                                                              |
| ------------------------------- | ---------------------------------------------------------------------------------------------------- |
| `Name`                          | The agent's name in Foundry. Must be unique per app if the project is shared                         |
| `SystemPromptKey`               | Its key under `PromptFiles:SystemPrompts`                                                            |
| `IsManagedAgent`                | `true` (default): kept and reused. `false`: created and deleted on every run                         |
| `AllowedTools`                  | The only tools the agent may call. Empty (the default) means no tools                                |
| `ToolsRequiringApproval`        | Tools that only run once approved, e.g. ones that change records ([Responsible AI](#responsible-ai)) |
| `OutputSchema`                  | A JSON schema for a typed answer; read it with `ReadOutputAs<T>()`                                   |
| `Validate`, `RequiredCitations` | [Answer checks](#answer-checks)                                                                      |

Other extension points:

- **Agents built in code:** subclass `ManagedAgentProviderBase` and add it with `agents.AddAgentProvider<T>()`.
- **Non-MCP tools:** `agents.AddTools("news-agent", new WebSearchToolProvider())`.
- **Local news, e.g. about a trust:** web search is biased towards a location (default: London, England). To search near
  each trust's or school's own town, run a temporary agent with that town; with no town, the default is used:

  ```csharp
  var tools = await new WebSearchToolProvider(WebSearchLocation.ForCity(trust.Town)).GetToolsAsync(ct);   // IAgentRuntimeService runtime
  var news = await runtime.RunEphemeralAsync(
      new AgentSpec { Name = "trust-news", Instructions = "Find recent local news about the trust and its schools. Cite each source.", Tools = tools },
      $"Recent news about {trust.Name} in {trust.Town}.", cancellationToken: ct);
  ```

  The location only biases results, so name the town in the prompt too. A temporary agent is created and deleted for each
  run, so every run can search a different town.

- **User prompt templates:** add them under `PromptFiles:UserPrompts` with `{{Name}}` placeholders, then fill one with
  `IPromptTemplateBuilder.Build("Key", values)`.

## Answer checks

- **Citations (on by default):** if the evidence is numbered (as search results are), the answer must cite it as
  `[Evidence n]` and may only cite evidence that exists. Unnumbered evidence isn't checked. Set
  `RequiredCitations = false` when the answer has nowhere to put citations.
- **No broken links:** citations are plain text, `[Evidence n]`, never links. Any link in an answer without a full web
  address (e.g. `[the report](files/report.pdf)`) is turned back into text, because it would point at your app and give
  a 404. Full `https://` links, such as web search sources, are kept.
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

Run fixed test cases in CI and fail the build if an agent got worse. Resolve `IAgentTestRunner` from the app's services:

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

- **Tolerance:** a judge can score the same answer differently from run to run. `Tolerance` (default `0.2`) stops that
  noise failing a release, while a real drop still does.
- **Repeats:** `repeats: 3` runs each case three times and averages its scores, so one unlucky answer doesn't decide the
  release. A case's facts must be right in every run. Each repeat costs another run and judge call.
- **Safe to run:** cases run against a temporary copy of the agent, so a failed gate publishes nothing. To test the
  deployed version instead, pass `AgentTestTarget.Deployed`.
- **Scores need an evaluator:** add the Evaluation package, or register your own `IAgentRunEvaluator`. Without one, only
  `mustMention` and `mustNotMention` are checked, and every metric fails as not scored.
- **First release:** pass no baseline. After a release passes, save its report as the new `baseline.json`.

## Responsible AI

Hooks that help your service meet the
[UK Government AI Playbook](https://www.gov.uk/government/publications/ai-playbook-for-the-uk-government). The library
gives you the hooks; your app owns the decisions, the user interface, and its DPIA and ATRS record.

### A person approves tool calls that change things

List the tools that need approval, and add an approver, e.g. one that asks a manager in your app:

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

- A denied call doesn't run. The agent is told why, and answers without it.
- The run waits for the decision, within `RunTimeout`. For decisions that take hours, end the run and start a new one
  once approved.
- Startup fails if a tool needs approval but there's no approver, or the tool isn't in `AllowedTools`.
- `dfe.ai_agents.tool.approvals` counts decisions, by tool and outcome.

### Personal data is removed before the model sees it

```csharp
agents.AddRedactor(new PatternRedactor(new Dictionary<string, string>
{
    ["UPN"] = @"\b[A-Z]\d{11}[0-9A-Z]\b",
    ["NI number"] = @"\b[A-CEGHJ-PR-TW-Z]{2}\d{6}[A-D]\b",
}));
```

Every prompt, evidence and tool output is redacted before it's sent, e.g. `Pupil [UPN removed]`. So are the copies given
to evaluators and run observers. Implement `IAgentInputRedactor` for anything a pattern can't catch.

### Every answer can be traced

Each `AgentResult` has a `RunId` and `CompletedAt`, and the run ID is tagged on the run's trace
(`dfe.ai_agents.run_id`). Use them to:

- **Label answers** as AI-generated, e.g. "Generated by AI on 5 October 2026. Check before use."
- **Keep an audit trail:** register an `IAgentRunObserver` that saves each `CompletedAgentRun` (prompt, evidence and
  answer, already redacted) to your own store, with your retention policy.
- **Collect feedback** in your app: store the user's verdict with the `RunId`, `AgentName`, `AgentVersion` and `Model`,
  so you can find the run and compare versions.

### Agents are checked for fairness before release

Give release-gate test cases a `group` (e.g. `"special-schools"`), then fail the gate if one group is served worse:

```csharp
// MaxGroupGap = 0.5 in the ReleaseGate above, or on its own:
var gaps = report.GroupGaps(maxGap: 0.5, "Groundedness", "Relevance");
```

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

- **Cost cap per run:** `MaxOutputTokensPerRun` caps output tokens across tool rounds and the retry, including reasoning
  tokens. A run that reaches the cap fails.
- **What runs cost:** add each model's prices per 1,000 tokens:

  ```json
  "Pricing": {
    "Currency": "GBP",
    "Models": {
      "gpt-5.1": { "CostPer1kTokensInput": 0.001, "CostPer1kTokensCachedInput": 0.0001, "CostPer1kTokensOutput": 0.008 }
    }
  }
  ```

  Then `result.Cost` is one run's cost, and `results.ToTokenUsageSummary().Cost` is the cost of several runs together,
  e.g. one whole briefing. Failed runs are included, as Foundry still bills them. A model name matches any model that
  starts with it, so `gpt-5.1` covers `gpt-5.1-2025-11-13`. A model with no price reports tokens only. Input tokens
  Foundry served from its prompt cache (`result.CachedInputTokens`) are charged at `CostPer1kTokensCachedInput`, or the
  input price if that isn't set.

- **Orphaned agents:** a crash can leave temporary agents behind. Add `agents.AddEphemeralAgentSweep()` to delete them
  every 30 minutes. The sweep only deletes this app's agents, and only ones older than any run could be, so it's safe to
  run on every instance. Keep `ApplicationName` unique per app.

## Limits

### Set by this library

| Limit                                                                 | Default                                  | Setting                               | When reached                                                                 |
| --------------------------------------------------------------------- | ---------------------------------------- | ------------------------------------- | ---------------------------------------------------------------------------- |
| Output tokens per run, including reasoning, tool rounds and the retry | 64,000                                   | `MaxOutputTokensPerRun`               | The run fails                                                                |
| Evidence per run                                                      | 200,000 characters (about 50,000 tokens) | `MaxEvidenceCharacters`               | Cut, keeping the start, with a note to the model                             |
| Evidence per search (AISearch)                                        | No limit                                 | `AISearch:MaxEvidenceCharacters`      | Whole results kept, most relevant first; the rest left out with a note       |
| One tool output                                                       | 40,000 characters (about 10,000 tokens)  | `MaxToolOutputCharacters`             | Cut, with a note to the model                                                |
| Tool-call rounds per run                                              | 10                                       | Fixed                                 | The run fails                                                                |
| Time per run                                                          | No limit                                 | `RunTimeout`                          | `TimeoutException`                                                           |
| Runs at once                                                          | No limit                                 | `MaxConcurrency`, `GlobalConcurrency` | The run waits up to `MaxWaitForRunSlot` (2 minutes), then `TimeoutException` |
| Retries of a rate-limited (429) or failed Foundry call                | 6, honouring `Retry-After`               | `MaxRetries`                          | The run fails; a rate limit says what to change                              |

Characters become tokens at about 4 to 1 for English text. The defaults are sized for **gpt-5.1** (up to 272,000 input
tokens): full evidence (~50,000 tokens) plus ten full tool outputs (~100,000) still leaves room for the instructions and
conversation. For a model with a smaller window, such as gpt-4o (128,000), lower them.

### Set by Foundry

Check the current figures for your model and region: they change. These are from Microsoft Learn, September 2026.

| Model                      | Context window (input + output)                | Max output tokens |
| -------------------------- | ---------------------------------------------- | ----------------- |
| gpt-5, gpt-5-mini, gpt-5.1 | 400,000 (input up to 272,000)                  | 128,000           |
| gpt-4.1, gpt-4.1-mini      | 1,047,576, but 300,000 on standard deployments | 32,768            |
| o3, o4-mini                | Input 200,000                                  | 100,000           |
| gpt-4o, gpt-4o-mini        | Input 128,000                                  | 16,384            |

- **The context window is shared:** instructions, prompt, evidence, tool outputs, the conversation so far, reasoning and
  the answer all count. Keep `MaxEvidenceCharacters` and tool outputs well within the model's input limit.
- **Rate limits are per deployment:** tokens per minute (TPM) and requests per minute (RPM) depend on the model, the
  deployment type and your subscription's quota tier. For example, gpt-5.1 on Global Standard starts at 1,000,000 TPM
  and 10,000 RPM. Size `MaxConcurrency` and `GlobalConcurrency` to stay under them; over the limit, calls get HTTP 429,
  which the library retries.
- **Tokens per minute is a rolling 60-second window,** shared by every app using the deployment: each request's tokens
  come back 60 seconds after it. The library reads the real figure from each response (`remaining-tokens` or
  `x-ratelimit-remaining-tokens`) into `dfe.ai_agents.tokens.remaining`, and counts each response below
  `LowRemainingTokensPercent` (default 10%) of the limit in `dfe.ai_agents.tokens.low`. Set the threshold per app.
- **Agent Service limits:** up to 128 tools per agent. The Agent Service has no rate limit of its own; the model
  deployment's limits apply.

Sources: [model context windows](https://learn.microsoft.com/azure/ai-foundry/openai/concepts/models),
[quotas and rate limits](https://learn.microsoft.com/azure/ai-foundry/openai/quotas-limits),
[Agent Service limits](https://learn.microsoft.com/azure/ai-foundry/agents/quotas-limits).

## Telemetry

Token usage is billed, so it must be recorded: startup fails unless the metrics below are subscribed. In tests and local
development only, you can set `"RequireTokenUsageTelemetry": false`.

Names follow the [OpenTelemetry generative AI conventions](https://github.com/open-telemetry/semantic-conventions-genai)
(`gen_ai.*`), so standard GenAI dashboards understand them. Signals the conventions don't cover use `dfe.ai_agents.*`.

| Metric                                        | Records                                                                                                    |
| --------------------------------------------- | ---------------------------------------------------------------------------------------------------------- |
| `gen_ai.client.inference.usage.input_tokens`  | Input tokens per run, including tool rounds and failed runs                                                |
| `gen_ai.client.inference.usage.output_tokens` | Output tokens per run, including reasoning, tool rounds and failed runs                                    |
| `gen_ai.invoke_agent.duration`                | Seconds per run; `error.type` is set when it failed                                                        |
| `gen_ai.invoke_agent.inference_calls`         | Model calls per run (more than 1 means tool rounds or a retry)                                             |
| `gen_ai.invoke_agent.tool_calls`              | Tool calls per run                                                                                         |
| `gen_ai.execute_tool.duration`                | Seconds per tool call, by tool                                                                             |
| `gen_ai.invoke_workflow.duration`             | Seconds per parallel or sequential run                                                                     |
| `dfe.ai_agents.workflow.tokens`               | Total tokens per parallel or sequential run, e.g. one briefing, by `gen_ai.token.type` (`input`, `output`) |
| `dfe.ai_agents.cost`                          | What each run cost, by agent, model and `dfe.ai_agents.currency` (needs `Pricing`)                         |
| `dfe.ai_agents.workflow.cost`                 | What each parallel or sequential run cost, e.g. one briefing (needs `Pricing`)                             |
| `dfe.ai_agents.run_slot.wait.duration`        | Seconds spent waiting for a slot. If this keeps rising, the limits are too low                             |
| `dfe.ai_agents.guardrail.blocks`              | Prompts and answers a Foundry guardrail blocked                                                            |
| `dfe.ai_agents.tool.approvals`                | Tool calls that needed approval, by tool and `dfe.ai_agents.tool.approved`                                 |
| `dfe.ai_agents.tokens.remaining`              | The deployment's remaining tokens per minute, from each Foundry response                                   |
| `dfe.ai_agents.tokens.low`                    | Responses below `LowRemainingTokensPercent` of the deployment's token limit                                |

Metrics are tagged with `gen_ai.agent.name`, the model (`gen_ai.response.model`) and the application
(`dfe.ai_agents.application`). The spans are `invoke_workflow` (a parallel or sequential run), `invoke_agent {agent}` and
`execute_tool {tool}`. Telemetry never contains prompts, evidence, tool arguments, tool output or exception messages:
a failure is recorded only as its `error.type`. Add-ons record their own metrics under the same meter (see their
readmes).

Tokens per app and agent per day:

```kusto
customMetrics
| where name in ("gen_ai.client.inference.usage.input_tokens", "gen_ai.client.inference.usage.output_tokens")
| summarize tokens = sum(valueSum) by cloud_RoleName, agent = tostring(customDimensions["gen_ai.agent.name"]), bin(timestamp, 1d)
```

Cost per app and agent per day:

```kusto
customMetrics
| where name == "dfe.ai_agents.cost"
| summarize cost = sum(valueSum) by cloud_RoleName, agent = tostring(customDimensions["gen_ai.agent.name"]),
    currency = tostring(customDimensions["dfe.ai_agents.currency"]), bin(timestamp, 1d)
```

Times per hour an app ran low on tokens:

```kusto
customMetrics
| where name == "dfe.ai_agents.tokens.low"
| summarize times = sum(valueSum) by cloud_RoleName, bin(timestamp, 1h)
```

When a guardrail blocks a prompt or answer, the run fails and the inner exception is an `AgentGuardrailException`. In
parallel and sequential runs, the agent gets a fallback result instead.

## Credentials

`Authentication` is the default identity. Any service can have its own `Authentication` block instead: `Foundry`,
`GlobalConcurrency`, `ExternallyManagedAgents`, and each add-on's section. Load secrets from Key Vault, e.g. the secret
`AiAgents--Foundry--Authentication--ClientSecret` (environment variable `AiAgents__Foundry__Authentication__ClientSecret`).

To use managed identities, set credentials in code:

```csharp
agents.UseCredential(new ManagedIdentityCredential())                     // default for every service
      .UseCredentialFor(AzureCredentialTarget.Foundry, foundryCredential)   // one core service
      .UseExternallyManagedAgentsCredential(centralCredential);           // the central agents' project
```

Each add-on has its own method: `UseMcpCredential(server, ...)`, `UseAISearchCredential(...)` and
`UseGuardrailsCredential(...)`.

A service uses, in order: its code credential, its own `Authentication` block, then the default. Give each identity only
the role its service needs.

## Options

All options sit under `AiAgents`. You can also change them in code with `agents.Configure(o => ...)`.

| Setting                           | Default             | Purpose                                                                                                                                                |
| --------------------------------- | ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `ApplicationName`                 | Entry assembly name | Telemetry tag, and scope of the orphan sweep                                                                                                           |
| `RunTimeout`                      | None                | Longest time one run may take                                                                                                                          |
| `MaxOutputTokensPerRun`           | 64000               | Output tokens one run may use (minimum 16)                                                                                                             |
| `MaxEvidenceCharacters`           | 200000              | Longer evidence is cut, keeping the start                                                                                                              |
| `MaxToolOutputCharacters`         | 40000               | Longer tool output is cut                                                                                                                              |
| `FenceToolOutput`                 | `true`              | Fences tool output as data                                                                                                                             |
| `DeleteConversationsAfterRun`     | `true`              | Keeps prompts and evidence out of Foundry                                                                                                              |
| `MaxConcurrency`                  | None                | Runs at once on one instance                                                                                                                           |
| `GlobalConcurrency`               | Off                 | `MaxConcurrentRuns` across instances, and the `BlobContainerUri` that holds the slots                                                                  |
| `MaxWaitForRunSlot`               | 2 minutes           | Longest time a run waits for a slot                                                                                                                    |
| `AgentCacheDuration`              | 30 seconds          | How long a resolved version is reused; `0` turns caching off                                                                                           |
| `VersionPins`                     | None                | The version this environment runs, per agent                                                                                                           |
| `ProtectedVersions`               | None                | Versions pruning must keep, per agent                                                                                                                  |
| `KeepLatestVersions`              | None                | Versions kept each time one is created (minimum 2)                                                                                                     |
| `ExternallyManagedAgents`         | None                | Agents from a provisioning job, with their versions; optional `Endpoint` and `Authentication`                                                          |
| `ResponseFormatKey`               | None                | A system prompt appended to every agent's instructions                                                                                                 |
| `ResponseFormatExemptPromptTypes` | None                | Prompt keys that `ResponseFormatKey` isn't appended to                                                                                                 |
| `RequireTokenUsageTelemetry`      | `true`              | Fails startup when token metrics aren't recorded                                                                                                       |
| `ValidateAgentToolsAtStartup`     | `true`              | Fails startup if a pinned or external agent's tools can't run here                                                                                     |
| `EnableDriftDetection`            | `false`             | Warns when a pinned version no longer matches its definition                                                                                           |
| `Pricing`                         | None                | Prices per model, so results and metrics report cost                                                                                                   |
| `MaxRetries`                      | 6                   | Retries on a rate limit or transient failure, honouring `Retry-After` (rides out about a minute of throttling). A retried 5xx call may be billed twice |
| `LowRemainingTokensPercent`       | 10                  | Counts responses below this % of the deployment's token limit (`dfe.ai_agents.tokens.low`); 0 turns it off                                             |

## Production checklist

- [ ] `ApplicationName` is set, and matches the name passed to `AddService(...)`.
- [ ] Secrets come from Key Vault (or you use managed identities), and each identity has only its own role.
- [ ] Telemetry is arriving in Application Insights.
- [ ] In a shared project, staging and production pin their agents, and those pins are in `ProtectedVersions`.
- [ ] `KeepLatestVersions` is set wherever versions are created.
- [ ] `RunTimeout`, `MaxOutputTokensPerRun`, `MaxConcurrency` and `GlobalConcurrency` fit your Foundry quota.
- [ ] Production uses a fixed model version, and important agents have a release gate.
- [ ] With add-ons: MCP allow-lists are set, every search is filtered, and every deployment has a guardrail.
- [ ] Tools that change records are in `ToolsRequiringApproval`, with an approver.
- [ ] Personal data is redacted, answers are labelled as AI-generated, and users can give feedback (stored with the `RunId`).
- [ ] Your DPIA and ATRS record are done. See [Responsible AI](#responsible-ai).

## Testing your app

- **Unit tests:** substitute `IAgentService` and return a result:

  ```csharp
  agents.RunAsync(BriefingAgents.Ofsted, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
      .Returns(new AgentResult { AgentName = "ofsted-agent", Output = """{"rating":"Good","strengths":[]}""" });
  ```

- **Tests that start the host:** set `"RequireTokenUsageTelemetry": false`.
- **Lower-level services:** `IAgentRunnerService` runs an `AgentSpec` or `AgentReference` directly. `IAgentRuntimeService` handles
  temporary agents and orphan clean-up. `IAgentFactory` maintains versions, e.g. pruning them by hand.

Not supported yet: streaming and built-in health checks.

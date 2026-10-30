# Security review

For the security reviewer and the team adopting these packages, before an app goes live and after any change to its
identities, services or package versions.

The library's tests prove its own behaviour against fakes: an in-memory Foundry, scripted model responses, a fake
Content Safety endpoint and substitute credentials. They can't prove which identities a deployment really uses, what
Azure or an MCP server actually enforces, or that a new SDK or service version still behaves the same. This page lists
each trust boundary, what the library enforces, what you must check in your deployment, and the evidence to keep. Sign
off each row of the [checklist](#sign-off-checklist).

Roles are listed in [Access](Access.md); settings in [Settings](Settings.md); guardrails in the
[Guardrails package](../src/GovUK.Dfe.AI.Agents.Guardrails/Readme.md).

## Trust boundaries

| # | Boundary                                                             | What crosses it                                                    | Identity                                                          |
| - | -------------------------------------------------------------------- | ------------------------------------------------------------------ | ----------------------------------------------------------------- |
| 1 | [App to its Foundry project](#1-app-to-its-foundry-project)          | Prompts, evidence, tool output, answers                            | The Foundry credential                                            |
| 2 | [App to another Foundry project](#2-app-to-another-foundry-project)  | The same, for `ExternallyManagedAgents` with an `Endpoint`         | That project's credential, **else this app's Foundry credential** |
| 3 | [App to MCP servers](#3-app-to-mcp-servers)                          | Tool arguments out, tool output in                                 | Each server's credential, with a token for its `Scope`            |
| 4 | [App to Resource Manager](#4-foundry-guardrails)                     | Guardrail definitions: read by apps, written by a provisioning job | The Guardrails credential                                         |
| 5 | [App to Content Safety](#5-prompt-shields)                           | Question, evidence and tool output, when scanning is on            | The Guardrails credential                                         |
| 6 | [Untrusted content to the model](#6-untrusted-content-and-the-model) | Evidence (e.g. AI Search results), tool output, earlier answers    | None                                                              |
| 7 | [Model to your users](#streamed-answers)                             | Answers, streamed or whole                                         | None                                                              |
| 8 | [App to telemetry and logs](#7-personal-data-telemetry-and-logs)     | Metrics, traces, logs                                              | None                                                              |

Every Azure call uses Entra ID: a `TokenCredential` set in code (e.g. a managed identity) or a service principal from
an `Authentication` block. There are no API keys.

## What Foundry stores

Runs don't store their history in Foundry, but an agent's definition is stored there, and so is anything published to
an evaluation. Treat what's stored as you would the data in it: a system prompt can describe your data or processes, and
a tool's description can name your systems.

| What                                                             | Stored in Foundry?                        | What it holds                                                                                                                                                                    | How long                                                                                                             |
| ---------------------------------------------------------------- | ----------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| A run's question, evidence, tool output and answer               | **No**: every request sets `store: false` | Nothing                                                                                                                                                                          | The app keeps the run's history in memory for the run only                                                           |
| Each agent version                                               | **Yes**                                   | Its full instructions (the system prompt file), every tool's name, description and argument schema (including MCP servers'), its output schema, description, model and guardrail | Until deleted. **Old versions are never pruned unless `KeepLatestVersions` is set**; pinned versions are always kept |
| An ephemeral agent (`IsManagedAgent: false`)                     | **Yes**, for the run                      | The same as a version                                                                                                                                                            | Deleted after the run; if that fails, until `DeleteOrphanedEphemeralAgentsAsync` removes it                          |
| A judge agent (Evaluation, on a model connection)                | **Yes**                                   | The evaluator's instructions; never a run's content                                                                                                                              | Until deleted                                                                                                        |
| Release-gate answers, with Evaluation's `PublishToFoundry: true` | **Yes**                                   | Each test case's **prompt, evidence and answer**, in a Foundry evaluation run                                                                                                    | Until the evaluation is deleted                                                                                      |
| Guardrails and blocklists (Guardrails' `ApplyAsync`)             | **Yes**, in the Foundry resource          | The filter settings and **every blocklist term and pattern**                                                                                                                     | Until deleted                                                                                                        |

**Outside the library:** Microsoft's own handling of what's sent to a model, e.g. abuse monitoring, follows your Azure
agreement, not these settings. Check it for your data.

**Check in your deployment:**

- System prompts and tool descriptions hold nothing you couldn't store in the Foundry project, e.g. no personal data or
  secrets.
- `KeepLatestVersions` is set (e.g. 3), unless every past version should be kept.
- `PublishToFoundry` is on only if the release-gate test cases may be stored in the project: use synthetic data in them.
- Blocklists hold no sensitive terms you can't store in the resource. The Guardrails package says to load them from Key
  Vault, but `ApplyAsync` still writes them to the resource.

## 1. App to its Foundry project

**The library enforces:**

- Runs are stateless: every request sets `store: false`, and the run's history stays in the app. Agent definitions
  are stored: see [What Foundry stores](#what-foundry-stores).
- Each run is capped: `RunTimeout` (300 s), `MaxOutputTokensPerRun` (64,000), `MaxEvidenceCharacters` (200,000),
  `MaxToolOutputCharacters` (40,000) and 10 tool-call rounds.
- Every agent version it creates gets the guardrail, when one is set.
- Ephemeral agents are deleted after each run; the orphan sweep deletes only this app's own.

**Check in your deployment:**

- The Foundry identity is a managed identity, or a service principal whose secret is in Key Vault, with **Foundry User on
  the project** only. The resource scope is needed only for Evaluation's `PublishToFoundry`.
- Foundry keeps no responses for the app's runs: after a test run, the project shows no stored response or conversation.
- **Shared projects:** anyone with Foundry User on the project can create versions of your agents. Pin production
  versions, and turn on `EnableDriftDetection` (off by default) so a changed version is reported.
- Network access to the Foundry resource (public access or private endpoint) meets your standards. The library doesn't
  configure it.

**Evidence:** the role assignments on the project and resource; one run's trace showing `gen_ai.agent.version`.

## 2. App to another Foundry project

Used when `ExternallyManagedAgents:Endpoint` is set: agents a central team provisions in their own project.

**The library enforces:**

- The credential is `UseExternallyManagedAgentsCredential(...)`, else `ExternallyManagedAgents:Authentication`, **else
  this app's own Foundry credential**.
- This app's input shield and tool output shield also check runs in that project.
- At startup, each pinned agent's function tools must be in its `AllowedTools` and offered by a binding here
  (`ValidateAgentToolsAtStartup`, on by default). An unreachable project only logs a warning.
- This app's guardrail check **skips** these agents: a guardrail belongs to one Foundry resource.

**Check in your deployment:**

- Which identity is really used. If neither credential is set, it's this app's Foundry identity: it then needs Foundry User
  on the central project, and nothing on that resource.
- The central project's agent versions carry a guardrail. Ask its owners for evidence; this app can't check it.
- Versions are pinned. An agent following `latest` takes the central team's next version without your release gate.
- Sending prompts and evidence to that project is allowed for your data: same tenant, region and data-processing terms.

**Evidence:** the credential in use and its roles on both projects; the central agents' guardrail; the pinned versions.

## 3. App to MCP servers

**The library enforces:**

- `ServerUri` must be `https` (plain `http` only for a loopback address).
- A token is requested for `Scope` from the server's own credential, cached, and sent as a `Bearer` header only to
  `ServerUri`. Foundry receives only the tools' names, descriptions and argument schemas, never the address or credential.
- Only tools in `AllowedToolNames` are offered or run. Startup fails if the server lacks one. Each agent can be given a
  subset, and a call to any other tool is refused.
- Tool output is capped, redacted, fenced as data and, with `ScanToolOutput`, scanned by Prompt Shields before the model
  reads it.
- Tools in `ToolsRequiringApproval` run only once your approver agrees.

**Check in your deployment:**

- `Scope` is the server's own app registration (e.g. `api://<server-app-id>/.default`), and the app's identity holds only
  the app roles it needs.
- The server checks the token's audience, issuer and app role on **every tool call**, not only when connecting.
- The server treats arguments as untrusted: the model chooses them, and injected text can steer it. It must authorise
  each record and validate each input itself.
- Tools that change data are in `ToolsRequiringApproval`, or the server enforces the same control.
- The token goes only to `ServerUri`: confirm with egress or proxy logs, and that Foundry's traces hold no MCP address.

**Evidence:** the app registration's exposed scope and assigned roles; a call with a token for the wrong audience,
refused by the server; the egress logs.

## 4. Foundry guardrails

**The library enforces:**

- At startup, each deployment, or each agent version this app runs, must carry the guardrail, not weakened.
  `RequireAtStartup` (on by default) fails startup; off, it only warns.
- If Resource Manager can't be reached, startup **only warns** unless `FailWhenUnreachable` is on.
- A Foundry block (`content_filter` or `ResponsibleAIPolicyViolation`), or a gateway error listed in
  `GatewayGuardrailErrors`, fails the run as a guardrail block, counted by `dfe.ai_agents.guardrail.blocks`.

**Check in your deployment:**

- Decide `FailWhenUnreachable` for production. On, the app never runs without its guardrail confirmed, but an Azure outage
  stops new instances starting.
- A real block is reported as one: run the release gate's `guardrail/` cases against the deployed agents, and check
  `dfe.ai_agents.guardrail.blocks` counts them.
- Microsoft lists agent guardrails as preview. Recheck the blocks after Foundry changes.
- For a model behind your own gateway, no Foundry guardrail runs. Check the gateway's own policy, and that its real block
  responses match `GatewayGuardrailErrors`.
- Only the provisioning job holds Cognitive Services Contributor.

**Evidence:** the release gate's report with its guardrail section; the role assignments on the Foundry resource.

## 5. Prompt Shields

Used when `ScanPrompts`, `ScanEvidence` or `ScanToolOutput` is on. For indirect attacks, `ScanEvidence` and
`ScanToolOutput` are the checks to rely on; see
[Which setting catches indirect attacks](../src/GovUK.Dfe.AI.Agents.Guardrails/Readme.md#which-setting-catches-indirect-attacks).

**The library enforces:**

- Text is scanned after redaction, before it's sent to the model; long text in overlapping parts.
- It fails closed: if text can't be scanned, the run fails rather than sending it. Throttling is retried.

**Check in your deployment:**

- The Guardrails identity has Cognitive Services User on the Content Safety resource.
- In a test environment, planted instructions in evidence and in a tool's output are blocked, with stage `evidence` and
  `tool_output`. With the role removed, runs fail rather than continue.
- The Content Safety endpoint is in a region your data may go to: scanned text is sent there.
- The added time, about 100–200 ms per scan, is acceptable.

**Evidence:** the blocked test runs and their `dfe.ai_agents.guardrail.blocks` counts; the failed run without the role.

## 6. Untrusted content and the model

**The library enforces:**

- Evidence goes in the user turn, fenced between random markers and JSON-escaped, never as instructions. Tool output is
  fenced too (`FenceToolOutput`, on by default). Earlier agents' answers in a sequence are passed as evidence.
- Agents may call no tools unless they're in `AllowedTools`.
- Answers are cleaned: images are removed, so they can't load an address that leaks data; HTML the model writes is shown
  as text; links without a full web address are turned back into text. Citation links are the only HTML the library adds.

**Check in your deployment:**

- Release-gate cases cover your own injection risks, e.g. instructions planted in your search index.
- Your page shows answers without re-enabling raw HTML.
- **Links stay:** a link with a full `https` address in an answer is kept, as web search sources need it. Injected
  evidence could make the model write a misleading one. Decide whether your page shows where links go, or opens them with
  a warning.

**Evidence:** the release gate's report; a review of how your page renders answers.

### Streamed answers

With `RunStreamingAsync`, an answer reaches the page before it's finished, so a failure can come after text is shown.

**The library enforces:**

- Each piece is cleaned before it's passed on: an image or tag is held back until it's complete, so it never reaches the
  page before it can be removed.
- The answer's checks (`Validate`, required citations) run once it's complete. A failed check, a guardrail block of the
  answer, a timeout or a token cap throws after the last piece, with no retry.
- The result's `Output` is exactly the pieces joined, including any text written before a tool call, so the checks cover
  everything shown. A JSON answer comes as one piece at the end.
- An agent that a tool or observer runs never streams into the answer.
- Stopping reading cancels the run; what it was billed is still recorded.

**Check in your deployment:**

- **Your page discards what it showed when the stream throws**, and tells the user the answer can't be used. Test it with
  an agent whose `Validate` always fails, and with a run that times out.
- A guardrail block of a streamed answer, run against the deployed agent, ends the stream with a block
  (`AgentGuardrailException`, stage `answer`) and leaves nothing on the page.
- The deployment's content filter doesn't use **asynchronous** streaming filtering, if your model offers it: that mode can
  send text before it's filtered.
- A user closing the page cancels the run, so it stops using tokens.

**Evidence:** recordings or tests of the page with a failed check, a timeout and a guardrail block; the content filter's
streaming setting.

## 7. Personal data, telemetry and logs

**The library enforces:**

- Redactors run on the question, evidence and tool output before the model sees them; run observers get the redacted text.
- Metrics and traces carry names, IDs, counts, durations, costs and error types, never the question, evidence, answer or
  tool arguments.
- **Logs** can carry exception messages, which can echo content, e.g. a tool's error.

**Check in your deployment:**

- Your redactor patterns cover your data, e.g. pupil numbers and case references.
- Access to logs is limited, and their retention is set.
- Your own metrics add no personal data as tags.

**Evidence:** the redactor configuration and its tests; the log workspace's access and retention settings.

## 8. SDK and service changes

The packages use GA SDKs: Azure.AI.Projects 2.0.1, Azure.AI.Projects.Agents 2.0.0, Azure.AI.Extensions.OpenAI 2.0.0,
Azure.ResourceManager.CognitiveServices 1.6.0, ModelContextProtocol.Core 2.3.0, and the Prompt Shields API 2024-09-01.
Renovate proposes upgrades. The fakes can't detect a change in how the services behave, for example:

- the Responses API's streaming events, token usage fields or reasons a response stops early
- the shape of a guardrail block error
- a guardrail assigned to an agent version by its Resource Manager ID
- the MCP protocol version a server accepts

**Check after each upgrade, in a test environment:** a run, a streamed run, a run that calls an MCP tool, a guardrail
block, a Prompt Shields block, and non-zero token and cost metrics. Keep the results with the upgrade.

## Sign-off checklist

| #   | Check                                                                                                                            | Evidence                                    | Signed off |
| --- | -------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------- | ---------- |
| 0.1 | Stored agent definitions, published evaluations and blocklists hold only what may be stored in Foundry; `KeepLatestVersions` set | Prompt files and blocklist review; settings |            |
| 1.1 | Foundry identity is a managed identity or Key Vault-held service principal, with Foundry User on the project only                | Role assignments                            |            |
| 1.2 | Foundry stores no responses for the app's runs                                                                                   | Project after a test run                    |            |
| 1.3 | Production versions are pinned; `EnableDriftDetection` decided                                                                   | Settings                                    |            |
| 2.1 | Identity used for another project is known, with Foundry User there only                                                         | Role assignments on both projects           |            |
| 2.2 | The central project's agents carry a guardrail; versions are pinned                                                              | Evidence from its owners; settings          |            |
| 2.3 | Sending data to that project is allowed                                                                                          | Data-processing review                      |            |
| 3.1 | Each MCP `Scope` is the server's own registration; the app holds only the roles it needs                                         | App registration                            |            |
| 3.2 | Each server checks audience, issuer and role on every tool call, and authorises each record                                      | Refused call with a wrong-audience token    |            |
| 3.3 | Tools that change data need approval                                                                                             | `ToolsRequiringApproval`, or server control |            |
| 3.4 | MCP tokens reach only their server                                                                                               | Egress logs; Foundry traces                 |            |
| 4.1 | `FailWhenUnreachable` decided for production                                                                                     | Settings                                    |            |
| 4.2 | Real guardrail blocks are reported as blocks                                                                                     | Release gate report; block metric           |            |
| 4.3 | A gateway model's own policy is checked, and `GatewayGuardrailErrors` matches it                                                 | Gateway policy; a real block                |            |
| 4.4 | Only the provisioning job holds Cognitive Services Contributor                                                                   | Role assignments                            |            |
| 5.1 | `ScanEvidence` and, with tools, `ScanToolOutput` are on, or their absence is accepted                                            | Settings                                    |            |
| 5.2 | Planted instructions are blocked; a missing role fails runs                                                                      | Test runs                                   |            |
| 5.3 | The Content Safety region is acceptable                                                                                          | Resource location                           |            |
| 6.1 | Release-gate cases cover your injection risks                                                                                    | Release gate report                         |            |
| 6.2 | Your page renders answers safely, including links                                                                                | Page review                                 |            |
| 6.3 | A streamed answer that fails, times out or is blocked is removed from the page                                                   | Page tests                                  |            |
| 6.4 | The content filter doesn't filter streamed text after sending it                                                                 | Content filter setting                      |            |
| 7.1 | Redactors cover your personal data                                                                                               | Redactor tests                              |            |
| 7.2 | Log access and retention are set                                                                                                 | Workspace settings                          |            |
| 8.1 | Each package upgrade is checked against the services in a test environment                                                       | Upgrade results                             |            |

## Known limits

- A Foundry guardrail isn't relied on to scan evidence or the output of tools this app runs; the app-side scans do that.
- Microsoft lists agent guardrails as preview.
- Links with a full web address stay in answers.
- Anyone with Foundry User on a shared project can create versions of your agents.
- Without its own credential, another project is called with this app's Foundry identity.
- Logs can carry content from exception messages.
- Agent definitions, published release-gate answers and blocklists are stored in Foundry; only run history isn't.
- A streamed answer is shown before it's checked: the page, not the library, must remove it when the stream fails.

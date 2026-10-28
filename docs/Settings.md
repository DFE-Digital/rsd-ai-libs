# Settings

Every `AiAgents` setting in the core package.

All under `AiAgents`, or in code with `agents.Configure(o => ...)`.

| Setting                           | Default             | Purpose                                                                                               |
| --------------------------------- | ------------------- | ----------------------------------------------------------------------------------------------------- |
| `Foundry:Endpoint`                | Required            | The Foundry project endpoint                                                                          |
| `Foundry:DefaultModel`            | Required            | The model agents use, e.g. `<connection>/gpt-5.1`; every model must then be on that connection        |
| `Foundry:Guardrail`               | None                | The guardrail every new agent version uses                                                            |
| `Foundry:GatewayGuardrailErrors`  | None                | Gateway errors (`Status`, `Code`) reported as guardrail blocks                                        |
| `Authentication`                  | None                | The default identity: `TenantId`, `ClientId` and `ClientSecret`, unless set in code                   |
| `PromptFiles`                     | None                | `SystemPrompts` and `UserPrompts`: each prompt key and its file                                       |
| `ApplicationName`                 | Entry assembly name | Telemetry tag, and scope of the temporary-agent sweep                                                 |
| `RunTimeout`                      | 300                 | Seconds one run may take, e.g. `1800`; needed by the orphan clean-up                                  |
| `MaxOutputTokensPerRun`           | 64000               | Output tokens one run may use (minimum 16)                                                            |
| `MaxEvidenceCharacters`           | 200000              | Longer evidence is cut, keeping the start                                                             |
| `MaxToolOutputCharacters`         | 40000               | Longer tool output is cut                                                                             |
| `FenceToolOutput`                 | `true`              | Fences tool output as data                                                                            |
| `MaxRunsPerInstance`              | 10                  | Runs at once on one instance                                                                          |
| `GlobalConcurrency`               | Off                 | `MaxRunsAcrossInstances` across instances, and the `BlobContainerUri` for slots                       |
| `MaxWaitToStartRun`               | 120                 | Seconds a run waits for a slot                                                                        |
| `AgentCacheDuration`              | 60                  | Seconds a resolved version is reused, e.g. `30`; `0` turns caching off                                |
| `VersionPins`                     | None                | The version this environment runs, per agent                                                          |
| `ProtectedVersions`               | None                | Versions pruning must keep, per agent                                                                 |
| `KeepLatestVersions`              | None                | Versions kept each time one is created (minimum 2)                                                    |
| `ExternallyManagedAgents`         | None                | Agents from a provisioning job, with versions; optional `Endpoint`, `Authentication`                  |
| `ResponseFormatKey`               | None                | A system prompt appended to every agent's instructions                                                |
| `ResponseFormatExemptPromptTypes` | None                | Prompt keys `ResponseFormatKey` isn't appended to                                                     |
| `RequireTokenUsageTelemetry`      | `true`              | Fails startup when token metrics aren't recorded                                                      |
| `ValidateAgentToolsAtStartup`     | `true`              | Fails startup if a pinned or external agent's tools can't run here                                    |
| `EnableDriftDetection`            | `false`             | Warns when a pinned version no longer matches its definition                                          |
| `Pricing`                         | None                | Prices per model per 1,000 tokens, and `Currency` (default `GBP`), so results and metrics report cost |
| `MaxRetries`                      | 6                   | Retries on a rate limit or transient failure; a retried 5xx may be billed twice                       |
| `LowRemainingTokensPercent`       | 10                  | Threshold for `dfe.ai_agents.tokens.low`; `0` turns it off                                            |

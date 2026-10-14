# DfE AI Agents Libraries

.NET libraries for running Azure AI Foundry agents in DfE services: agents defined in code, checked answers with
citations, cost and concurrency limits, and token and cost telemetry. Nothing is stored in Foundry.

## Packages

| Package                          | What it does                                  | Docs                                                   |
| -------------------------------- | --------------------------------------------- | ------------------------------------------------------ |
| `GovUK.Dfe.AI.Agents`            | Core: define, version and run agents          | [readme](src/GovUK.Dfe.AI.Agents/readme.md)            |
| `GovUK.Dfe.AI.Agents.Mcp`        | Tools from your own MCP servers               | [readme](src/GovUK.Dfe.AI.Agents.Mcp/readme.md)        |
| `GovUK.Dfe.AI.Agents.AISearch`   | Evidence from Azure AI Search, with citations | [readme](src/GovUK.Dfe.AI.Agents.AISearch/readme.md)   |
| `GovUK.Dfe.AI.Agents.Evaluation` | A judge model that scores answers             | [readme](src/GovUK.Dfe.AI.Agents.Evaluation/readme.md) |
| `GovUK.Dfe.AI.Agents.Guardrails` | Foundry guardrails on your model deployments  | [readme](src/GovUK.Dfe.AI.Agents.Guardrails/readme.md) |

Install core plus only the add-ons you need. Each package is released on its own, so you can upgrade any one separately.

## Quick start

```sh
dotnet add package GovUK.Dfe.AI.Agents
```

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddMcpServers()     // optional add-ons
    .AddAISearch());

// In any service, inject IAgentService (and IContextRetriever for search):
var evidence = await search.GetContextAsync("ofsted_index", $"URN {urn}", filter: $"urn eq {urn}", cancellationToken: ct);
var result = await agents.RunAsync(BriefingAgents.Ofsted, "Summarise the latest inspection.", evidence, ct);
```

Start with the [core readme](src/GovUK.Dfe.AI.Agents/readme.md): configuration, defining agents, running them, and the
Azure roles each identity needs.

## UK Government AI Playbook

The libraries help services meet the
[AI Playbook for the UK Government](https://www.gov.uk/government/publications/ai-playbook-for-the-uk-government).
Some principles are the service team's responsibility, whatever library it uses.

| Principle                        | The libraries provide                                                                                                                     | Your service must still                                                                |
| -------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| 1. Know AI's limits              | Answer checks, required citations, a release gate                                                                                         | Tell users what the tool can't do                                                      |
| 2. Lawful and ethical            | Personal data redaction; nothing sensitive in telemetry; nothing stored in Foundry                                                        | Complete a DPIA and equality impact assessment                                         |
| 3. Secure                        | Entra ID only; tools denied by default; evidence fenced against prompt injection; model HTML shown as text; guardrails checked at startup | Threat model and Secure by Design review                                               |
| 4. Meaningful human control      | Approval before tools that change things; every answer traceable by run ID                                                                | Decide which decisions a person reviews, label AI answers, and collect users' feedback |
| 5. Full life cycle               | Agent versions and pins, drift warnings, release gate against a baseline, live quality scores                                             | Plan ownership, model upgrades and retirement                                          |
| 6. Right tool                    | Token and cost metrics per app and agent; cost caps per run                                                                               | Show AI beats a simpler option                                                         |
| 7. Open and collaborative        | Open source; OpenTelemetry standard names                                                                                                 | Publish an ATRS record                                                                 |
| 8–10. Commercial, skills, policy | Quota and concurrency limits; readmes                                                                                                     | Contracts, training, DfE AI governance                                                 |

Bias: give release-gate test cases a `group`, and fail the gate when one group is served worse
(`report.GroupGaps(...)`). See [Responsible AI](src/GovUK.Dfe.AI.Agents/readme.md#responsible-ai) in the core readme.

## Build and test

Requires the .NET 10 SDK. Tests use an in-memory Foundry, so they need no Azure resources.

```sh
dotnet build GovUK.Dfe.AI.Agents.slnx
dotnet test GovUK.Dfe.AI.Agents.slnx
```

### Live checks before a release

`LiveFoundryTests` run against a dev Foundry project, and are skipped otherwise. They check that Foundry keeps each
agent version's guardrail (so versions are reused), and that a jailbreak is reported as a guardrail block. If a gateway
blocks it instead, the failure shows the status and error code to add to `Foundry:GatewayGuardrailErrors`.

```sh
az login
export AiAgents__Foundry__Endpoint="https://<resource>.services.ai.azure.com/api/projects/<project>"
export AiAgents__Foundry__DefaultModel="<connection>/gpt-5.1"
export AiAgents__Foundry__Guardrail="<guardrail>"
dotnet test src/Tests/GovUK.Dfe.AI.Agents.Tests --filter "FullyQualifiedName~LiveFoundryTests"
```

## Releasing

Each package has its own workflow, `.github/workflows/build-deploy-ai-agents[-{addon}].yml`, which runs when that
package's folder changes. It builds, tests, packs and pushes to NuGet.org, and creates a GitHub release. SonarCloud
analysis runs once for the whole solution, with coverage from every test project, in
[sonarcloud.yml](.github/workflows/sonarcloud.yml).

- **Pull request:** publishes a prerelease, e.g. `1.0.5-prerelease.3`.
- **Merge to `main`:** publishes a release, e.g. `1.0.5`.
- **Version:** worked out by [GitVersion](GitVersion.yml) from the package's own `{PackageName}-X.Y.Z` tags. For a
  minor or major bump, tag the new base version on your branch, e.g.
  `git tag GovUK.Dfe.AI.Agents.Mcp-2.0.0 && git push origin <branch> --tags`.
- **Release notes:** add `(%release-note: your notes %)` to the body of the commit being released.

### Releasing an add-on on its own

Add-ons reference core as a project, so you can change both in one pull request and test them together. When an add-on
is packed, it's built against the **published** core at the version in its `.csproj`:

```xml
<CoreMinimumVersion>1.2.0</CoreMinimumVersion>
```

Its package then depends on `GovUK.Dfe.AI.Agents >= 1.2.0`, so:

- **An add-on change** releases only that add-on.
- **A core change** releases only core. Released add-ons keep working with it.
- **An add-on that needs something new in core** needs core released first, then `CoreMinimumVersion` raised to that
  version. That's the only case with two releases.

## Compatibility rules

Apps can upgrade any package on its own because the packages follow Microsoft's
[.NET library guidance](https://learn.microsoft.com/dotnet/standard/library-guidance/). The checks are set up in
[src/Directory.Build.targets](src/Directory.Build.targets).

- **Add-ons use only core's public API.** They plug in through `IAgentsPackage` and `AgentsPackageContext`.
- **Add-ons own their settings.** Each one reads, checks and documents its own block under `AiAgents` (e.g.
  `AiAgents:Guardrails`), so a new add-on setting never needs a core release.
- **Breaking changes need a new major version.** Removing or changing anything public breaks apps that use it, so do
  it only in a major version. `dotnet pack` validates each package; to have it also catch breaking changes, set
  `PackageValidationBaselineVersion` in the `.csproj` to the package's last released version.
- **Dependencies are minimums.** Packages declare the lowest dependency versions that work. Renovate doesn't raise them
  for minor or patch releases, and NuGet audit fails restore on a high or critical vulnerability.

## Adding a package

1. Create `src/GovUK.Dfe.AI.Agents.{Name}` and its tests in `src/Tests/GovUK.Dfe.AI.Agents.{Name}.Tests` (the workflow
   finds tests by this name), and add both to [GovUK.Dfe.AI.Agents.slnx](GovUK.Dfe.AI.Agents.slnx).
2. Implement `IAgentsPackage`, and add an `Add{Name}(this AgentsBuilder agents)` method in the
   `Microsoft.Extensions.DependencyInjection` namespace that calls `agents.AddPackage(...)`. Use only core's public API.
   In `Register`, use the `AgentsPackageContext`:

   | To...                         | Call                                                                                       |
   | ----------------------------- | ------------------------------------------------------------------------------------------ |
   | Read your settings            | `context.Section.GetSection("{Name}")` (your block under `AiAgents`)                       |
   | Fail startup on a bad setting | `context.ReportProblem("{Name}:Setting")`: listed with every other problem                 |
   | Sign in to an Azure service   | `context.CredentialFor(...)`: code credential, your `Authentication` block, or the default |
   | Give an agent tools           | `context.AddTools(agentName, sp => ...)`                                                   |
   | Tag your telemetry            | `context.ApplicationName`                                                                  |
   | Register services             | `context.Services`                                                                         |

   Set `CoreMinimumVersion` in the `.csproj` to the core version you build on.
3. Copy an add-on workflow to `.github/workflows/build-deploy-ai-agents-{name}.yml` and change the package name and paths.
4. Add a `readme.md`, which is packed into the NuGet package, and add the package to the tables here and in the core
   readme.

## Contributing

Branch from `main` and open a pull request. Keep the build free of warnings and SonarCloud issues, add tests for new
behaviour, and update the package readme when usage changes.

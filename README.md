# DfE AI Agents Libraries

.NET libraries for running Azure AI Foundry agents in DfE services: agents defined in code, checked answers with
citations, cost and concurrency limits, and token and cost telemetry. Run history stays in the app; agent definitions
and versions are stored in Foundry.

## Packages

| Package                          | What it does                                  | Docs                                                   |
| -------------------------------- | --------------------------------------------- | ------------------------------------------------------ |
| `GovUK.Dfe.AI.Agents`            | Core: define, version and run agents          | [readme](src/GovUK.Dfe.AI.Agents/Readme.md)            |
| `GovUK.Dfe.AI.Agents.Mcp`        | Tools from your own MCP servers               | [readme](src/GovUK.Dfe.AI.Agents.Mcp/Readme.md)        |
| `GovUK.Dfe.AI.Agents.AISearch`   | Evidence from Azure AI Search, with citations | [readme](src/GovUK.Dfe.AI.Agents.AISearch/Readme.md)   |
| `GovUK.Dfe.AI.Agents.Evaluation` | A judge model that scores answers             | [readme](src/GovUK.Dfe.AI.Agents.Evaluation/Readme.md) |
| `GovUK.Dfe.AI.Agents.Guardrails` | Foundry guardrails on your models or agents   | [readme](src/GovUK.Dfe.AI.Agents.Guardrails/Readme.md) |

Install core plus only the add-ons you need. Each package is released, and can be upgraded, on its own.

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

Start with the [core readme](src/GovUK.Dfe.AI.Agents/Readme.md): configuration, agents and runs.

## Docs

Guides that cover every package:

| Guide                                     | What's in it                                                                                   |
| ----------------------------------------- | ---------------------------------------------------------------------------------------------- |
| [Access](docs/Access.md)                  | How each package signs in to Azure, and the roles each identity needs                          |
| [Telemetry](docs/Telemetry.md)            | Every metric the packages record, and standard Application Insights queries to use in your app |
| [Limits](docs/Limits.md)                  | The limits a run works within, set by the library and by Azure AI Foundry                      |
| [Settings](docs/Settings.md)              | Every `AiAgents` setting in the core package, with its default                                 |
| [Responsible AI](docs/ResponsibleAI.md)   | How the library supports the UK Government AI Playbook                                         |
| [Security review](docs/SecurityReview.md) | Each trust boundary, what to check in your deployment, and a sign-off checklist                |

## UK Government AI Playbook

How the libraries help meet the
[AI Playbook for the UK Government](https://www.gov.uk/government/publications/ai-playbook-for-the-uk-government), and
what stays with the service team:

| Principle                        | The libraries provide                                                                                                                     | Your service must still                                                                |
| -------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| 1. Know AI's limits              | Answer checks, required citations, a release gate                                                                                         | Tell users what the tool can't do                                                      |
| 2. Lawful and ethical            | Personal data redaction; nothing sensitive in telemetry; nothing stored in Foundry                                                        | Complete a DPIA and equality impact assessment                                         |
| 3. Secure                        | Entra ID only; tools denied by default; evidence fenced against prompt injection; model HTML shown as text; guardrails checked at startup | Threat model and Secure by Design review ([checklist](docs/SecurityReview.md))         |
| 4. Meaningful human control      | Approval before tools that change things; every answer traceable by run ID                                                                | Decide which decisions a person reviews, label AI answers, and collect users' feedback |
| 5. Full life cycle               | Agent versions and pins, drift warnings, release gate against a baseline, live quality scores                                             | Plan ownership, model upgrades and retirement                                          |
| 6. Right tool                    | Token and cost metrics per app and agent; cost caps per run                                                                               | Show AI beats a simpler option                                                         |
| 7. Open and collaborative        | Open source; OpenTelemetry standard names                                                                                                 | Publish an ATRS record                                                                 |
| 8–10. Commercial, skills, policy | Quota and concurrency limits; readmes                                                                                                     | Contracts, training, DfE AI governance                                                 |

Bias: give release-gate test cases a `group` and set `MaxGroupGap`, so the gate fails when one group is served worse.
See [Responsible AI](docs/ResponsibleAI.md).

## Build and test

Requires the .NET 10 SDK. Tests use an in-memory Foundry, so they need no Azure resources.

```sh
dotnet build GovUK.Dfe.AI.Agents.slnx
dotnet test GovUK.Dfe.AI.Agents.slnx
```

## Releasing

Each package has its own workflow, `.github/workflows/build-deploy-ai-agents[-{addon}].yml`, run when its folder
changes: build, test, pack, push to NuGet.org and create a GitHub release.
[sonarcloud.yml](.github/workflows/sonarcloud.yml) analyses the whole solution once, with coverage from every test project.

- **Pull request:** publishes a prerelease, e.g. `1.0.5-prerelease.3`.
- **Merge to `main`:** publishes a release, e.g. `1.0.5`.
- **Version:** [GitVersion](GitVersion.yml) works it out from the package's `{PackageName}-X.Y.Z` tags. For a minor
  or major bump, tag the new base version on your branch: `git tag GovUK.Dfe.AI.Agents.Mcp-2.0.0 && git push --tags`.
- **Release notes:** add `(%release-note: your notes %)` to the body of the commit being released.

### Releasing an add-on on its own

Add-ons reference core as a project, so both are tested together. When packed, an add-on is built against the
**published** core at the version in its `.csproj`:

```xml
<CoreMinimumVersion>1.2.0</CoreMinimumVersion>
```

Its package then depends on `GovUK.Dfe.AI.Agents >= 1.2.0`, so:

- **An add-on change** releases only that add-on.
- **A core change** releases only core. Released add-ons keep working with it.
- **An add-on that needs something new in core** needs two pull requests: release core first, then raise
  `CoreMinimumVersion`.

## Compatibility rules

Apps can upgrade any package on its own because the packages follow Microsoft's
[.NET library guidance](https://learn.microsoft.com/dotnet/standard/library-guidance/):

- **Add-ons use only core's public API.** They plug in through `IAgentsPackage` and `AgentsPackageContext`.
- **Add-ons own their settings.** Each one reads, checks and documents its own block under `AiAgents` (e.g.
  `AiAgents:Guardrails`), so a new add-on setting never needs a core release.
- **Breaking changes need a new major version.** Removing or changing anything public breaks apps that use it.
- **Dependencies are minimums.** Packages declare the lowest versions that work; Renovate doesn't raise them for minor
  or patch releases. Raise one when NuGet audit reports a vulnerability.

## Adding a package

1. Create `src/GovUK.Dfe.AI.Agents.{Name}` and `src/Tests/GovUK.Dfe.AI.Agents.{Name}.Tests` (the workflow finds tests
   by this name), and add both to [GovUK.Dfe.AI.Agents.slnx](GovUK.Dfe.AI.Agents.slnx).
2. Implement `IAgentsPackage`, and add `Add{Name}(this AgentsBuilder agents)` in the
   `Microsoft.Extensions.DependencyInjection` namespace, calling `agents.AddPackage(...)`. In `Register`, use only
   core's public API through `AgentsPackageContext`:

   | To...                         | Call                                                                                       |
   | ----------------------------- | ------------------------------------------------------------------------------------------ |
   | Read your settings            | `context.Section.GetSection("{Name}")` (your block under `AiAgents`)                       |
   | Fail startup on a bad setting | `context.ReportProblem("{Name}:Setting")`: listed with every other problem                 |
   | Sign in to an Azure service   | `context.CredentialFor(...)`: code credential, your `Authentication` block, or the default |
   | Give an agent tools           | `context.AddTools(agentName, sp => ...)`                                                   |
   | Tag your telemetry            | `context.ApplicationName`                                                                  |
   | Register services             | `context.Services`                                                                         |

   Set `CoreMinimumVersion` in the `.csproj` to the core version you build on.
3. Copy an add-on workflow to `.github/workflows/build-deploy-ai-agents-{name}.yml`, changing the name and paths.
4. Add a `Readme.md` (it's packed into the package), and add the package to the tables here and in the core readme.

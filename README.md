# DfE AI Agents Libraries

.NET libraries for running Azure AI Foundry agents in DfE services, with answer checks, cost and concurrency limits and
token telemetry. Optional add-ons provide MCP tools, Azure AI Search evidence, answer scoring and guardrails.

## Packages

| Package | What it does | Docs |
| --- | --- | --- |
| `GovUK.Dfe.AI.Agents` | Core: define, version and run agents | [readme](src/GovUK.Dfe.AI.Agents/readme.md) |
| `GovUK.Dfe.AI.Agents.Mcp` | Tools from your own MCP servers | [readme](src/GovUK.Dfe.AI.Agents.Mcp/readme.md) |
| `GovUK.Dfe.AI.Agents.AISearch` | Evidence from Azure AI Search | [readme](src/GovUK.Dfe.AI.Agents.AISearch/readme.md) |
| `GovUK.Dfe.AI.Agents.Evaluation` | A judge model that scores answers | [readme](src/GovUK.Dfe.AI.Agents.Evaluation/readme.md) |
| `GovUK.Dfe.AI.Agents.Guardrails` | Foundry guardrails on your model deployments | [readme](src/GovUK.Dfe.AI.Agents.Guardrails/readme.md) |

Install core plus only the add-ons you need. Each package is versioned and released on its own, so you can upgrade core
or any add-on separately.

## Quick start

```sh
dotnet add package GovUK.Dfe.AI.Agents
```

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddMcpServers()     // optional add-ons
    .AddAISearch());

// In any service, inject IAgentService:
var result = await agents.RunAsync(BriefingAgents.Ofsted, "Summarise the latest inspection.", evidence, ct);
```

See the [core readme](src/GovUK.Dfe.AI.Agents/readme.md) for configuration, defining agents and running them.

## Build and test

Requires the .NET 10 SDK. Tests use an in-memory Foundry, so they need no Azure resources.

```sh
dotnet build GovUK.Dfe.AI.Agents.slnx
dotnet test GovUK.Dfe.AI.Agents.slnx
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

After each release, update the package's [public API files](#public-api-files).

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

> [!IMPORTANT]
> **Before core's first release,** add-ons can't be packed: `CoreMinimumVersion` is empty, and packing stops with an
> error rather than publish a package nobody can restore. Release core, then set `CoreMinimumVersion` in each add-on.

## Compatibility rules

Apps can upgrade any package on its own because the packages follow Microsoft's
[.NET library guidance](https://learn.microsoft.com/dotnet/standard/library-guidance/). The checks are set up in
[src/Directory.Build.targets](src/Directory.Build.targets).

- **Add-ons use only core's public API.** They plug in through `IAgentsPackage` and `AgentsPackageContext`.
- **Add-ons own their settings.** Each one reads, checks and documents its own block under `AiAgents` (e.g.
  `AiAgents:Guardrails`), so a new add-on setting never needs a core release.
- **Public API is tracked.** Each package lists its public API in two files, and the build fails if the code doesn't
  match (see [Public API files](#public-api-files)). A breaking change needs a new major version.
- **Dependencies are minimums.** Packages declare the lowest dependency versions that work. Renovate doesn't raise them
  for minor or patch releases, and NuGet audit fails restore on a high or critical vulnerability.

### Public API files

Each package has a `PublicAPI.Shipped.txt` and a `PublicAPI.Unshipped.txt` next to its `.csproj`. They list every public
type and member, one per line, so a change that could break apps or add-ons fails the build and shows up in the pull
request diff.

| File | Holds |
| --- | --- |
| `PublicAPI.Shipped.txt` | Public API in a released version, which apps may depend on |
| `PublicAPI.Unshipped.txt` | Public API added since the last release |

| You... | The build fails with | Fix |
| --- | --- | --- |
| Add or make something public | `RS0016` (not part of the declared public API) | Run `dotnet format analyzers <project.csproj> --diagnostics RS0016`, which adds the lines to `PublicAPI.Unshipped.txt` |
| Remove, rename or change something public | `RS0017` (part of the declared API, but not found) | Delete the line the error quotes. There's no automatic fix |

In review, a line removed from `PublicAPI.Shipped.txt` is a breaking change: the package needs a new major version. If
something shouldn't be public, make it `internal` rather than list it.

After a release, move the package's `PublicAPI.Unshipped.txt` lines into `PublicAPI.Shipped.txt` (keep
`#nullable enable` as the first line of both), and set `PackageValidationBaselineVersion` in its `.csproj` to the
version just released.

## Adding a package

1. Create `src/GovUK.Dfe.AI.Agents.{Name}` and its tests in `src/Tests/GovUK.Dfe.AI.Agents.{Name}.Tests` (the workflow
   finds tests by this name), and add both to [GovUK.Dfe.AI.Agents.slnx](GovUK.Dfe.AI.Agents.slnx).
2. Implement `IAgentsPackage`, and add an `Add{Name}(this AgentsBuilder agents)` method in the
   `Microsoft.Extensions.DependencyInjection` namespace that calls `agents.AddPackage(...)`. Use only core's public API.
   In `Register`, use the `AgentsPackageContext`:

   | To... | Call |
   | --- | --- |
   | Read your settings | `context.Section.GetSection("{Name}")` (your block under `AiAgents`) |
   | Fail startup on a bad setting | `context.ReportProblem("{Name}:Setting")`: listed with every other problem |
   | Sign in to an Azure service | `context.CredentialFor(...)`: code credential, your `Authentication` block, or the default |
   | Give an agent tools | `context.AddTools(agentName, sp => ...)` |
   | Tag your telemetry | `context.ApplicationName` |
   | Register services | `context.Services` |

   Set `CoreMinimumVersion` in the `.csproj` to the core version you build on.
3. Add the [public API files](#public-api-files) next to the `.csproj`. Both are needed, even while empty, and each
   starts with this line:

   ```text
   #nullable enable
   ```

   Then run `dotnet format analyzers src/GovUK.Dfe.AI.Agents.{Name}/GovUK.Dfe.AI.Agents.{Name}.csproj --diagnostics RS0016`
   to list your public API in `PublicAPI.Unshipped.txt`, and check it holds only what apps should use. Commit both files.
4. Copy an add-on workflow to `.github/workflows/build-deploy-ai-agents-{name}.yml` and change the package name and paths.
5. Add a `readme.md`, which is packed into the NuGet package, and add the package to the tables here and in the core
   readme.

## Contributing

Branch from `main` and open a pull request. Keep the build free of warnings and SonarCloud issues, add tests for new
behaviour, and update the package readme when usage changes.

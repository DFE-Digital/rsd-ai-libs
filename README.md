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

var result = await agentService.RunAsync(BriefingAgents.Ofsted, "Summarise the latest inspection.", evidence, ct);
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
package's folder changes. It builds, tests, runs SonarCloud analysis, packs and pushes to NuGet.org, and creates a
GitHub release.

- **Pull request:** publishes a prerelease, e.g. `1.0.5-prerelease.3`.
- **Merge to `main`:** publishes a release, e.g. `1.0.5`.
- **Version:** worked out by [GitVersion](GitVersion.yml) from the package's own `{PackageName}-X.Y.Z` tags. For a
  minor or major bump, tag the new base version on your branch, e.g.
  `git tag GovUK.Dfe.AI.Agents.Mcp-2.0.0 && git push origin <branch> --tags`.
- **Release notes:** add `(%release-note: your notes %)` to the body of the commit being released.

After each release, move the package's `PublicAPI.Unshipped.txt` entries into `PublicAPI.Shipped.txt`, and set its
`PackageValidationBaselineVersion` to the version just released.

> [!IMPORTANT]
> **Before the first release of core:** the add-ons reference core as a project, so their pack step is blocked on
> purpose (the `RequireCorePackageReference` target in each add-on `.csproj`). Once core is on NuGet.org, in each
> add-on, replace the `ProjectReference` with `<PackageReference Include="GovUK.Dfe.AI.Agents" Version="X.Y.Z" />`
> (the lowest core version it needs) and delete that target.

## Compatibility rules

Apps can upgrade any package on its own because the packages follow Microsoft's
[.NET library guidance](https://learn.microsoft.com/dotnet/standard/library-guidance/). The checks are set up in
[src/Directory.Build.targets](src/Directory.Build.targets).

- **Add-ons use only core's public API.** They plug in through `IAgentsPackage` and `AgentsPackageContext`.
- **Public API is tracked.** Each package lists its public API in `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`,
  and the build fails if the code doesn't match. After changing public API, run
  `dotnet format analyzers <project> --diagnostics RS0016 RS0017` and review the diff. A breaking change needs a new
  major version.
- **Dependencies are minimums.** Packages declare the lowest dependency versions that work. Renovate doesn't raise them
  for minor or patch releases, and NuGet audit fails restore on a high or critical vulnerability.

## Adding a package

1. Create `src/GovUK.Dfe.AI.Agents.{Name}` and its tests in `src/Tests/GovUK.Dfe.AI.Agents.{Name}.Tests` (the workflow
   finds tests by this name), and add both to [GovUK.Dfe.AI.Agents.slnx](GovUK.Dfe.AI.Agents.slnx).
2. Implement `IAgentsPackage`, and add an `Add{Name}(this AgentsBuilder agents)` method in the
   `Microsoft.Extensions.DependencyInjection` namespace that calls `agents.AddPackage(...)`. Use only core's public API.
3. Add `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` (each starting with `#nullable enable`), then run
   `dotnet format analyzers <project> --diagnostics RS0016`.
4. Copy an add-on workflow to `.github/workflows/build-deploy-ai-agents-{name}.yml` and change the package name and paths.
5. Add a `readme.md`, which is packed into the NuGet package, and add the package to the tables here and in the core
   readme.

## Contributing

Branch from `main` and open a pull request. Keep the build free of warnings and SonarCloud issues, add tests for new
behaviour, and update the package readme when usage changes.

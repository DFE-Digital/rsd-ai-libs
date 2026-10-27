# Access

How each package signs in to Azure, and the roles its identity needs.

## Credentials

`AiAgents:Authentication` is the default identity. Any service can have its own `Authentication` block: `Foundry`,
`GlobalConcurrency`, `ExternallyManagedAgents`, and each add-on's section (`AISearch`, `Guardrails`, and each server
under `McpServers`). For managed identities, set credentials in code:

```csharp
agents.UseCredential(new ManagedIdentityCredential())                     // default for every service
      .UseCredentialFor(AzureCredentialTarget.Foundry, foundryCredential)   // one core service
      .UseExternallyManagedAgentsCredential(centralCredential)            // the central agents' project
      .UseAISearchCredential(searchCredential)                            // add-ons have their own
      .UseGuardrailsCredential(armCredential)
      .UseMcpCredential("school-performance", mcpCredential);
```

A service uses its code credential, else its own block, else the default. Load secrets from Key Vault, never
appsettings.json, e.g. `AiAgents--Foundry--Authentication--ClientSecret`.

## Roles

Assign in Azure (**Access control (IAM)**) to each identity, e.g. the app's managed identity. Use the narrowest scope:
**Foundry User on the project** is all most apps need.

| Role                           | Scope                                       | Needed by                                               | Why                                                          |
| ------------------------------ | ------------------------------------------- | ------------------------------------------------------- | ------------------------------------------------------------ |
| Foundry User                   | Foundry project                             | Every app, and a provisioning job                       | Run agents, create versions, call the Evaluation judge model |
| Foundry User                   | Foundry resource (instead of the project)   | Apps with Evaluation `PublishToFoundry: true`           | Run Foundry evaluations, shown on the Evaluations page       |
| Foundry User                   | The central Foundry project                 | Apps using `ExternallyManagedAgents` with an `Endpoint` | Run the central agents                                       |
| Storage Blob Data Contributor  | Storage account, or just the container      | Apps using `GlobalConcurrency`                          | Hold run slots across instances                              |
| Search Index Data Reader       | Search service                              | Apps using AISearch                                     | Read evidence from the indexes                               |
| The MCP server's app role      | The server's app registration (its `Scope`) | Apps using Mcp                                          | Call the server's tools                                      |
| Reader                         | Foundry resource (not the project)          | Apps using Guardrails                                   | Read the guardrail and deployments at startup                |
| Cognitive Services Contributor | Foundry resource                            | Apps using Guardrails `ScanPrompts` or `ScanEvidence` and a job that calls `ApplyAsync` | Scan input with Prompt Shields and Create the guardrail; not needed if Bicep or Terraform does  |

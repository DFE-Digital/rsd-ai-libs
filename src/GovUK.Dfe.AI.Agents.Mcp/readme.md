# GovUK.Dfe.AI.Agents.Mcp

Gives your [GovUK.Dfe.AI.Agents](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md)
agents tools from your own MCP servers. Tools run in your app with your credential; Foundry sees only each tool's
name, description and input schema.

## Set up

```sh
dotnet add package GovUK.Dfe.AI.Agents.Mcp
```

```json
"AiAgents": {
  "McpServers": {
    "school-performance": {
      "ServerUri": "https://mcp.internal.example/mcp",
      "Scope": "api://school-performance/.default",
      "AllowedToolNames": [ "get_performance_data" ]
    }
  }
}
```

```csharp
builder.Services.AddAgents(builder.Configuration, agents => agents
    .AddAgents(BriefingAgents.All)
    .AddMcpServers());
```

Add an entry per server, then list the tools each agent may call:

```csharp
public static readonly AgentDefinition Ofsted = new("ofsted-agent", "Ofsted")
{
    AllowedTools = ["get_performance_data"],
};
```

### Use MCP servers without agents

An app can call MCP tools itself, with no Foundry settings and no agents:

```csharp
builder.Services.AddMcpServers(builder.Configuration, new ManagedIdentityCredential());

public sealed class PerformanceService([FromKeyedServices("school-performance")] IMcpToolClient server)
{
    public Task<string> GetAsync(string urn, CancellationToken ct)
        => server.CallToolAsync("get_performance_data", $$"""{"urn":"{{urn}}"}""", ct);
}
```

It reads the same section. Each server signs in with its own `Authentication` block, else the credential you pass,
and only its `AllowedToolNames` can be called. Adding agents later still connects each server once.

## How it works

- **Deny by default:** an agent can call a tool only if it's in both the server's `AllowedToolNames` and the agent's
  `AllowedTools`. This is checked on every call.
- **Sign-in:** a cached token for each server's `Scope`. For another identity, add the server's `Authentication` block
  or call `agents.UseMcpCredential("school-performance", credential)`.
- **Safe output:** cut at `MaxToolOutputCharacters` (default 40,000) and fenced as data.
- **Errors** go back to the model. Calls are never retried, so side effects can't happen twice.
- **Startup check:** fails if an allowed tool doesn't exist; only warns if a server can't be reached.

## Settings

Each entry under `McpServers` is keyed by a name you choose.

| Setting                 | Default              | Purpose                                                                                               |
| ----------------------- | -------------------- | ----------------------------------------------------------------------------------------------------- |
| `ServerUri`             | Required             | The server's MCP endpoint; `https://` (`http://` only for localhost), as each request carries a token |
| `Scope`                 | Required             | The token scope, e.g. `api://school-performance/.default`                                             |
| `AllowedToolNames`      | Required             | Every tool this app may use from the server                                                           |
| `ToolListCacheDuration` | 5 minutes            | How long the server's tool list is reused                                                             |
| `Authentication`        | The default identity | A separate identity for this server, possibly in another tenant                                       |

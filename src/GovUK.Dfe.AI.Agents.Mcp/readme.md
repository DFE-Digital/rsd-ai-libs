# GovUK.Dfe.AI.Agents.Mcp

Gives your [GovUK.Dfe.AI.Agents](https://github.com/DFE-Digital/rsd-ai-libs/blob/main/src/GovUK.Dfe.AI.Agents/readme.md)
agents tools from your own MCP servers. Tools run in your app with your credential. Foundry only sees each tool's name,
description and input schema, never the server's address or a credential.

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

public static readonly AgentDefinition Ofsted = new("ofsted-agent", "Ofsted")
{
    AllowedTools = ["get_performance_data"],
};
```

## How it works

- **Deny by default:** an agent can call a tool only if it's in both the server's `AllowedToolNames` and the agent's
  `AllowedTools`. This is checked on every call.
- **Sign-in:** the app's identity gets a token for each server's `Scope`; tokens are cached and refreshed. To use a
  different identity, add an `Authentication` block to the server, or call
  `agents.UseMcpCredential("school-performance", credential)`.
- **Safe output:** tool output is cut at `MaxToolOutputCharacters` (default 20,000) and fenced as data, so the model
  doesn't follow instructions inside it.
- **Errors:** a tool error is returned to the model. Calls are never retried, so side effects can't happen twice.
- **Startup check:** if an allowed tool doesn't exist on its server, startup fails. If a server can't be reached,
  startup only logs a warning, so an outage doesn't stop new instances starting.

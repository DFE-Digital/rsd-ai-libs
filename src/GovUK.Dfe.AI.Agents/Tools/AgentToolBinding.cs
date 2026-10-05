using GovUK.Dfe.AI.Agents.Tools.Interfaces;

namespace GovUK.Dfe.AI.Agents.Tools;

/// <summary>Gives an agent a tool provider.</summary>
internal sealed record AgentToolBinding(string AgentName, IAgentToolProvider Provider);

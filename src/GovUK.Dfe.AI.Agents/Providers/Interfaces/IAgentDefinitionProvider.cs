using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Providers.Interfaces;

/// <summary>This app's agents.</summary>
internal interface IAgentDefinitionProvider
{
    /// <summary>This app's agents.</summary>
    IReadOnlyCollection<AgentDefinition> GetAgentsDefinitions();
}

using GovUK.Dfe.AI.Agents.Providers.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;

/// <summary>Agent definitions for tests; stands in for what <c>AddAgents</c> registers.</summary>
internal sealed class FakeAgentDefinitions : IAgentDefinitionProvider
{
    public IReadOnlyCollection<AgentDefinition> Definitions { get; set; } = [];

    public IReadOnlyCollection<AgentDefinition> GetAgentsDefinitions() => Definitions;
}

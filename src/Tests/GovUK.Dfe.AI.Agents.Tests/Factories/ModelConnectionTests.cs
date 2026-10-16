using Azure.AI.Projects.Agents;
using GovUK.Dfe.AI.Agents.Factories;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Factories;

/// <summary>
/// When <c>Foundry:DefaultModel</c> is on a connection, every agent version uses a model on it; and every version gets the
/// guardrail when one is set.
/// </summary>
public sealed class ModelConnectionTests
{
    private const string Guardrail = "briefing-guardrail";
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private readonly InMemoryFoundry _foundry = new();
    private static readonly AgentSpec Spec = new() { Name = "ofsted-agent", Instructions = "You analyse Ofsted reports." };

    private FoundryAgentFactory Factory(string? guardrail = Guardrail)
        => new(_foundry.Admin, new FoundryAgentFactoryOptions("myconnection/gpt-5.1") { Guardrail = guardrail });

    [Fact]
    public async Task Versions_AndTestCopies_AreCreatedWithTheConnectionModel_AndTheGuardrail()
    {
        var factory = Factory();
        var testCopy = Spec with { Name = $"ofsted-agent-{Guid.NewGuid():N}" };   // as RunEphemeralAsync names it

        await factory.GetOrCreateAsync(Spec, cancellationToken);
        await factory.GetOrCreateAsync(testCopy, cancellationToken);

        foreach (var name in new[] { Spec.Name, testCopy.Name })
        {
            var definition = _foundry.Definition(name, "1");
            Assert.Equal(("myconnection/gpt-5.1", Guardrail), (definition.Model, definition.ContentFilterConfiguration?.RaiPolicyName));
        }

        Assert.Equal(Guardrail, await factory.GetGuardrailAsync(Spec.Name, cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task AVersionWithoutTheGuardrail_IsntReused()
    {
        _foundry.Seed(Spec.Name, "myconnection/gpt-5.1", Spec.Instructions);

        var agent = await Factory().GetOrCreateAsync(Spec, cancellationToken);

        Assert.Equal("2", agent.Version);
        Assert.Null(_foundry.Definition(Spec.Name, "1").ContentFilterConfiguration);
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("otherconnection/gpt-4o")]
    public async Task AModelOffTheDefaultModelsConnection_IsRefused_BeforeAnyVersionIsCreated(string model)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Factory().GetOrCreateAsync(Spec with { Model = model }, cancellationToken));

        Assert.Equal($"Agent 'ofsted-agent' can't use model '{model}': every model must be on the connection in " +
            "Foundry:DefaultModel, as 'myconnection/<model>'.", ex.Message);
        Assert.Empty(_foundry.CreatedNames);
    }

    [Fact]
    public async Task AnotherModelOnTheSameConnection_IsAllowed_AndTheGuardrailIsOptional()
    {
        await Factory(guardrail: null).GetOrCreateAsync(Spec with { Model = "MyConnection/gpt-4o" }, cancellationToken);

        var definition = _foundry.Definition(Spec.Name, "1");
        Assert.Equal(("MyConnection/gpt-4o", (ContentFilterConfiguration?)null), (definition.Model, definition.ContentFilterConfiguration));
    }

    [Fact]
    public async Task ADefaultModelWithNoConnection_AllowsAnyModel()
    {
        var factory = new FoundryAgentFactory(_foundry.Admin, new FoundryAgentFactoryOptions("gpt-4o"));

        await factory.GetOrCreateAsync(Spec with { Model = "otherconnection/gpt-5.1" }, cancellationToken);

        Assert.Equal("otherconnection/gpt-5.1", _foundry.Definition(Spec.Name, "1").Model);
    }
}

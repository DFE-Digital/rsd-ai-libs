using GovUK.Dfe.AI.Agents.Extensions;
using GovUK.Dfe.AI.Agents.Factories;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.ValueObjects;
using System.Text.Json.Nodes;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests;

/// <summary>Answers: a strict schema, read back as a C# type, a new version only when the schema changes, and display names.</summary>
public sealed class AgentResultTests
{
    public sealed record OfstedFindings(string Rating, string? InspectionDate, IReadOnlyList<string> Strengths);

    [Fact]
    public void For_BuildsAStrictSchema_WithEveryPropertyRequired_AndNoExtraProperties()
    {
        var schema = JsonNode.Parse(AgentOutputSchema.For<OfstedFindings>("ofsted_findings").JsonSchema)!;

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["rating", "inspectionDate", "strengths"], schema["required"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void ReadOutputAs_ReadsAStructuredAnswer()
    {
        var result = new AgentResult("ofsted-agent", """{"rating":"Good","inspectionDate":"2024-03-12","strengths":["Leadership"]}""", 10);

        var findings = result.ReadOutputAs<OfstedFindings>();

        Assert.Equal("Good", findings.Rating);
        Assert.Equal(["Leadership"], findings.Strengths);
    }

    [Fact]
    public void ReadOutputAs_ExplainsTheProblem_WhenTheAgentFailed()
    {
        var fallback = new AgentResult("ofsted-agent", "This section could not be generated due to an error retrieving or analysing evidence.", 0);

        var ex = Assert.Throws<InvalidOperationException>(() => fallback.ReadOutputAs<OfstedFindings>());

        Assert.Contains("ofsted-agent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSchema_IsStoredOnTheAgent_AndOnlyAChangeToItCreatesANewVersion()
    {
        var foundry = new InMemoryFoundry();
        var factory = new FoundryAgentFactory(foundry.Admin, new FoundryAgentFactoryOptions("gpt-4o"));
        var spec = new AgentSpec { Name = "ofsted-agent", Instructions = "x", OutputSchema = AgentOutputSchema.For<OfstedFindings>("ofsted_findings") };

        await factory.GetOrCreateAsync(spec, cancellationToken: TestContext.Current.CancellationToken);
        await factory.GetOrCreateAsync(spec, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(foundry.Versions("ofsted-agent"));
        Assert.NotNull(foundry.Definition("ofsted-agent", "1").TextOptions);

        await factory.GetOrCreateAsync(spec with { OutputSchema = AgentOutputSchema.For<AgentResult>("other") }, cancellationToken: TestContext.Current.CancellationToken);
        await factory.GetOrCreateAsync(spec with { OutputSchema = null }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, foundry.Versions("ofsted-agent").Count);
    }

    [Theory]
    [InlineData("trust-agent", "Trust")]
    [InlineData("rise-concerns-agent", "Rise Concerns")]
    [InlineData("web-search-agent", "Web Search")]
    [InlineData("no-suffix", "No Suffix")]
    public void ToDisplayName_FormatsKebabCaseAgentNames(string agentName, string expected)
        => Assert.Equal(expected, agentName.ToDisplayName());
}

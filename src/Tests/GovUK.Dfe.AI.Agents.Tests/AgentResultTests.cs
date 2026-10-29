using GovUK.Dfe.AI.Agents.Extensions;
using GovUK.Dfe.AI.Agents.Factories;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.ValueObjects;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

    public sealed record Inspection(string Rating, Judgement Leadership, IReadOnlyList<Judgement> Areas, NoDetail Extra);

    public sealed record Judgement(string Area, int Grade);

    /// <summary>An object with no properties in its schema: its one property isn't serialized.</summary>
    public sealed record NoDetail([property: JsonIgnore] string? Note = null);

    public sealed record ScoresByArea(string Rating, Dictionary<string, int> Scores);

    public sealed record NestedScores(string Rating, IReadOnlyList<ScoresByArea> Sections);

    [Fact]
    public void For_MakesEveryObjectStrict_AtAnyDepth_IncludingOneWithNoProperties()
    {
        var schema = JsonNode.Parse(AgentOutputSchema.For<Inspection>("inspection").JsonSchema)!;
        var properties = schema["properties"]!;

        JsonNode[] objects = [schema, properties["leadership"]!, properties["areas"]!["items"]!, properties["extra"]!];
        Assert.All(objects, node => Assert.False(node["additionalProperties"]!.GetValue<bool>()));
        Assert.Equal(["area", "grade"], properties["leadership"]!["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Empty(properties["extra"]!["required"]!.AsArray());
        Assert.Empty(properties["extra"]!["properties"]!.AsObject());
    }

    [Fact]
    public void For_RefusesADictionary_NamingWhereItIs_RatherThanFailingWhenFoundryRejectsTheSchema()
    {
        var ex = Assert.Throws<NotSupportedException>(() => AgentOutputSchema.For<ScoresByArea>("scores"));

        Assert.Contains("ScoresByArea", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#/properties/scores", ex.Message, StringComparison.Ordinal);
        Assert.Contains("list of records with a key property", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_RefusesADictionary_NestedInAList()
    {
        var ex = Assert.Throws<NotSupportedException>(() => AgentOutputSchema.For<NestedScores>("sections"));

        Assert.Contains("#/properties/sections/items/properties/scores", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOutputAs_ReadsAStructuredAnswer()
    {
        var result = new AgentResult { AgentName = "ofsted-agent", Output = """{"rating":"Good","inspectionDate":"2024-03-12","strengths":["Leadership"]}""", TotalTokens = 10 };

        var findings = result.ReadOutputAs<OfstedFindings>();

        Assert.Equal("Good", findings.Rating);
        Assert.Equal(["Leadership"], findings.Strengths);
    }

    [Fact]
    public void ReadOutputAs_ExplainsTheProblem_WhenTheAgentFailed()
    {
        var fallback = new AgentResult { AgentName = "ofsted-agent", Output = "This section could not be generated due to an error retrieving or analysing evidence.", TotalTokens = 0 };

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
}

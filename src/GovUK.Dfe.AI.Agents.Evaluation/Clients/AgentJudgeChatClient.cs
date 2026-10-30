using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.AI;
using System.Net;

namespace GovUK.Dfe.AI.Agents.Evaluation.Clients;

/// <summary>
/// The judge run as an agent, used when <c>Foundry:DefaultModel</c> is on a connection: like every agent, it calls its
/// model through that connection, has the guardrail (if set), stores no run history, and shows in token and cost telemetry. It
/// has no tools.
/// </summary>
/// <remarks>
/// Each metric has one judge agent, named after what it scores, that's created once and then reused like any other:
/// scoring an answer costs one model call, not a create and a delete too. A new version is created only when the
/// evaluator's instructions or the guardrail change.
/// </remarks>
/// <param name="runner">Gets or creates the judge agent, then runs it.</param>
/// <param name="model">The judge model, e.g. "myconnection/gpt-5.1".</param>
/// <param name="metrics">What the judge scores, e.g. "Groundedness": it names the agent, so it's clear in Foundry.</param>
internal sealed class AgentJudgeChatClient(IAgentRunnerService runner, string model, IEnumerable<string> metrics) : JudgeChatClient
{
    internal const string AgentNamePrefix = "answer-judge";

    /// <summary>Foundry's longest agent name.</summary>
    private const int MaxNameLength = 63;

    /// <summary>What the judge scores, e.g. "answer-judge-groundedness", in letters, digits and hyphens.</summary>
    internal string AgentName { get; } = NameFor(metrics);

    /// <summary>Used only when an evaluator sends no system message.</summary>
    private const string DefaultInstructions = "You are an evaluator. Follow the instructions in the message.";

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
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

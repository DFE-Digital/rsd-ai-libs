using Azure.AI.Extensions.OpenAI;
using GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.Constants;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace GovUK.Dfe.AI.Agents.Evaluation.Clients;

/// <summary>A judge model called through this app's Foundry project, with no extra client or credential.</summary>
/// <remarks>
/// Sends only the model and messages, so reasoning models (e.g. gpt-5.1) work. Like agent runs, judge calls aren't stored
/// in Foundry, which matters as they carry the run's prompt, evidence and answer.
/// </remarks>
/// <param name="client">This app's Foundry project client.</param>
/// <param name="model">The judge model, e.g. "myconnection/gpt-5.1".</param>
/// <param name="telemetry">
/// Records each call's tokens and cost as a run of <see cref="AgentJudgeChatClient.AgentNamePrefix"/>, as a judge agent's are.
/// </param>
internal sealed class FoundryJudgeChatClient(ProjectOpenAIClient client, string model, IModelCallTelemetry? telemetry = null) : JudgeChatClient
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = new CreateResponseOptions(model, messages.Select(ToResponseItem)) { StoredOutputEnabled = false };
        using var call = telemetry?.Start(AgentJudgeChatClient.AgentNamePrefix);
        ResponseResult response;
        try
        {
            response = (await client.GetProjectResponsesClient().CreateResponseAsync(request, cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (Exception ex)
        {
            call?.Failed(ex);
            throw;
        }

        call?.Completed(response.Model, response.Usage is { } billed
            ? new TokenUsage(billed.InputTokenCount, billed.OutputTokenCount, billed.TotalTokenCount)
            {
                CachedInputTokens = billed.InputTokenDetails?.CachedTokenCount ?? 0,
            }
            : TokenUsage.None);

        // A failed or cut-short reply, e.g. one stopped at its token limit, could still hold something that reads as a
        // score; it's thrown instead, so the metric is left unscored with the reason. Its tokens are still recorded.
        if (Unfinished(response) is { } unfinished)
        {
            call?.Failed(unfinished);
            throw unfinished;
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, response.GetOutputText()))
        {
            ModelId = response.Model,
            Usage = response.Usage is { } usage
                ? new UsageDetails { InputTokenCount = usage.InputTokenCount, OutputTokenCount = usage.OutputTokenCount, TotalTokenCount = usage.TotalTokenCount }
                : null,
        };
    }

    /// <summary>Why the reply didn't finish, or null when it did. A reply with no status is taken as finished, as agent runs take it.</summary>
    private InvalidOperationException? Unfinished(ResponseResult response)
    {
        if (response.Error is null && (response.Status is null || response.Status == ResponseStatus.Completed))
        {
            return null;
        }

        var reason = response.Error?.Message ?? response.IncompleteStatusDetails?.Reason?.ToString() ?? "no reason given";
        return new InvalidOperationException(JudgeMessages.ReplyUnfinished(model, response.Status?.ToString(), reason));
    }

    private static ResponseItem ToResponseItem(ChatMessage message)
    {
        if (message.Role == ChatRole.System)
        {
            return ResponseItem.CreateSystemMessageItem(message.Text);
        }

        return message.Role == ChatRole.Assistant
            ? ResponseItem.CreateAssistantMessageItem(message.Text, annotations: null)
            : ResponseItem.CreateUserMessageItem(message.Text);
    }
}

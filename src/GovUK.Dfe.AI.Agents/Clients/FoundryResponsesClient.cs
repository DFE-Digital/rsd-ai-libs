using Azure.AI.Extensions.OpenAI;
using GovUK.Dfe.AI.Agents.Clients.Interfaces;
using OpenAI.Responses;

namespace GovUK.Dfe.AI.Agents.Clients;

/// <summary>
/// Foundry agent responses, through the project's OpenAI client. Stateless: nothing is stored, so the caller sends the
/// run's history each time. Encrypted reasoning is returned, so a reasoning model keeps its reasoning across tool rounds.
/// </summary>
internal sealed class FoundryResponsesClient(ProjectOpenAIClient client) : IFoundryResponsesClient
{
    public async Task<ResponseResult> CreateResponseAsync(string agentName, IReadOnlyList<ResponseItem> inputItems,
        string? agentVersion = null, int? maxOutputTokens = null, CancellationToken cancellationToken = default)
    {
        var response = await ResponsesClient(agentName, agentVersion)
            .CreateResponseAsync(Options(inputItems, maxOutputTokens), cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    public async Task<ResponseResult> StreamResponseAsync(string agentName, IReadOnlyList<ResponseItem> inputItems, Action<string> onText,
        string? agentVersion = null, int? maxOutputTokens = null, CancellationToken cancellationToken = default)
    {
        ResponseResult? response = null;
        var updates = ResponsesClient(agentName, agentVersion).CreateResponseStreamingAsync(Options(inputItems, maxOutputTokens), cancellationToken);
        await foreach (var update in updates.ConfigureAwait(false))
        {
            switch (update)
            {
                case StreamingResponseOutputTextDeltaUpdate text:
                    onText(text.Delta);
                    break;
                case StreamingResponseCompletedUpdate completed:
                    response = completed.Response;
                    break;
                case StreamingResponseIncompleteUpdate incomplete:
                    response = incomplete.Response;
                    break;
                case StreamingResponseFailedUpdate failed:
                    response = failed.Response;
                    break;
            }
        }

        return response ?? throw new InvalidOperationException($"Agent '{agentName}' stopped streaming before its response finished.");
    }

    private ProjectResponsesClient ResponsesClient(string agentName, string? agentVersion)
        => client.GetProjectResponsesClientForAgent(new AgentReference(agentName, version: agentVersion ?? ""));

    internal static CreateResponseOptions Options(IReadOnlyList<ResponseItem> inputItems, int? maxOutputTokens)
    {
        var options = new CreateResponseOptions
        {
            MaxOutputTokenCount = maxOutputTokens,
            StoredOutputEnabled = false,
            IncludedProperties = { IncludedResponseProperty.ReasoningEncryptedContent },
        };
        foreach (var item in inputItems)
        {
            options.InputItems.Add(item);
        }

        return options;
    }
}

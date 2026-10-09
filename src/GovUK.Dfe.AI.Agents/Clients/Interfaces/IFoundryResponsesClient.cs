using OpenAI.Responses;

namespace GovUK.Dfe.AI.Agents.Clients.Interfaces;

/// <summary>Sends input to a Foundry agent and returns its response. Nothing is stored in Foundry.</summary>
internal interface IFoundryResponsesClient
{
    /// <summary>Sends the run's history to an agent and returns its response.</summary>
    /// <param name="agentVersion">The version to run; null runs the latest.</param>
    /// <param name="maxOutputTokens">The most output tokens this response may use; null for the model's own limit.</param>
    Task<ResponseResult> CreateResponseAsync(string agentName, IReadOnlyList<ResponseItem> inputItems, string? agentVersion = null,
        int? maxOutputTokens = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="CreateResponseAsync"/>, but streamed: each piece of answer text goes to <paramref name="onText"/> as
    /// it's written, and the finished response is returned.
    /// </summary>
    Task<ResponseResult> StreamResponseAsync(string agentName, IReadOnlyList<ResponseItem> inputItems, Action<string> onText,
        string? agentVersion = null, int? maxOutputTokens = null, CancellationToken cancellationToken = default);
}

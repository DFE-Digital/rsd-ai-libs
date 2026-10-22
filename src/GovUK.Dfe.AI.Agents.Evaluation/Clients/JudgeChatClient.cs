using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace GovUK.Dfe.AI.Agents.Evaluation.Clients;

/// <summary>
/// What every judge shares: it answers in one piece, as judges don't need streaming, and it owns nothing, as the client it
/// calls is shared and owned by the container. Each judge says only how it calls the model.
/// </summary>
internal abstract class JudgeChatClient : IChatClient
{
    public abstract Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the whole answer as one update.</summary>
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
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Nothing to release by default: the client a judge calls is shared and owned by the container.</summary>
    protected virtual void Dispose(bool disposing)
    {
    }
}

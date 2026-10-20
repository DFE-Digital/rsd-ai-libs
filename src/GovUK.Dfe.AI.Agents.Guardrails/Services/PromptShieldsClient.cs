using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;

namespace GovUK.Dfe.AI.Agents.Guardrails.Services;

/// <summary>
/// Azure AI Content Safety Prompt Shields (<c>text:shieldPrompt</c>): finds jailbreak attempts in a question, and
/// instructions planted in documents. Prompt Shields takes up to 10,000 characters a call, so long evidence is scanned in
/// overlapping parts, a few at a time; an attack in any part counts. Fails closed: if something can't be scanned, it
/// throws, so the run fails.
/// </summary>
internal sealed class PromptShieldsClient : IDisposable
{
    internal const string ApiVersion = "2024-09-01";

    /// <summary>Prompt Shields' limit for one call.</summary>
    internal const int MaxCallLength = 10_000;

    /// <summary>Evidence per call, leaving room for the prompt within <see cref="MaxCallLength"/>.</summary>
    internal const int PartLength = 9_000;

    /// <summary>Repeated at the start of the next part, so an instruction split by a boundary is still seen whole.</summary>
    internal const int Overlap = 300;

    /// <summary>The prompt sent with each evidence part, for context.</summary>
    internal const int MaxPromptLength = 1_000;

    private const int MaxParallelCalls = 4;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly Uri _shieldPrompt;
    private readonly TokenCredential _credential;
    private readonly HttpClient _http;

    public PromptShieldsClient(Uri endpoint, TokenCredential credential, HttpMessageHandler? handler = null)
    {
        _shieldPrompt = new Uri(endpoint, $"contentsafety/text:shieldPrompt?api-version={ApiVersion}");
        _credential = credential;
        // Connections are renewed, so a long-running app follows DNS changes.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>Whether the question is a jailbreak attempt (a user prompt attack). Its first 10,000 characters are scanned.</summary>
    public async Task<bool> PromptIsAttackAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var result = await ScanAsync(prompt.Length <= MaxCallLength ? prompt : prompt[..MaxCallLength], [], cancellationToken)
            .ConfigureAwait(false);
        return result.UserPromptAnalysis?.AttackDetected
               ?? throw new InvalidOperationException(Constants.ErrorMessages.PromptShieldsNoResult);
    }

    /// <summary>Whether the evidence carries planted instructions (a document attack).</summary>
    public async Task<bool> EvidenceHasAttackAsync(string prompt, string evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(evidence);

        var userPrompt = prompt.Length <= MaxPromptLength ? prompt : prompt[..MaxPromptLength];
        var found = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(Parts(evidence), new ParallelOptions { MaxDegreeOfParallelism = MaxParallelCalls, CancellationToken = stop.Token },
                async (part, token) =>
                {
                    var result = await ScanAsync(userPrompt, [part], token).ConfigureAwait(false);
                    if (result.DocumentsAnalysis is not { Count: > 0 } documents)
                    {
                        throw new InvalidOperationException(Constants.ErrorMessages.PromptShieldsNoResult);
                    }

                    if (documents.Exists(static analysis => analysis.AttackDetected))
                    {
                        Interlocked.Exchange(ref found, 1);
                        await stop.CancelAsync().ConfigureAwait(false);   // one attack is enough; skip the rest
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (found == 1 && !cancellationToken.IsCancellationRequested)
        {
            // Stopped early because an attack was found.
        }

        return found == 1;
    }

    /// <summary>The evidence in parts of at most <see cref="PartLength"/> characters, each overlapping the last.</summary>
    internal static IEnumerable<string> Parts(string evidence)
    {
        for (var start = 0; start < evidence.Length; start += PartLength - Overlap)
        {
            var length = Math.Min(PartLength, evidence.Length - start);
            yield return evidence.Substring(start, length);
            if (start + length == evidence.Length)
            {
                yield break;   // the last part: without this, a short tail would be sent again inside the next part
            }
        }
    }

    /// <summary>One call. A rate limit or server error is retried, up to <see cref="MaxAttempts"/> calls in all.</summary>
    private async Task<ShieldResult> ScanAsync(string userPrompt, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        // Each attempt returns, waits and retries, or throws, so the loop ends within MaxAttempts.
        var attempt = 0;
        while (true)
        {
            attempt++;
            using var response = await SendAsync(userPrompt, documents, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<ShieldResult>(cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException(Constants.ErrorMessages.PromptShieldsNoResult);
            }

            if (attempt >= MaxAttempts || !IsTransient(response.StatusCode))
            {
                throw await FailureAsync(response, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string userPrompt, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, _shieldPrompt)
        {
            Content = JsonContent.Create(new ShieldRequest(userPrompt, documents)),
        };
        request.Headers.Authorization = new("Bearer", token.Token);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

    /// <summary>The service's <c>Retry-After</c>, else a second per attempt; at most 10 seconds.</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt);
        return wait < MaxRetryDelay ? wait : MaxRetryDelay;
    }

    private static async Task<HttpRequestException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new HttpRequestException(
            string.Format(Constants.ErrorMessages.PromptShieldsFailed, (int)response.StatusCode, detail), null, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();

    private sealed record ShieldRequest(
        [property: JsonPropertyName("userPrompt")] string UserPrompt,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents);

    private sealed record ShieldResult(
        [property: JsonPropertyName("userPromptAnalysis")] Analysis? UserPromptAnalysis,
        [property: JsonPropertyName("documentsAnalysis")] List<Analysis>? DocumentsAnalysis);

    private sealed record Analysis([property: JsonPropertyName("attackDetected")] bool AttackDetected);
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using GovUK.Dfe.AI.Agents.Safety.Interfaces;

namespace GovUK.Dfe.AI.Agents.Guardrails.Services;

/// <summary>
/// Scans evidence with Azure AI Content Safety Prompt Shields (<c>text:shieldPrompt</c>), which detects instructions
/// planted in documents. Prompt Shields takes up to 10,000 characters a call, so long evidence is scanned in overlapping
/// parts, a few at a time; an attack in any part blocks the run. Fails closed: if a part can't be scanned, the run fails.
/// </summary>
internal sealed class PromptShieldEvidenceShield : IEvidenceShield, IDisposable
{
    internal const string ApiVersion = "2024-09-01";

    /// <summary>Evidence per call, leaving room for the prompt within Prompt Shields' 10,000-character limit.</summary>
    internal const int PartLength = 9_000;

    /// <summary>Repeated at the start of the next part, so an instruction split by a boundary is still seen whole.</summary>
    internal const int Overlap = 300;

    internal const int MaxPromptLength = 1_000;

    private const int MaxParallelCalls = 4;
    private const int MaxAttempts = 3;
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly Uri _shieldPrompt;
    private readonly TokenCredential _credential;
    private readonly HttpClient _http;

    public PromptShieldEvidenceShield(Uri endpoint, TokenCredential credential, HttpMessageHandler? handler = null)
    {
        _shieldPrompt = new Uri(endpoint, $"contentsafety/text:shieldPrompt?api-version={ApiVersion}");
        _credential = credential;
        // Connections are renewed, so a long-running app follows DNS changes.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async ValueTask<bool> FindsAttackAsync(string prompt, string evidence, CancellationToken cancellationToken)
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
                    if (await ScanAsync(userPrompt, part, token).ConfigureAwait(false))
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
        for (var start = 0; ; start += PartLength - Overlap)
        {
            var length = Math.Min(PartLength, evidence.Length - start);
            yield return evidence.Substring(start, length);
            if (start + length >= evidence.Length)
            {
                yield break;
            }
        }
    }

    private async Task<bool> ScanAsync(string userPrompt, string document, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, _shieldPrompt)
            {
                Content = JsonContent.Create(new ShieldRequest(userPrompt, [document])),
            };
            request.Headers.Authorization = new("Bearer", token.Token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ShieldResult>(cancellationToken).ConfigureAwait(false);
                return result?.DocumentsAnalysis is { Count: > 0 } documents
                    ? documents.Exists(static analysis => analysis.AttackDetected)
                    : throw new InvalidOperationException(Constants.ErrorMessages.EvidenceShieldNoResult);
            }

            if (attempt < MaxAttempts && response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt);
                await Task.Delay(wait < TimeSpan.FromSeconds(10) ? wait : TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                string.Format(Constants.ErrorMessages.EvidenceShieldFailed, (int)response.StatusCode, detail), null, response.StatusCode);
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record ShieldRequest(
        [property: JsonPropertyName("userPrompt")] string UserPrompt,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents);

    private sealed record ShieldResult(
        [property: JsonPropertyName("documentsAnalysis")] List<DocumentAnalysis>? DocumentsAnalysis);

    private sealed record DocumentAnalysis([property: JsonPropertyName("attackDetected")] bool AttackDetected);
}

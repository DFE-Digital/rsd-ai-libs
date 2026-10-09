using OpenAI.Responses;
using System.Collections.Concurrent;
using System.ClientModel.Primitives;
using GovUK.Dfe.AI.Agents.Clients.Interfaces;

namespace GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;

/// <summary>A response call as Foundry would have received it.</summary>
internal sealed record RecordedResponseCall(string AgentName, string? AgentVersion, string SerializedInput, int? MaxOutputTokens = null);

/// <summary>
/// Plays back scripted Responses per agent name and records every call. Ephemeral agents are
/// matched by prefix, since the runtime suffixes their names with a GUID.
/// </summary>
internal sealed class ScriptedResponsesClient : IFoundryResponsesClient
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<CancellationToken, Task<ResponseResult>>>> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedResponseCall> _calls = new();
    private int _inFlight;
    private int _peakInFlight;

    public IReadOnlyList<RecordedResponseCall> Calls => [.. _calls];



    /// <summary>The most responses that were ever being generated at the same time.</summary>
    public int PeakConcurrentResponses => Volatile.Read(ref _peakInFlight);

    /// <summary>When set, deletes fail - to exercise clean-up failure handling.</summary>

    public IReadOnlyList<RecordedResponseCall> CallsFor(string agentName)
        => [.. _calls.Where(call => Matches(agentName, call.AgentName))];

    public void Reply(string agentName, params ResponseResult[] responses)
    {
        foreach (var response in responses)
        {
            Script(agentName).Enqueue(_ => Task.FromResult(response));
        }
    }

    public void ReplyAfter(string agentName, TimeSpan delay, ResponseResult response)
        => Script(agentName).Enqueue(async ct =>
        {
            await Task.Delay(delay, ct);
            return response;
        });

    /// <summary>Never answers; only the caller's cancellation (or a run timeout) ends the call.</summary>
    public void Hang(string agentName)
        => Script(agentName).Enqueue(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable.");
        });

    public void Fail(string agentName, Exception exception)
        => Script(agentName).Enqueue(_ => Task.FromException<ResponseResult>(exception));

    public async Task<ResponseResult> CreateResponseAsync(string agentName, IReadOnlyList<ResponseItem> inputItems, string? agentVersion = null,
        int? maxOutputTokens = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var input = string.Join(' ', inputItems.Select(item => ModelReaderWriter.Write(item).ToString()));
        _calls.Enqueue(new RecordedResponseCall(agentName, agentVersion, input, maxOutputTokens));

        var script = _scripts.FirstOrDefault(pair => Matches(pair.Key, agentName)).Value
            ?? throw new InvalidOperationException($"No scripted response for agent '{agentName}'.");

        var next = NextStep(script) ?? throw new InvalidOperationException($"Script for agent '{agentName}' is empty.");

        var now = Interlocked.Increment(ref _inFlight);
        UpdatePeak(now);
        try
        {
            return await next(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Plays the scripted response back in pieces of <see cref="StreamPieceLength"/> characters, as Foundry streams.</summary>
    public async Task<ResponseResult> StreamResponseAsync(string agentName, IReadOnlyList<ResponseItem> inputItems, Action<string> onText,
        string? agentVersion = null, int? maxOutputTokens = null, CancellationToken cancellationToken = default)
    {
        var response = await CreateResponseAsync(agentName, inputItems, agentVersion, maxOutputTokens, cancellationToken);
        var text = response.GetOutputText() ?? string.Empty;
        for (var start = 0; start < text.Length; start += StreamPieceLength)
        {
            onText(text.Substring(start, Math.Min(StreamPieceLength, text.Length - start)));
        }

        StreamedCalls++;
        return response;
    }

    /// <summary>How many characters each streamed piece has.</summary>
    public int StreamPieceLength { get; set; } = 3;

    /// <summary>Responses that were streamed.</summary>
    public int StreamedCalls { get; private set; }

    private ConcurrentQueue<Func<CancellationToken, Task<ResponseResult>>> Script(string agentName)
        => _scripts.GetOrAdd(agentName, _ => new ConcurrentQueue<Func<CancellationToken, Task<ResponseResult>>>());

    /// <summary>The last scripted step repeats, so a test only scripts what differs between calls.</summary>
    private static Func<CancellationToken, Task<ResponseResult>>? NextStep(ConcurrentQueue<Func<CancellationToken, Task<ResponseResult>>> script)
    {
        if (script.Count > 1 && script.TryDequeue(out var dequeued))
        {
            return dequeued;
        }

        return script.TryPeek(out var last) ? last : null;
    }

    private void UpdatePeak(int value)
    {
        var current = Volatile.Read(ref _peakInFlight);
        while (current < value && Interlocked.CompareExchange(ref _peakInFlight, value, current) != current)
        {
            current = Volatile.Read(ref _peakInFlight);
        }
    }

    private static bool Matches(string scriptedName, string actualName)
        => actualName == scriptedName
           || (actualName.StartsWith(scriptedName + "-", StringComparison.Ordinal)
               && Guid.TryParseExact(actualName[(scriptedName.Length + 1)..], "N", out _));
}

internal static class FoundryResponses
{
    public static ResponseResult Completed(string id, string text, int totalTokens = 30, int cachedTokens = 0)
        => WithOutputItems(id, [ResponseItem.CreateAssistantMessageItem(text, (IEnumerable<ResponseMessageAnnotation>?)null)], totalTokens,
            cachedTokens: cachedTokens);

    public static ResponseResult FunctionCall(string id, string callId, string functionName)
        => WithOutputItems(id, [ResponseItem.CreateFunctionCallItem(callId: callId, functionName: functionName,
            functionArguments: BinaryData.FromString("{}"))]);

    /// <summary>Usage always reports 10 input tokens and the rest as output tokens.</summary>
    public static ResponseResult WithOutputItems(string id, IEnumerable<ResponseItem> items, int? totalTokens = null, string status = "completed",
        int cachedTokens = 0)
    {
        var itemsJson = string.Join(',', items.Select(item => ModelReaderWriter.Write(item).ToString()));
        var usageJson = totalTokens is null
            ? ""
            : $",\"usage\":{{\"input_tokens\":10,\"input_tokens_details\":{{\"cached_tokens\":{cachedTokens}}},\"output_tokens\":{totalTokens - 10},\"total_tokens\":{totalTokens}}}";
        var json = $"{{\"id\":\"{id}\",\"object\":\"response\",\"created_at\":0,\"status\":\"{status}\",\"model\":\"gpt-4o\",\"output\":[{itemsJson}]{usageJson}}}";
        return ModelReaderWriter.Read<ResponseResult>(BinaryData.FromString(json))!;
    }
}

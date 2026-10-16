using System.ClientModel.Primitives;
using System.Globalization;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// Reads the model deployment's token limit and remaining tokens from every Foundry response, records the remaining tokens,
/// and counts each response where they've dropped below <paramref name="lowPercent"/> of the limit. The limit is shared by
/// every app using the deployment, so the response headers, not this app's own count, are the true figure.
/// </summary>
internal sealed class TokenHeadroomPolicy(string applicationName, double lowPercent) : PipelinePolicy
{
    // Through a gateway, then directly from Azure OpenAI.
    private static readonly string[] RemainingHeaders = ["remaining-tokens", "x-ratelimit-remaining-tokens"];
    private static readonly string[] LimitHeaders = ["deployment-token-limit", "x-ratelimit-limit-tokens"];

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        Record(message.Response);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        Record(message.Response);
    }

    private void Record(PipelineResponse? response)
    {
        if (response is null || ReadFirst(response, RemainingHeaders) is not { } remaining)
        {
            return;
        }

        KeyValuePair<string, object?> application = new(AgentTelemetry.ApplicationTag, applicationName);
        AgentTelemetry.TokensRemaining.Record(remaining, application);
        if (lowPercent > 0 && ReadFirst(response, LimitHeaders) is { } limit and > 0 && remaining < limit * lowPercent / 100)
        {
            AgentTelemetry.TokensLow.Add(1, application);
        }
    }

    private static long? ReadFirst(PipelineResponse response, string[] names)
    {
        foreach (var name in names)
        {
            if (response.Headers.TryGetValue(name, out var value)
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        return null;
    }
}

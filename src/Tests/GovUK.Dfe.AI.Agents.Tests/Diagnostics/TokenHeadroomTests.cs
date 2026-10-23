using System.ClientModel.Primitives;
using System.Diagnostics.Metrics;
using System.Net;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Diagnostics;

/// <summary>
/// The deployment's remaining tokens, read from each Foundry response through the client options the library really uses,
/// and a count of each response below the app's own threshold.
/// </summary>
public sealed class TokenHeadroomTests : IDisposable
{
    private readonly string _application = $"headroom-test-{Guid.NewGuid():N}";
    private readonly MeterListener _listener = new();
    private readonly List<long> _remaining = [];
    private int _low;

    public TokenHeadroomTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgentTelemetry.SourceName && instrument.Name.StartsWith("dfe.ai_agents.tokens.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!tags.ToArray().Any(tag => Equals(tag.Value, _application)))
            {
                return;
            }

            if (instrument.Name == "dfe.ai_agents.tokens.remaining")
            {
                lock (_remaining) { _remaining.Add(value); }
            }
            else
            {
                Interlocked.Add(ref _low, (int)value);
            }
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>Sends one request through a pipeline built from the library's Foundry client options.</summary>
    private async Task SendAsync(double lowPercent, params (string Name, string Value)[] headers)
    {
        var options = AgentsServiceCollectionExtensions.FoundryClientOptions(new AgentsOptions { LowRemainingTokensPercent = lowPercent }, _application);
        options.Transport = new HttpClientPipelineTransport(new HttpClient(new Respond(headers)));
        var pipeline = ClientPipeline.Create(options, [], [], []);
        using var message = pipeline.CreateMessage();
        message.Request.Method = "GET";
        message.Request.Uri = new Uri("https://example.services.ai.azure.com/api/projects/test");

        await pipeline.SendAsync(message);
    }

    [Theory]
    [InlineData("remaining-tokens", "deployment-token-limit")]                 // through a gateway
    [InlineData("x-ratelimit-remaining-tokens", "x-ratelimit-limit-tokens")]   // directly from Azure OpenAI
    public async Task RemainingTokens_AreRecorded_FromEitherHeaderStyle(string remaining, string limit)
    {
        await SendAsync(10, (remaining, "78000"), (limit, "100000"));

        Assert.Equal([78_000], _remaining);
        Assert.Equal(0, _low);
    }

    [Theory]
    [InlineData("9999", 10, 1)]    // below 10% of 100,000: counted
    [InlineData("10000", 10, 0)]   // at 10%: not below
    [InlineData("9999", 5, 0)]     // each app sets its own threshold
    [InlineData("0", 0, 0)]        // 0 turns the count off
    public async Task EachResponseBelowTheAppsThreshold_IsCounted(string remaining, double lowPercent, int counted)
    {
        await SendAsync(lowPercent, ("remaining-tokens", remaining), ("deployment-token-limit", "100000"));

        Assert.Equal(counted, _low);
    }

    [Fact]
    public async Task WithoutTheHeaders_NothingIsRecorded()
    {
        await SendAsync(10);

        Assert.Empty(_remaining);
        Assert.Equal(0, _low);
    }

    private sealed class Respond((string Name, string Value)[] headers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        }
    }
}

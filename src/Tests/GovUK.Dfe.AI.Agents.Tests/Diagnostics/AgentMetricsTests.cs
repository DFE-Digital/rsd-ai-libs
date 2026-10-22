using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Diagnostics;

/// <summary>An app's own metrics, made to the library's standard and kept apart from every other app's.</summary>
public sealed class AgentMetricsTests
{
    // Each test counts on its own counter, so tests running side by side don't see each other's counts.
    private static AgentCounter Counter(string name) => new($"dfe.ai_agents.tests.{name}", "Counted by a test.");

    [Fact]
    public void Tags_every_count_with_the_app_and_agent_and_its_own_tags()
    {
        var counter = Counter("tags");
        using var counts = new CountListener(counter);

        new AgentMetrics("briefing-tool").Count(counter, "ofsted-agent", new KeyValuePair<string, object?>("dfe.ai_agents.release_gate.outcome", "passed"));

        var count = Assert.Single(counts.Recorded);
        Assert.Equal("briefing-tool", count[AgentTelemetry.ApplicationTag]);
        Assert.Equal("ofsted-agent", count[AgentTelemetry.AgentNameTag]);
        Assert.Equal("passed", count["dfe.ai_agents.release_gate.outcome"]);
    }

    [Fact]
    public void Keeps_each_apps_counts_apart_on_the_same_counter()
    {
        var counter = Counter("apps");
        using var counts = new CountListener(counter);

        new AgentMetrics("briefing-tool").Count(counter, "ofsted-agent");
        new AgentMetrics("rsd-common-ai-services").Count(counter, "ofsted-agent");

        Assert.Equal(["briefing-tool", "rsd-common-ai-services"],
            counts.Recorded.Select(count => (string)count[AgentTelemetry.ApplicationTag]!).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(AgentTelemetry.ApplicationTag)]
    [InlineData(AgentTelemetry.AgentNameTag)]
    public void Wont_let_a_tag_replace_the_app_or_agent(string reserved)
    {
        var counter = Counter("reserved");
        using var counts = new CountListener(counter);

        Assert.Throws<ArgumentException>(() =>
            new AgentMetrics("briefing-tool").Count(counter, "ofsted-agent", new KeyValuePair<string, object?>(reserved, "another-app")));
        Assert.Empty(counts.Recorded);
    }

    [Fact]
    public void Counts_a_test_copy_under_its_agents_name_as_the_librarys_metrics_do()
    {
        var counter = Counter("copies");
        using var counts = new CountListener(counter);

        new AgentMetrics("briefing-tool").Count(counter, $"ofsted-agent-{Guid.NewGuid():N}");

        Assert.Equal("ofsted-agent", Assert.Single(counts.Recorded)[AgentTelemetry.AgentNameTag]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Needs_the_apps_name(string applicationName) =>
        Assert.ThrowsAny<ArgumentException>(() => new AgentMetrics(applicationName));

    [Theory]
    [InlineData("dfe.ai_agents.release_gate.guardrail_checks", true)]
    [InlineData("dfe.ai_agents.agents.provisioned", true)]
    [InlineData("dfe.common_ai_services.guardrail.checks", false)]   // outside the library's namespace
    [InlineData("dfe.ai_agents.", false)]
    [InlineData("dfe.ai_agents.Release_Gate", false)]                // not lower case
    [InlineData("dfe.ai_agents.release-gate", false)]                // OpenTelemetry names use dots and underscores
    public void Names_a_counter_only_to_the_librarys_standard(string name, bool valid)
    {
        var define = () => new AgentCounter(name, "Counted by a test.");

        if (valid)
        {
            Assert.Equal(name, define().Name);
        }
        else
        {
            Assert.Throws<ArgumentException>(define);
        }
    }

    [Fact]
    public void Defining_a_name_twice_counts_on_one_instrument_not_a_conflicting_duplicate() =>
        Assert.Same(Counter("twice").Instrument, Counter("twice").Instrument);

    [Fact]
    public void AddAgents_tags_counts_with_AiAgents_ApplicationName()
    {
        var counter = Counter("registered");
        using var counts = new CountListener(counter);
        var settings = new Dictionary<string, string?>
        {
            ["AiAgents:ApplicationName"] = "briefing-tool",
            ["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/briefings",
            ["AiAgents:Foundry:DefaultModel"] = "my-connection/gpt-4o",
            ["AiAgents:Authentication:TenantId"] = "tenant-1",
            ["AiAgents:Authentication:ClientId"] = "client-1",
            ["AiAgents:Authentication:ClientSecret"] = "secret-1",
        };
        var services = new ServiceCollection().AddLogging();
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAgentMetrics>().Count(counter, "ofsted-agent");

        Assert.Equal("briefing-tool", Assert.Single(counts.Recorded)[AgentTelemetry.ApplicationTag]);
    }

    /// <summary>The counts recorded on one counter, each as its tags.</summary>
    private sealed class CountListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<IReadOnlyDictionary<string, object?>> _recorded = new();

        public CountListener(AgentCounter counter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AgentTelemetry.SourceName && instrument.Name == counter.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
                _recorded.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
            _listener.Start();
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Recorded => [.. _recorded];

        public void Dispose() => _listener.Dispose();
    }
}

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
        using var counts = new MetricListener(counter.Name);

        new AgentMetrics("briefing-tool").Count(counter, "ofsted-agent", new KeyValuePair<string, object?>("dfe.ai_agents.release_gate.outcome", "passed"));

        var count = Assert.Single(counts.Recorded).Tags;
        Assert.Equal("briefing-tool", count[AgentTelemetry.ApplicationTag]);
        Assert.Equal("ofsted-agent", count[AgentTelemetry.AgentNameTag]);
        Assert.Equal("passed", count["dfe.ai_agents.release_gate.outcome"]);
    }

    [Fact]
    public void Keeps_each_apps_counts_apart_on_the_same_counter()
    {
        var counter = Counter("apps");
        using var counts = new MetricListener(counter.Name);

        new AgentMetrics("briefing-tool").Count(counter, "ofsted-agent");
        new AgentMetrics("rsd-common-ai-services").Count(counter, "ofsted-agent");

        Assert.Equal(["briefing-tool", "rsd-common-ai-services"],
            counts.Recorded.Select(count => (string)count.Tags[AgentTelemetry.ApplicationTag]!).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(AgentTelemetry.ApplicationTag)]
    [InlineData(AgentTelemetry.AgentNameTag)]
    public void Wont_let_a_tag_replace_the_app_or_agent(string reserved)
    {
        var counter = Counter("reserved");
        using var counts = new MetricListener(counter.Name);

        Assert.Throws<ArgumentException>(() =>
            new AgentMetrics("briefing-tool").Count(counter, "ofsted-agent", new KeyValuePair<string, object?>(reserved, "another-app")));
        Assert.Empty(counts.Recorded);
    }

    [Fact]
    public void Counts_a_test_copy_under_its_agents_name_as_the_librarys_metrics_do()
    {
        var counter = Counter("copies");
        using var counts = new MetricListener(counter.Name);

        new AgentMetrics("briefing-tool").Count(counter, $"ofsted-agent-{Guid.NewGuid():N}");

        Assert.Equal("ofsted-agent", Assert.Single(counts.Recorded).Tags[AgentTelemetry.AgentNameTag]);
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
    public void A_scope_adds_its_tags_until_it_is_disposed_and_scopes_nest()
    {
        var counter = Counter("scopes");
        using var counts = new MetricListener(counter.Name);
        var metrics = new AgentMetrics("briefing-tool");

        using (metrics.BeginScope(new KeyValuePair<string, object?>("dfe.ai_agents.release_gate.run_id", "run-1")))
        {
            using (metrics.BeginScope(new KeyValuePair<string, object?>("dfe.ai_agents.tests.step", "inner")))
            {
                metrics.Count(counter, "inner-agent");
            }

            metrics.Count(counter, "outer-agent");
        }

        metrics.Count(counter, "after-agent");

        var inner = Assert.Single(counts.For("inner-agent"));
        Assert.Equal(("run-1", "inner"), (inner["dfe.ai_agents.release_gate.run_id"], inner["dfe.ai_agents.tests.step"]));
        var outer = Assert.Single(counts.For("outer-agent"));
        Assert.Equal("run-1", outer["dfe.ai_agents.release_gate.run_id"]);
        Assert.False(outer.ContainsKey("dfe.ai_agents.tests.step"));
        Assert.False(Assert.Single(counts.For("after-agent")).ContainsKey("dfe.ai_agents.release_gate.run_id"));
    }

    [Fact]
    public async Task A_scopes_tags_reach_work_started_inside_it_including_parallel_work()
    {
        var counter = Counter("parallel");
        using var counts = new MetricListener(counter.Name);
        var metrics = new AgentMetrics("briefing-tool");

        using (metrics.BeginScope(new KeyValuePair<string, object?>("dfe.ai_agents.release_gate.run_id", "run-2")))
        {
            await Task.WhenAll(Enumerable.Range(1, 4).Select(i => Task.Run(() => metrics.Count(counter, $"parallel-agent-{i}"))));
        }

        Assert.Equal(4, counts.Recorded.Count(count => Equals(count.Tags["dfe.ai_agents.release_gate.run_id"], "run-2")));
    }

    [Theory]
    [InlineData(AgentTelemetry.ApplicationTag)]
    [InlineData(AgentTelemetry.AgentNameTag)]
    public void A_scope_cant_replace_the_app_or_agent(string reserved) =>
        Assert.Throws<ArgumentException>(() =>
            new AgentMetrics("briefing-tool").BeginScope(new KeyValuePair<string, object?>(reserved, "another-app")));

    [Fact]
    public void Records_a_histograms_value_tagged_like_a_count()
    {
        var histogram = new AgentHistogram("dfe.ai_agents.tests.score", "{score}", "Recorded by a test.");
        using var values = new MetricListener(histogram.Name);

        new AgentMetrics("briefing-tool").Record(histogram, "scored-agent", 4.5,
            new KeyValuePair<string, object?>("gen_ai.evaluation.name", "Relevance"));

        var value = Assert.Single(values.Recorded);
        Assert.Equal(4.5, value.Value);
        Assert.Equal(("briefing-tool", "scored-agent", "Relevance"),
            (value.Tags[AgentTelemetry.ApplicationTag], value.Tags[AgentTelemetry.AgentNameTag], value.Tags["gen_ai.evaluation.name"]));
    }

    [Fact]
    public void AddAgents_tags_counts_with_AiAgents_ApplicationName()
    {
        var counter = Counter("registered");
        using var counts = new MetricListener(counter.Name);
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

        Assert.Equal("briefing-tool", Assert.Single(counts.Recorded).Tags[AgentTelemetry.ApplicationTag]);
    }
}

using Azure.Core;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Interfaces;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI;

/// <summary>
/// Setting up the gate: one call adds the judge, release gate and quality gate, and an app's own
/// <c>AddQualityEvaluation</c> or <c>AddReleaseGate</c> calls count whatever their order, so nothing is silently lost.
/// </summary>
public sealed class QualityGateSetupTests
{
    private readonly IAgentRunEvaluator _judge = Substitute.For<IAgentRunEvaluator>();

    [Fact]
    public void Adds_the_judge_release_gate_and_quality_gate_in_one_call()
    {
        var registrations = Setup(agents => agents.AddResponsibleAiQualityGate());
        using var services = registrations.BuildServiceProvider();

        Assert.NotNull(services.GetService<IResponsibleAiQualityGate>());
        Assert.NotNull(services.GetService<IAgentReleaseGate>());
        Assert.Contains(registrations, s => s.ServiceType == typeof(IAgentRunEvaluator));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Applies_release_gate_changes_made_before_or_after_the_quality_gate(bool before)
    {
        var registrations = Setup(agents => (before
            ? agents.AddReleaseGate(s => s.Repeats = 5).AddResponsibleAiQualityGate()
            : agents.AddResponsibleAiQualityGate().AddReleaseGate(s => s.Repeats = 5)).AddReleaseGate(s => s.MaxParallelTestRuns = 2));
        using var services = registrations.BuildServiceProvider();

        var settings = services.GetRequiredService<ReleaseGateSettings>();
        Assert.Equal((5, 2), (settings.Repeats, settings.MaxParallelTestRuns));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Uses_the_apps_own_judge_instead_of_the_default_whether_added_before_or_after(bool before)
    {
        var registrations = Setup(agents => _ = before
            ? agents.AddCustomQualityEvaluation(_ => _judge).AddResponsibleAiQualityGate()
            : agents.AddResponsibleAiQualityGate().AddCustomQualityEvaluation(_ => _judge));
        using var services = registrations.BuildServiceProvider();

        Assert.Same(_judge, services.GetRequiredService<IAgentRunEvaluator>());
    }

    [Fact]
    public void Uses_the_judge_chosen_in_the_gate()
    {
        var registrations = Setup(agents => agents.AddResponsibleAiQualityGate(gate => gate.UseEvaluator(_ => _judge)));
        using var services = registrations.BuildServiceProvider();

        Assert.Same(_judge, services.GetRequiredService<IAgentRunEvaluator>());
    }

    [Fact]
    public void Adds_no_judge_when_the_team_chooses_none()
    {
        var registrations = Setup(agents => agents.AddResponsibleAiQualityGate(gate => gate.WithoutJudge()));
        using var services = registrations.BuildServiceProvider();

        Assert.Null(services.GetService<IAgentRunEvaluator>());
        Assert.NotNull(services.GetService<IResponsibleAiQualityGate>());
    }

    [Fact]
    public void Treats_the_same_judge_chosen_twice_as_one()
    {
        var registrations = Setup(agents => agents.AddQualityEvaluation().AddResponsibleAiQualityGate().AddQualityEvaluation());
        using var services = registrations.BuildServiceProvider();

        Assert.Single(registrations, s => s.ServiceType == typeof(IAgentRunEvaluator));
    }

    [Fact]
    public void Stops_startup_naming_both_when_two_different_judges_are_chosen()
    {
        var error = Record.Exception(() => Setup(agents => agents
            .AddCustomQualityEvaluation(_ => _judge)
            .AddResponsibleAiQualityGate(gate => gate.WithoutJudge())));

        var message = error?.GetBaseException().Message;
        Assert.Equal("Two judges were set up for agent evaluation: AddCustomQualityEvaluation(...) and WithoutJudge(). Choose one.", message);
    }

    [Fact]
    public void Leaves_an_app_that_only_scores_live_answers_as_it_was()
    {
        var registrations = Setup(agents => agents.AddCustomQualityEvaluation(_ => _judge));
        using var services = registrations.BuildServiceProvider();

        Assert.Same(_judge, services.GetRequiredService<IAgentRunEvaluator>());
        Assert.Null(services.GetService<IResponsibleAiQualityGate>());
        Assert.Null(services.GetService<IAgentReleaseGate>());
    }

    private static IServiceCollection Setup(Action<AgentsBuilder> configure)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/briefings",
            ["AiAgents:Foundry:DefaultModel"] = "myconnection/gpt-5.1",
            ["AiAgents:Evaluation:SampleRate"] = "0",
        }).Build();

        var services = new ServiceCollection().AddLogging();
        services.AddAgents(configuration, agents => configure(agents.UseCredential(Substitute.For<TokenCredential>())));
        return services;
    }
}

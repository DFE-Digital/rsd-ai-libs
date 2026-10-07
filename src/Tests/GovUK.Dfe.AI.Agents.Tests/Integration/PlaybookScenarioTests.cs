using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Privacy;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Integration;

/// <summary>
/// The hooks a service needs to meet the UK Government AI Playbook: a person approves tool calls that change things, personal
/// data is removed before it reaches the model, and every answer can be traced.
/// </summary>
public sealed partial class AgentPlatformEndToEndTests
{
    private static readonly AgentDefinition CaseAgent = new("case-agent", "Case")
    {
        AllowedTools = ["get_performance_data", "update_case"],
        ToolsRequiringApproval = ["update_case"],
    };

    private const string Upn = "A12345678901B";

    // ===================== Human approval =====================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AToolNeedingApproval_RunsOnlyOnceApproved_AndADenialIsToldToTheAgent(bool approved)
    {
        WriteSystemPrompt("Case", "You manage cases.");
        _conversations.Reply("case-agent",
            FoundryResponses.FunctionCall("r1", "call-1", "update_case"),
            FoundryResponses.Completed("r2", "Done."));
        var tools = new FakeToolServer("get_performance_data", "update_case") { Output = "Case updated." };

        using var provider = Build(services => services.AddSingleton(new ApprovalDecision(approved)).AddSingleton(new AgentToolBinding("case-agent", tools)),
            agents: agents => agents.AddToolApprover<TestApprover>());
        await provider.GetRequiredService<IAgentService>().RunAsync(CaseAgent, "Close case 42.", cancellationToken: cancellationToken);

        Assert.Equal([("case-agent", "update_case")], ((TestApprover)provider.GetRequiredService<IToolCallApprover>()).Asked);
        Assert.Equal(approved ? 1 : 0, tools.Calls.Count);
        var followUp = _conversations.CallsFor("case-agent")[1].SerializedInput;
        Assert.Equal(!approved, followUp.Contains("was not approved, so it didn't run: a manager must agree first", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AToolNotNeedingApproval_RunsWithoutAsking()
    {
        WriteSystemPrompt("Case", "You manage cases.");
        _conversations.Reply("case-agent",
            FoundryResponses.FunctionCall("r1", "call-1", "get_performance_data"),
            FoundryResponses.Completed("r2", "Done."));
        var tools = new FakeToolServer("get_performance_data", "update_case");

        using var provider = Build(services => services.AddSingleton(new ApprovalDecision(false)).AddSingleton(new AgentToolBinding("case-agent", tools)),
            agents: agents => agents.AddToolApprover<TestApprover>());
        await provider.GetRequiredService<IAgentService>().RunAsync(CaseAgent, "Read case 42.", cancellationToken: cancellationToken);

        Assert.Single(tools.Calls);
        Assert.Empty(((TestApprover)provider.GetRequiredService<IToolCallApprover>()).Asked);
    }

    [Theory]
    [InlineData(false, "agent case-agent's ToolsRequiringApproval (call agents.AddToolApprover<T>() so they can run)")]
    [InlineData(true, "agent case-agent's ToolsRequiringApproval (not in its AllowedTools: delete_case)")]
    public void ApprovalThatCouldNeverHappen_FailsStartup(bool withApprover, string expected)
    {
        WriteSystemPrompt("Case", "You manage cases.");
        var definition = withApprover ? CaseAgent with { ToolsRequiringApproval = ["delete_case"] } : CaseAgent;

        var ex = Assert.Throws<InvalidOperationException>(() => Build(agents: agents =>
        {
            agents.AddAgents(definition);
            if (withApprover)
            {
                agents.AddToolApprover<TestApprover>();
            }
        }));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    private sealed record ApprovalDecision(bool Approved);

    /// <summary>An app's approver, e.g. one that asks a manager; it takes its own services from the app's container.</summary>
    private sealed class TestApprover(ApprovalDecision decision) : IToolCallApprover
    {
        public List<(string Agent, string Tool)> Asked { get; } = [];

        public Task<ToolCallApproval> ApproveAsync(string agentName, ToolCallRequest call, CancellationToken cancellationToken)
        {
            Asked.Add((agentName, call.FunctionName));
            return Task.FromResult(decision.Approved ? ToolCallApproval.Approve() : ToolCallApproval.Deny("a manager must agree first"));
        }
    }

    // ===================== Personal data =====================

    [Fact]
    public async Task PersonalData_IsRemovedFromThePromptEvidenceAndToolOutput_BeforeTheModel_AndFromObservedRuns()
    {
        WriteSystemPrompt("Performance", "You summarise school performance.");
        _conversations.Reply("performance-agent",
            FoundryResponses.FunctionCall("r1", "call-1", "get_performance_data"),
            FoundryResponses.Completed("r2", "Summarised."));
        var tools = PerformanceTools($"Pupil {Upn} met the standard.");
        var observer = Substitute.For<IAgentRunObserver>();
        var redactor = new PatternRedactor(new Dictionary<string, string> { ["UPN"] = @"\b[A-Z]\d{11}[0-9A-Z]\b" });

        using var provider = Build(services => services.AddSingleton(observer).AddSingleton(new AgentToolBinding("performance-agent", tools)),
            agents: agents => agents.AddRedactor(redactor));
        await provider.GetRequiredService<IAgentService>().RunAsync(
            new AgentDefinition("performance-agent", "Performance") { AllowedTools = ["get_performance_data"] },
            $"Summarise pupil {Upn}.", $"Record for {Upn}: attendance 92%.", cancellationToken);

        var sent = string.Concat(_conversations.CallsFor("performance-agent").Select(call => call.SerializedInput));
        Assert.DoesNotContain(Upn, sent, StringComparison.Ordinal);
        Assert.Equal(3, sent.Split("[UPN removed]").Length - 1);   // prompt, evidence and tool output
        observer.Received(1).OnRunCompleted(Arg.Is<CompletedAgentRun>(run =>
            !run.Prompt.Contains(Upn) && !run.Evidence!.Contains(Upn)));
    }

    // ===================== Traceability =====================

    [Fact]
    public async Task EveryAnswer_HasARunIdAndTime_SharedWithObservers_SoItCanBeTracedAndAudited()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _conversations.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Good."));
        CompletedAgentRun? observed = null;
        var observer = Substitute.For<IAgentRunObserver>();
        observer.OnRunCompleted(Arg.Do<CompletedAgentRun>(run => observed = run));
        var before = DateTimeOffset.UtcNow;

        using var provider = Build(services => services.AddSingleton(observer));
        var result = await provider.GetRequiredService<IAgentService>().RunAsync(new AgentDefinition("ofsted-agent", "Ofsted"), "Summarise.",
            cancellationToken: cancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(result.RunId));
        Assert.InRange(result.CompletedAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.Equal((result.RunId, result.CompletedAt), (observed!.RunId, observed.CompletedAt));
    }

    // ===================== Answers the app can show =====================

    [Fact]
    public async Task ACitationWrittenAsALink_IsReturnedAsPlainText_AndStillPassesTheCitationCheck()
    {
        WriteSystemPrompt("Ofsted", "You analyse Ofsted reports.");
        _conversations.Reply("ofsted-agent", FoundryResponses.Completed("r1", "Rated Good [Evidence 1](evidence/1)."));
        using var provider = Build();

        var result = await provider.GetRequiredService<IAgentService>().RunAsync(new AgentDefinition("ofsted-agent", "Ofsted"), "Summarise.",
            "--- ofsted_index Evidence 1 ---" + Environment.NewLine + "Rated Good in 2024.", cancellationToken);

        Assert.Equal("Rated Good [Evidence 1].", result.Output);   // no link to the app's own address, so no 404
        Assert.Single(_conversations.CallsFor("ofsted-agent"));      // the citation counted: no retry
        Assert.Contains("never as a link", _conversations.CallsFor("ofsted-agent")[0].SerializedInput, StringComparison.Ordinal);
    }
}

using Azure.AI.Extensions.OpenAI;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.Clients;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.Extensibility;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds answer scoring to <c>AddAgents</c>, set up under <c>AiAgents:Evaluation</c>.</summary>
public static class QualityEvaluationExtensions
{
    private const string SectionName = "Evaluation";

    /// <summary>
    /// Scores answers with the <c>AiAgents:Evaluation:JudgeModel</c> (default: <c>Foundry:DefaultModel</c>): every
    /// <c>IAgentTestRunner</c> case, and a <c>SampleRate</c> share of live runs in the background.
    /// </summary>
    /// <param name="evaluator">Default: groundedness and relevance, each scored 1–5.</param>
    public static AgentsBuilder AddQualityEvaluation(this AgentsBuilder agents, IEvaluator? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var scoring = evaluator ?? new CompositeEvaluator(new GroundednessEvaluator(), new RelevanceEvaluator());
        return agents.AddPackage(new EvaluationPackage((judgeModel, asAgent) => sp => new ExtensionsAiEvaluator(scoring,
            new ChatConfiguration(asAgent
                ? new AgentJudgeChatClient(sp.GetRequiredService<IAgentRunnerService>(), judgeModel!)
                : new FoundryJudgeChatClient(sp.GetRequiredService<ProjectOpenAIClient>(), judgeModel!)),
            sp.GetService<ILogger<ExtensionsAiEvaluator>>()), needsJudge: true));
    }

    /// <summary>
    /// Scores answers with your own <paramref name="evaluator"/>: every <c>IAgentTestRunner</c> case, and the
    /// <c>AiAgents:Evaluation:SampleRate</c> share of live runs (default 5%) in the background.
    /// </summary>
    public static AgentsBuilder AddCustomQualityEvaluation(this AgentsBuilder agents, Func<IServiceProvider, IAgentRunEvaluator> evaluator)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(evaluator);

        return agents.AddPackage(new EvaluationPackage((_, _) => evaluator, needsJudge: false));
    }

    /// <summary>One package whichever way it's added, so scoring is registered once.</summary>
    /// <param name="evaluatorFor">The evaluator, given the judge model and whether the judge runs as a temporary agent.</param>
    private sealed class EvaluationPackage(Func<string?, bool, Func<IServiceProvider, IAgentRunEvaluator>> evaluatorFor, bool needsJudge)
        : IAgentsPackage
    {
        public string Name => "Evaluation";

        public void Register(AgentsPackageContext context)
        {
            var settings = context.Section.GetSection(SectionName).Get<EvaluationSettings>() ?? new EvaluationSettings();
            var foundry = context.Options.Foundry;
            var problems = settings.Problems(needsJudge, foundry).ToList();
            problems.ForEach(problem => context.ReportProblem($"{SectionName}:{problem}"));
            if (problems.Count > 0)
            {
                return;
            }

            var services = context.Services;
            // With a model connection, the judge runs as an agent, so its calls get the same connection and guardrail.
            services.AddSingleton(evaluatorFor(settings.JudgeModelFor(foundry), foundry.ModelConnection is not null));
            if (settings.SampleRate <= 0)
            {
                return;
            }

            var applicationName = context.ApplicationName;
            services.AddSingleton(sp => new AgentQualityMonitor(sp.GetRequiredService<IAgentRunEvaluator>(), settings.SampleRate,
                applicationName, sp.GetRequiredService<ILogger<AgentQualityMonitor>>()));
            services.AddSingleton<IAgentRunObserver>(sp => sp.GetRequiredService<AgentQualityMonitor>());
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AgentQualityMonitor>());
        }
    }
}

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
using GovUK.Dfe.AI.Agents.Evaluation.Metrics;

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

        IEvaluator[] scoring = evaluator is null ? [new GroundednessEvaluator(), new RelevanceEvaluator()] : [evaluator];
        return agents.AddPackage(new EvaluationPackage((judgeModel, asAgent) => sp => asAgent
            ? JudgedByAgents(scoring, sp.GetRequiredService<IAgentRunnerService>(), judgeModel!, sp)
            : new ExtensionsAiEvaluator(Combined(scoring),
                new ChatConfiguration(new FoundryJudgeChatClient(sp.GetRequiredService<ProjectOpenAIClient>(), judgeModel!)),
                sp.GetService<ILogger<ExtensionsAiEvaluator>>()), needsJudge: true));
    }

    /// <summary>
    /// One evaluator as it is, as a team's own may not list its metric names, which <c>CompositeEvaluator</c> requires.
    /// </summary>
    private static IEvaluator Combined(IEvaluator[] evaluators)
        => evaluators.Length == 1 ? evaluators[0] : new CompositeEvaluator(evaluators);

    /// <summary>
    /// Each evaluator gets its own judge agent, named after what it scores (e.g. "answer-judge-groundedness"), kept and
    /// reused.
    /// </summary>
    private static ExtensionsAiEvaluator JudgedByAgents(IEvaluator[] evaluators, IAgentRunnerService runner, string judgeModel,
        IServiceProvider services)
    {
        var judged = evaluators.Select(evaluator => (IEvaluator)new MetricJudgeEvaluator(evaluator,
            new ChatConfiguration(new AgentJudgeChatClient(runner, judgeModel, evaluator.EvaluationMetricNames))));

        // Each evaluator above uses its own judge, so this one is only a fallback that's never called.
        var fallback = new ChatConfiguration(new AgentJudgeChatClient(runner, judgeModel, []));
        return new ExtensionsAiEvaluator(Combined([.. judged]), fallback, services.GetService<ILogger<ExtensionsAiEvaluator>>());
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

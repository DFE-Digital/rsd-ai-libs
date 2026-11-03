using Azure.AI.Extensions.OpenAI;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;
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

    /// <summary>Stands for groundedness and relevance, so choosing them twice isn't two judges.</summary>
    private static readonly object BuiltInJudge = new();

    /// <summary>Groundedness and relevance: the judge the quality gate adds unless the app chooses another.</summary>
    private static readonly JudgeChoice DefaultJudge = Judged(evaluator: null);

    /// <summary>
    /// Scores answers with the <c>AiAgents:Evaluation:JudgeModel</c> (default: <c>Foundry:DefaultModel</c>): every
    /// <c>IAgentTestRunner</c> case, and a <c>SampleRate</c> share of live runs in the background.
    /// </summary>
    /// <param name="evaluator">Default: groundedness and relevance, each scored 1–5.</param>
    /// <remarks>Call it before or after <c>AddResponsibleAiQualityGate()</c>: your choice of judge is kept either way.</remarks>
    public static AgentsBuilder AddQualityEvaluation(this AgentsBuilder agents, IEvaluator? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(agents);

        EvaluationSetup.For(agents.Services).Choose(evaluator is null ? DefaultJudge : Judged(evaluator));
        return agents.AddPackage(new EvaluationPackage());
    }

    /// <summary>Groundedness and relevance unless the app chooses its own judge, in any call, in any order.</summary>
    internal static AgentsBuilder AddDefaultQualityEvaluation(this AgentsBuilder agents)
    {
        EvaluationSetup.For(agents.Services).DefaultJudgeRequested = true;
        return agents.AddPackage(new EvaluationPackage());
    }

    /// <summary>No judge, as the app chose; any other judge choice then contradicts it.</summary>
    internal static AgentsBuilder AddNoQualityEvaluation(this AgentsBuilder agents)
    {
        EvaluationSetup.For(agents.Services).Choose(JudgeChoice.None);
        return agents;
    }

    /// <summary>A judge model scoring with <paramref name="evaluator"/>, or with groundedness and relevance when it's null.</summary>
    private static JudgeChoice Judged(IEvaluator? evaluator)
    {
        IEvaluator[] scoring = evaluator is null ? [new GroundednessEvaluator(), new RelevanceEvaluator()] : [evaluator];
        return new JudgeChoice(
            evaluator is null ? "AddQualityEvaluation()" : $"AddQualityEvaluation({evaluator.GetType().Name})",
            (object?)evaluator ?? BuiltInJudge,
            (judgeModel, asAgent) => sp => asAgent
                ? JudgedByAgents(scoring, sp.GetRequiredService<IAgentRunnerService>(), judgeModel!, sp)
                : new ExtensionsAiEvaluator(Combined(scoring),
                    new ChatConfiguration(new FoundryJudgeChatClient(sp.GetRequiredService<ProjectOpenAIClient>(), judgeModel!,
                        sp.GetService<IModelCallTelemetry>())),
                    sp.GetService<ILogger<ExtensionsAiEvaluator>>()),
            NeedsJudgeModel: true);
    }

    /// <summary>
    /// A <see cref="CompositeEvaluator"/>, even for one: it reports a failed judge call as an unscored metric with the reason,
    /// where a lone evaluator would throw it out of the evaluation. It needs each evaluator's metric names, so a lone
    /// evaluator of the app's own that lists none is used as it is.
    /// </summary>
    private static IEvaluator Combined(IEvaluator[] evaluators)
        => evaluators is [{ EvaluationMetricNames.Count: 0 } only] ? only : new CompositeEvaluator(evaluators);

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

        EvaluationSetup.For(agents.Services).Choose(
            new JudgeChoice("AddCustomQualityEvaluation(...)", evaluator, (_, _) => evaluator, NeedsJudgeModel: false));
        return agents.AddPackage(new EvaluationPackage());
    }

    /// <summary>
    /// One package however scoring is added, so it's registered once, with the judge every call chose between them
    /// (<see cref="EvaluationSetup"/>): a later call's choice is never lost.
    /// </summary>
    private sealed class EvaluationPackage : IAgentsPackage
    {
        public string Name => "Evaluation";

        public void Register(AgentsPackageContext context)
        {
            if (EvaluationSetup.For(context.Services).Judge(DefaultJudge) is not { EvaluatorFor: { } evaluatorFor } judge)
            {
                return;
            }

            var settings = context.Section.GetSection(SectionName).Get<EvaluationSettings>() ?? new EvaluationSettings();
            var foundry = context.Options.Foundry;
            var problems = settings.Problems(judge.NeedsJudgeModel, foundry).ToList();
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

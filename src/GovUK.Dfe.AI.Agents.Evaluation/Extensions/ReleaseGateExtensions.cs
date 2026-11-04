using Azure.AI.Extensions.OpenAI;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.Publishers;
using GovUK.Dfe.AI.Agents.Evaluation.Publishers.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Extensibility;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Evaluation.Metrics;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds the release gate to <c>AddAgents</c>, set up under <c>AiAgents:Evaluation:ReleaseGate</c>.</summary>
public static class ReleaseGateExtensions
{
    private const string SectionName = "Evaluation:ReleaseGate";
    private const string EvaluationSectionName = "Evaluation";

    /// <summary>
    /// Adds <see cref="IAgentReleaseGate"/>: each agent's test cases run on a temporary copy, a report is written per agent,
    /// and the gate is applied. With <c>AddQualityEvaluation()</c> (or your own evaluator), answers are also scored and held
    /// to the minimum score, their baseline and the group gap.
    /// </summary>
    /// <param name="configure">
    /// Changes the settings after they're read. Every call's changes apply, in the order called, before or after
    /// <c>AddResponsibleAiQualityGate()</c>; prefer settings where you can.
    /// </param>
    public static AgentsBuilder AddReleaseGate(this AgentsBuilder agents, Action<ReleaseGateSettings>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(agents);

        if (configure is not null)
        {
            EvaluationSetup.For(agents.Services).ReleaseGateChanges.Add(configure);
        }

        return agents.AddPackage(new ReleaseGatePackage());
    }

    /// <summary>One package however often it's added, applying every call's changes (<see cref="EvaluationSetup"/>).</summary>
    private sealed class ReleaseGatePackage : IAgentsPackage
    {
        public string Name => "ReleaseGate";

        public void Register(AgentsPackageContext context)
        {
            var settings = context.Section.GetSection(SectionName).Get<ReleaseGateSettings>() ?? new ReleaseGateSettings();
            EvaluationSetup.For(context.Services).ReleaseGateChanges.ForEach(change => change(settings));
            settings.AllowForRunTimeout(context.Options.RunTimeout is { } seconds ? TimeSpan.FromSeconds(seconds) : null);

            var foundry = context.Options.Foundry;
            var evaluation = context.Section.GetSection(EvaluationSectionName).Get<EvaluationSettings>() ?? new EvaluationSettings();
            var judgeModel = evaluation.JudgeModelFor(foundry);

            var problems = settings.Problems().ToList();
            if (settings.PublishToFoundry && !foundry.AllowsModel(judgeModel))
            {
                problems.Add($"PublishToFoundry (the judge model must be on the connection in Foundry:DefaultModel, as {foundry.ModelConnection}/<model>)");
            }

            problems.ForEach(problem => context.ReportProblem($"{SectionName}:{problem}"));
            if (problems.Count > 0)
            {
                return;
            }

            var services = context.Services;
            services.AddSingleton(settings);
            // Scores only when an evaluator is registered: AddQualityEvaluation() or your own.
            services.AddSingleton(sp => new ReleaseGateJudge(sp.GetService<IAgentRunEvaluator>() is null ? null : judgeModel));
            services.AddSingleton<ReleaseGateMetrics>();
            services.TryAddSingleton(GuardrailBlocklists.From(context.Section));
            services.AddSingleton<SensitiveDataMasker>();
            services.AddSingleton<ReleaseReportWriter>();
            services.AddSingleton<AgentReleaseEvaluator>();
            services.AddSingleton<AgentReleaseGate>();
            services.AddSingleton<IAgentReleaseGate>(sp => sp.GetRequiredService<AgentReleaseGate>());

            if (settings.PublishToFoundry)
            {
                // Core's Foundry project client, so evaluations use the same project and credential as the agents.
                services.AddSingleton(sp => sp.GetRequiredService<ProjectOpenAIClient>().GetEvaluationClient());
                services.AddSingleton<IFoundryEvaluationPublisher>(sp => new FoundryEvaluationPublisher(
                    sp.GetRequiredService<OpenAI.Evals.EvaluationClient>(), judgeModel!, settings,
                    sp.GetRequiredService<ILogger<FoundryEvaluationPublisher>>()));
            }
        }
    }
}

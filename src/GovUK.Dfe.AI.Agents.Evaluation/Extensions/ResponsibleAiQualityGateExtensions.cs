using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Accountability;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Fairness;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Inclusiveness;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.PrivacyAndSecurity;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.ReliabilityAndSafety;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Transparency;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Reporting;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Extensibility;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds the Responsible AI quality gate to <c>AddAgents</c>, set up under <c>AiAgents:Evaluation:QualityGate</c>.</summary>
public static class ResponsibleAiQualityGateExtensions
{
    private const string SectionName = "Evaluation:QualityGate";

    /// <summary>The library's checks, at least one for each principle.</summary>
    private static readonly Type[] BuiltInChecks =
    [
        typeof(ScenarioOutcomeCheck),
        typeof(GroupOutcomeParityCheck), typeof(GroupScoreParityCheck), typeof(GroupSampleSizeCheck),
        typeof(JudgeScoreCheck), typeof(JudgeConfidenceCheck), typeof(BaselineRegressionCheck),
        typeof(GuardrailInPlaceCheck), typeof(SensitiveDataLeakageCheck), typeof(ToolAuthorisationCheck),
        typeof(ReadabilityCheck), typeof(AccessibleFormattingCheck), typeof(UserGroupCoverageCheck),
        typeof(CitationAccuracyCheck),
        typeof(EvaluationDatasetCheck), typeof(TestCaseConventionsCheck), typeof(TraceabilityCheck), typeof(AttestationCheck),
    ];

    /// <summary>
    /// Everything a release needs, in one call: the judge (<c>AddQualityEvaluation()</c>), the release gate that runs the
    /// test cases (<c>AddReleaseGate()</c>), and <see cref="IResponsibleAiQualityGate"/>, which decides each release
    /// against the six Responsible AI principles. Set it up in <c>AiAgents:Evaluation</c> settings.
    /// </summary>
    /// <remarks>
    /// An app that also calls <c>AddQualityEvaluation</c> or <c>AddReleaseGate</c> itself, before or after this, keeps
    /// what those calls set: its own judge replaces the default, and every release gate change applies. Two different
    /// judges stop startup, with both named.
    /// </remarks>
    /// <param name="configure">Only what settings can't hold: your own checks, and a different judge or none.</param>
    public static AgentsBuilder AddResponsibleAiQualityGate(this AgentsBuilder agents, Action<QualityGateBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(agents);

        var gate = new QualityGateBuilder(agents.Services);
        configure?.Invoke(gate);

        // In this order, so each package finds what it builds on when it registers.
        gate.AddJudge(agents);
        return agents.AddReleaseGate().AddPackage(new QualityGatePackage());
    }

    private sealed class QualityGatePackage : IAgentsPackage
    {
        public string Name => "QualityGate";

        public void Register(AgentsPackageContext context)
        {
            // The release gate registers its settings only when they're valid; otherwise it has reported why already.
            var services = context.Services;
            if (!services.Any(static service => service.ServiceType == typeof(ReleaseGateSettings)))
            {
                return;
            }

            var policy = context.Section.GetSection(SectionName).Get<QualityGatePolicy>() ?? new QualityGatePolicy();
            foreach (var check in BuiltInChecks)
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IQualityCheck), check));
            }

            var problems = policy.Problems().ToList();
            problems.ForEach(problem => context.ReportProblem($"{SectionName}:{problem}"));
            if (problems.Count > 0)
            {
                return;
            }

            var prompts = context.Section.GetSection("PromptFiles");
            var sources = new ManifestSources(context.ApplicationName, context.Options.Foundry.DefaultModel,
                prompts.GetSection("SystemPrompts").Get<Dictionary<string, string>>() ?? [],
                prompts.GetSection("UserPrompts").Get<Dictionary<string, string>>() ?? []);

            services.AddSingleton(policy);
            services.TryAddSingleton(GuardrailBlocklists.From(context.Section));
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton(sp => new ReleaseManifestBuilder(sources, policy, sp.GetRequiredService<ReleaseGateSettings>(),
                sp.GetRequiredService<ReleaseGateJudge>(), sp.GetRequiredService<TimeProvider>(), Environment.GetEnvironmentVariable));
            services.AddSingleton(_ => new QualityGateReportWriter(Environment.GetEnvironmentVariable));
            services.AddSingleton<QualityCheckRunner>();
            services.AddSingleton<QualityGateRecorder>();
            services.AddSingleton<IResponsibleAiQualityGate, ResponsibleAiQualityGate>();
        }
    }
}

using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Interfaces;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI;

/// <summary>
/// What only code can say about the quality gate, in <c>AddResponsibleAiQualityGate</c>: the app's own checks, and which
/// judge scores answers. Everything else, thresholds included, is in <c>AiAgents:Evaluation</c> settings.
/// </summary>
public sealed class QualityGateBuilder
{
    private readonly IServiceCollection _services;

    internal QualityGateBuilder(IServiceCollection services) => _services = services;

    /// <summary>
    /// Adds the judge: groundedness and relevance, unless the app chooses another here or with its own
    /// <c>AddQualityEvaluation</c> call, before or after this one.
    /// </summary>
    internal Action<AgentsBuilder> AddJudge { get; private set; } = static agents => agents.AddDefaultQualityEvaluation();

    /// <summary>Adds a check, created by the container, so it can take the app's services. Adding the same type twice is harmless.</summary>
    public QualityGateBuilder AddCheck<TCheck>() where TCheck : class, IQualityCheck
    {
        _services.TryAddEnumerable(ServiceDescriptor.Singleton<IQualityCheck, TCheck>());
        return this;
    }

    /// <summary>Adds a check instance.</summary>
    public QualityGateBuilder AddCheck(IQualityCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _services.AddSingleton(check);
        return this;
    }

    /// <summary>
    /// Scores answers with a Microsoft.Extensions.AI evaluator instead of groundedness and relevance. List its metrics in
    /// <c>ReleaseGate:Metrics</c>, so each is held to its minimum.
    /// </summary>
    public QualityGateBuilder UseEvaluator(IEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        AddJudge = agents => agents.AddQualityEvaluation(evaluator);
        return this;
    }

    /// <summary>Scores answers with your own scorer instead of the judge model. List its metrics in <c>ReleaseGate:Metrics</c>.</summary>
    public QualityGateBuilder UseEvaluator(Func<IServiceProvider, IAgentRunEvaluator> evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        AddJudge = agents => agents.AddCustomQualityEvaluation(evaluator);
        return this;
    }

    /// <summary>
    /// No judge: answers are checked by their test cases only, and groundedness and relevance aren't scored. For agents
    /// whose answers a judge can't read, e.g. structured output. Saves one model call per test case.
    /// </summary>
    public QualityGateBuilder WithoutJudge()
    {
        AddJudge = static agents => agents.AddNoQualityEvaluation();
        return this;
    }
}

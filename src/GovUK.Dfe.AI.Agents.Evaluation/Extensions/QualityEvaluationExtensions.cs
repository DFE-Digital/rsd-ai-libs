using Azure.AI.Extensions.OpenAI;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Logging;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds a Foundry judge model to <c>AddAgents</c>.</summary>
public static class QualityEvaluationExtensions
{
    /// <summary>
    /// Scores answers with <paramref name="judgeModel"/>, a model in this app's Foundry project (e.g. "myconnection/gpt-5.1"):
    /// every <c>IAgentTestRunner</c> case, and a <paramref name="sampleRate"/> share of live runs in the background.
    /// </summary>
    /// <param name="evaluator">Default: groundedness and relevance, each scored 1–5.</param>
    public static AgentsBuilder AddQualityEvaluation(this AgentsBuilder agents, string judgeModel, double sampleRate = 0.05,
        IEvaluator? evaluator = null)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentException.ThrowIfNullOrWhiteSpace(judgeModel);

        var scoring = evaluator ?? new CompositeEvaluator(new GroundednessEvaluator(), new RelevanceEvaluator());

        return agents.AddQualityEvaluation(sp => new ExtensionsAiEvaluator(scoring,
            new ChatConfiguration(new FoundryJudgeChatClient(sp.GetRequiredService<ProjectOpenAIClient>(), judgeModel)),
            sp.GetService<ILogger<ExtensionsAiEvaluator>>()), sampleRate);
    }
}

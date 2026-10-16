using System.ClientModel;
using System.Globalization;
using System.Text.Json;
using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.Publishers.Interfaces;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.Quality;
using Microsoft.Extensions.Logging;
using OpenAI.Evals;

namespace GovUK.Dfe.AI.Agents.Evaluation.Publishers;

/// <summary>
/// Runs Foundry's built-in groundedness and relevance evaluators over each release's test answers, through the Foundry
/// project's evaluation API. Each agent has one evaluation, with one run per release, so the Foundry portal's
/// Evaluations page compares releases.
/// </summary>
/// <param name="judgeModel">The model Foundry's evaluators use: the judge model, or <c>Foundry:DefaultModel</c>.</param>
internal sealed class FoundryEvaluationPublisher(
    EvaluationClient evaluations,
    string judgeModel,
    ReleaseGateSettings settings,
    ILogger<FoundryEvaluationPublisher> logger) : IFoundryEvaluationPublisher
{
    private const int EvaluationsToSearch = 100;
    private static readonly string[] requiredProperties = ["query", "context", "response"];

    public async Task PublishAsync(string agentName, IReadOnlyList<(AgentTestCase Case, AgentTestResult Result)> cases,
        CancellationToken cancellationToken = default)
    {
        // Refusals are checked by their fixed sentence instead: relevance marks any refusal as irrelevant.
        var items = cases
            .Where(c => c.Result.Output is not null && !c.Result.BlockedByGuardrail && !settings.ExpectsRefusal(c.Case))
            .Select(c => new { item = new { query = c.Case.Prompt, context = c.Case.Evidence ?? string.Empty, response = c.Result.Output } })
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            var evaluationId = await FindOrCreateEvaluationAsync(EvaluationName(agentName), cancellationToken);
            var run = await PostAsync(content => evaluations.CreateEvaluationRunAsync(evaluationId, content,
                new() { CancellationToken = cancellationToken }), new
                {
                    name = $"{agentName} {DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC",
                    data_source = new { type = "jsonl", source = new { type = "file_content", content = items } },
                });

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.FoundryEvaluationStarted(agentName, items.Count, Property(run, "id"),
                    Property(run, "report_url") ?? "the Foundry portal's Evaluations page");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.FoundryEvaluationFailed(ex, agentName);
        }
    }

    /// <summary>One evaluation per agent and judge model, as its evaluators are fixed when it's created.</summary>
    private string EvaluationName(string agentName) =>
        $"{agentName} release gate ({judgeModel})";

    private async Task<string> FindOrCreateEvaluationAsync(string name, CancellationToken cancellationToken)
    {
        var existing = await evaluations.GetEvaluationsAsync(EvaluationsToSearch, "created_at", "desc", after: null,
            new() { CancellationToken = cancellationToken });
        using (var page = Parse(existing))
        {
            foreach (var evaluation in page.RootElement.GetProperty("data").EnumerateArray())
            {
                if (evaluation.TryGetProperty("name", out var found) && found.GetString() == name)
                {
                    return evaluation.GetProperty("id").GetString()!;
                }
            }
        }

        var created = await PostAsync(content => evaluations.CreateEvaluationAsync(content,
            new() { CancellationToken = cancellationToken }), new
            {
                name,
                data_source_config = new
                {
                    type = "custom",
                    item_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string" },
                            context = new { type = "string" },
                            response = new { type = "string" },
                        },
                        required = requiredProperties,
                    },
                },
                testing_criteria = new object[]
                {
                    Evaluator("groundedness", "builtin.groundedness",
                        new { query = "{{item.query}}", context = "{{item.context}}", response = "{{item.response}}" }),
                    Evaluator("relevance", "builtin.relevance",
                        new { query = "{{item.query}}", response = "{{item.response}}" }),
                },
            });

        return Property(created, "id")!;
    }

    private object Evaluator(string name, string evaluatorName, object dataMapping) => new
    {
        type = "azure_ai_evaluator",
        name,
        evaluator_name = evaluatorName,
        initialization_parameters = new { model = judgeModel },
        data_mapping = dataMapping,
    };

    private static async Task<ClientResult> PostAsync(Func<BinaryContent, Task<ClientResult>> send, object body)
    {
        using var content = BinaryContent.Create(BinaryData.FromObjectAsJson(body));
        return await send(content);
    }

    private static JsonDocument Parse(ClientResult result) => JsonDocument.Parse(result.GetRawResponse().Content);

    private static string? Property(ClientResult result, string name)
    {
        using var json = Parse(result);
        return json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}




using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenAI;
using OpenAI.Evals;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary>
/// A fake Foundry evaluation API behind the real SDK client, so tests see the exact requests and no request leaves the
/// machine. It holds the evaluations created, and answers with an error status when told to.
/// </summary>
internal sealed class FakeFoundryEvaluations : HttpMessageHandler
{
    private readonly List<JsonObject> _evaluations = [];

    public List<(string Path, JsonObject Body)> Posts { get; } = [];

    public HttpStatusCode? FailWith { get; set; }

    public EvaluationClient Client() => new(new ApiKeyCredential("key"), new OpenAIClientOptions
    {
        Endpoint = new Uri("https://example.services.ai.azure.com/api/projects/briefings/openai/v1"),
        Transport = new HttpClientPipelineTransport(new HttpClient(this)),
        RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
    });

    public void AddExistingEvaluation(string id, string name) =>
        _evaluations.Add(new JsonObject { ["id"] = id, ["name"] = name });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (FailWith is { } status)
        {
            return Json(status, new JsonObject { ["error"] = new JsonObject { ["message"] = "Failed." } });
        }

        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get)
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["data"] = new JsonArray([.. _evaluations.Select(e => e.DeepClone())]) });
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        lock (Posts)
        {
            Posts.Add((path, body));
        }

        if (path.EndsWith("/runs", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.Created, new JsonObject { ["id"] = "run-1", ["report_url"] = "https://ai.azure.com/report/run-1" });
        }

        var evaluation = new JsonObject { ["id"] = $"eval-{_evaluations.Count + 1}", ["name"] = (string?)body["name"] };
        lock (_evaluations)
        {
            _evaluations.Add(evaluation);
        }

        return Json(HttpStatusCode.Created, evaluation.DeepClone());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}

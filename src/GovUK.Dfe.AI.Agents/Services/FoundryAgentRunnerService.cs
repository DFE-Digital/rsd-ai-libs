using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using GovUK.Dfe.AI.Agents.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Concurrency.Interfaces;
using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Exceptions;
using GovUK.Dfe.AI.Agents.Extensions;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Safety.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;

namespace GovUK.Dfe.AI.Agents.Services;

/// <summary>
/// Runs one Foundry agent: sends the prompt (with evidence fenced), runs the tool calls the model asks for, retries one
/// invalid answer, and enforces the run's timeout, token budget and run slot. Records tokens.
/// </summary>
/// <remarks>
/// Runs are stateless, so nothing is stored in Foundry. The runner keeps the run's history (input, the model's output
/// items, including encrypted reasoning, and tool outputs) and sends it with each call.
/// </remarks>
internal sealed class FoundryAgentRunnerService(IAgentFactory agentFactory, IFoundryResponsesClient responsesClient,
    ILogger<FoundryAgentRunnerService>? logger = null, AgentRunOptions? runOptions = null, IAgentRunLimiter? runLimiter = null,
    IEvidenceShield? evidenceShield = null)
    : IAgentRunnerService
{
    /// <summary>The most tool-call rounds in one run.</summary>
    private const int MaxToolCallRounds = 10;

    private readonly ILogger<FoundryAgentRunnerService> _logger = logger ?? NullLogger<FoundryAgentRunnerService>.Instance;
    private readonly AgentRunOptions _runOptions = runOptions ?? new AgentRunOptions();

    public async Task<AgentResult> RunFromSpecAsync(AgentSpec spec, string prompt, string? additionalContext = null, ToolCallResolver? resolveToolCalls = null, Func<AgentResult, string?>? validateOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        cancellationToken.ThrowIfCancellationRequested();

        var agent = await agentFactory.GetOrCreateAsync(spec, cancellationToken).ConfigureAwait(false);

        return await RunCoreAsync(agent, prompt, additionalContext, resolveToolCalls, validateOutput, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentResult> RunAsync(AgentReference agent, string prompt, string? additionalContext = null,
        ToolCallResolver? resolveToolCalls = null,
        Func<AgentResult, string?>? validateOutput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        cancellationToken.ThrowIfCancellationRequested();

        return await RunCoreAsync(agent, prompt, additionalContext, resolveToolCalls, validateOutput, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>One run, start to finish. Every failure is rethrown wrapped, carrying the tokens used so far.</summary>
    private async Task<AgentResult> RunCoreAsync(AgentReference agent, string prompt, string? additionalContext, ToolCallResolver? resolveToolCalls, Func<AgentResult, string?>? validateOutput,
        CancellationToken cancellationToken)
    {
        // Held for the whole run. Waiting for it doesn't count towards RunTimeout.
        await using var slot = runLimiter is null ? null : await runLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var telemetry = new AgentRunTelemetry(_runOptions.ApplicationName, agent);
        using var timeoutSource = _runOptions.RunTimeout is { } timeout ? CreateTimeoutSource(timeout, cancellationToken) : null;
        var runToken = timeoutSource?.Token ?? cancellationToken;

        var usage = new UsageTally();
        var runId = Guid.CreateVersion7().ToString("N");   // time-ordered, so audit records sort by when they ran
        telemetry.Identified(runId);

        try
        {
            var history = new List<ResponseItem>(
                await BuildInitialInputItemsAsync(agent.Name, prompt, additionalContext, runToken).ConfigureAwait(false));
            var response = await RunResponsesAsync(agent, history, resolveToolCalls, usage, runToken).ConfigureAwait(false);
            telemetry.AnsweredBy(response.Model);

            if (validateOutput is not null)
            {
                response = await CheckAnswerAsync(new RunInProgress(agent, history, resolveToolCalls, usage, runId), response,
                    validateOutput, runToken).ConfigureAwait(false);
            }

            return ToResult(agent, response, usage, runId);
        }
        catch (OperationCanceledException ex) when (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            telemetry.Failed(typeof(TimeoutException).FullName!, "The agent run timed out.");
            _logger.LogError(ex, "Agent {AgentName} did not finish within {RunTimeout}", agent.Name, _runOptions.RunTimeout);
            throw WithUsage(new TimeoutException(string.Format(ErrorMessages.AgentRunTimedOut, agent.Name, _runOptions.RunTimeout), ex), usage);
        }
        catch (OperationCanceledException)
        {
            telemetry.Cancelled();
            throw;
        }
        catch (AgentGuardrailException ex)
        {
            telemetry.Failed("content_filter", ex.Message);   // Foundry's own error code
            AgentTelemetry.RecordGuardrailBlock(_runOptions.ApplicationName, agent.Name, ex.Stage);
            LogBlock(AgentTelemetry.AgentNameForTelemetry(agent.Name), ex.Stage);
            throw WithUsage(new InvalidOperationException(string.Format(ErrorMessages.AgentRunFailed, agent.Name), ex), usage);
        }
        catch (Exception ex)
        {
            telemetry.Failed(ex.GetType().FullName ?? AgentTelemetry.OtherError, "The agent run failed.");
            _logger.LogError(ex, "Agent {AgentName} failed", agent.Name);
            var message = ex is ClientResultException { Status: 429 } ? ErrorMessages.AgentRateLimited : ErrorMessages.AgentRunFailed;
            throw WithUsage(new InvalidOperationException(string.Format(message, agent.Name), ex), usage);
        }
        finally
        {
            telemetry.AnsweredBy(usage.Model);
            telemetry.Record(usage.Total, usage.InferenceCalls, usage.ToolCalls, _runOptions.Pricing.CostOf(usage.Model, usage.Total),
                _runOptions.Pricing.Currency);
        }
    }

    /// <summary>
    /// One line, without a stack trace: a block is an expected outcome, e.g. a release-gate case with planted
    /// instructions, not a fault in the app. Says what blocked it, as the evidence is scanned by Prompt Shields, not Foundry.
    /// </summary>
    private void LogBlock(string agentName, string stage)
    {
        if (stage == AgentGuardrailException.EvidenceStage)
        {
            _logger.LogWarning("Prompt Shields found instructions planted in {AgentName}'s evidence, so it wasn't sent", agentName);
        }
        else
        {
            _logger.LogWarning("A Foundry guardrail blocked {AgentName}'s {Stage}", agentName, stage);
        }
    }

    /// <summary>
    /// Checks the answer, and if it fails, asks once more with the run so far and the reason. A second failure fails the
    /// run, as does any failure of a streamed answer, which has already been shown.
    /// </summary>
    /// <returns>The answer that passed.</returns>
    private async Task<ResponseResult> CheckAnswerAsync(RunInProgress run, ResponseResult response,
        Func<AgentResult, string?> validateOutput, CancellationToken cancellationToken)
    {
        var (agent, history, resolveToolCalls, usage, runId) = run;
        if (validateOutput(ToResult(agent, response, usage, runId)) is not { } problem)
        {
            return response;
        }

        if (AnswerStream.Current is not null)
        {
            throw new InvalidOperationException(string.Format(ErrorMessages.StreamedAnswerInvalid, agent.Name, problem));
        }

        _logger.LogWarning("Agent {AgentName} gave an invalid answer; asking once more: {Problem}", agent.Name, problem);
        history.Add(ResponseItem.CreateUserMessageItem(string.Format(PromptText.AnswerRejected, problem)));
        var retried = await RunResponsesAsync(agent, history, resolveToolCalls, usage, cancellationToken).ConfigureAwait(false);

        return validateOutput(ToResult(agent, retried, usage, runId)) is { } stillInvalid
            ? throw new InvalidOperationException(string.Format(ErrorMessages.AgentAnswerInvalid, agent.Name, stillInvalid))
            : retried;
    }

    /// <summary>The run an answer belongs to: what's needed to check it and, if it's invalid, ask once more.</summary>
    private sealed record RunInProgress(AgentReference Agent, List<ResponseItem> History, ToolCallResolver? ResolveToolCalls,
        UsageTally Usage, string RunId);

    private AgentResult ToResult(AgentReference agent, ResponseResult response, UsageTally usage, string runId)
        => new()
        {
            Cost = _runOptions.Pricing.CostOf(usage.Model, usage.Total),
            AgentName = agent.Name,
            RunId = runId,
            CompletedAt = DateTimeOffset.UtcNow,
            Output = Quality.AnswerLinks.Clean(response.GetOutputText()),
            TotalTokens = usage.Total.TotalTokens,
            InputTokens = usage.Total.InputTokens,
            OutputTokens = usage.Total.OutputTokens,
            CachedInputTokens = usage.Total.CachedInputTokens,
            AgentVersion = agent.Version,
            Model = response.Model,
        };

    /// <summary>
    /// Sends the history, then runs the tool calls the model asks for until it answers, for at most
    /// <see cref="MaxToolCallRounds"/> rounds. Each response's output items and tool outputs are added to the history.
    /// </summary>
    private async Task<ResponseResult> RunResponsesAsync(AgentReference agent, List<ResponseItem> history,
        ToolCallResolver? resolveToolCalls, UsageTally usage, CancellationToken cancellationToken)
    {
        for (var round = 1; round <= MaxToolCallRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await SendAsync(agent, history, usage, cancellationToken).ConfigureAwait(false);
            history.AddRange(response.OutputItems);
            var toolCalls = response.OutputItems.OfType<FunctionCallResponseItem>().ToList();
            if (toolCalls.Count == 0)
            {
                ThrowIfNotCompleted(agent.Name, response);
                return response;
            }

            usage.ToolCalls += toolCalls.Count;

            history.AddRange(await ResolveToolCallsAsync(response, toolCalls, resolveToolCalls, cancellationToken).ConfigureAwait(false));
        }

        throw new InvalidOperationException(string.Format(ErrorMessages.ToolCallRoundLimitExceeded, agent.Name, MaxToolCallRounds));
    }

    /// <summary>
    /// Sends one request, capped at the run's remaining output tokens, and adds its usage. Throws when the budget is used up, the
    /// model stops at the cap, or a guardrail blocks the prompt or answer.
    /// </summary>
    private async Task<ResponseResult> SendAsync(AgentReference agent, IReadOnlyList<ResponseItem> inputItems, UsageTally usage,
        CancellationToken cancellationToken)
    {
        var outputTokensLeft = (int)Math.Max(0, _runOptions.MaxOutputTokensPerRun - usage.Total.OutputTokens);
        if (outputTokensLeft < AgentRunOptions.MinOutputTokens)
        {
            throw OutputTokenLimitReached(agent.Name);
        }

        ResponseResult response;
        usage.InferenceCalls++;   // counted when sent, so failed calls count too
        var started = Stopwatch.GetTimestamp();
        try
        {
            response = AnswerStream.Current is { } onText
                ? await responsesClient.StreamResponseAsync(agent.Name, inputItems, onText, agent.Version, outputTokensLeft, cancellationToken)
                    .ConfigureAwait(false)
                : await responsesClient.CreateResponseAsync(agent.Name, inputItems, agent.Version, outputTokensLeft, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (ClientResultException ex) when (IsGuardrailBlock(ex) || IsGatewayGuardrailBlock(ex, _runOptions.GatewayGuardrailErrors))
        {
            RecordCall(agent, started, null, "content_filter");
            throw new AgentGuardrailException(agent.Name, AgentGuardrailException.PromptStage, ex);
        }
        catch (Exception ex)
        {
            RecordCall(agent, started, null, ex is OperationCanceledException ? "cancelled" : ex.GetType().FullName ?? AgentTelemetry.OtherError);
            throw;
        }

        RecordCall(agent, started, response.Model, null);

        usage.Add(response.Usage);   // every response is billed, even in a run that later fails
        usage.Model ??= response.Model;

        if (response.IncompleteStatusDetails?.Reason == ResponseIncompleteStatusReason.MaxOutputTokens)
        {
            throw OutputTokenLimitReached(agent.Name);
        }

        if (response.IncompleteStatusDetails?.Reason == ResponseIncompleteStatusReason.ContentFilter)
        {
            throw new AgentGuardrailException(agent.Name, AgentGuardrailException.AnswerStage);
        }

        return response;
    }

    /// <summary>
    /// Foundry rejects a prompt its guardrail blocks with a 400 whose JSON error has code <c>content_filter</c>, or an inner
    /// error code of <c>ResponsibleAIPolicyViolation</c>. Read from the codes, not the message, whose wording can change.
    /// </summary>
    internal static bool IsGuardrailBlock(ClientResultException ex)
    {
        if (ex.Status != 400 || ex.GetRawResponse()?.Content is not { } content)
        {
            return false;
        }

        try
        {
            using var body = JsonDocument.Parse(content.ToMemory());
            if (!body.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return CodeOf(error) == "content_filter"
                || (error.TryGetProperty("innererror", out var inner) && CodeOf(inner) == "ResponsibleAIPolicyViolation");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A gateway in front of the model, e.g. API Management's <c>llm-content-safety</c> policy, blocked the prompt: the
    /// error has a configured status and, when one is given, its code (<c>error.code</c> or <c>code</c>).
    /// </summary>
    internal static bool IsGatewayGuardrailBlock(ClientResultException ex, IReadOnlyList<AgentsOptions.GatewayGuardrailError> gatewayErrors)
    {
        var matching = gatewayErrors.Where(error => error.Status == ex.Status).ToList();
        if (matching.Count == 0)
        {
            return false;
        }

        if (matching.Exists(static error => string.IsNullOrWhiteSpace(error.Code)))
        {
            return true;
        }

        var code = ErrorCodeOf(ex);
        return code is not null && matching.Exists(error => string.Equals(error.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ErrorCodeOf(ClientResultException ex)
    {
        if (ex.GetRawResponse()?.Content is not { } content)
        {
            return null;
        }

        try
        {
            using var body = JsonDocument.Parse(content.ToMemory());
            var root = body.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && CodeOf(error) is { } nested
                ? nested
                : CodeOf(root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? CodeOf(JsonElement element)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;

    /// <summary>How long one Foundry call took, so a slow model or deployment shows up in the metrics.</summary>
    private void RecordCall(AgentReference agent, long started, string? model, string? errorType)
        => AgentTelemetry.RecordOperation(_runOptions.ApplicationName, agent.Name, model,
            Stopwatch.GetElapsedTime(started).TotalSeconds, errorType);

    private InvalidOperationException OutputTokenLimitReached(string agentName)
        => new(string.Format(ErrorMessages.OutputTokenLimitReached, agentName, _runOptions.MaxOutputTokensPerRun));

    /// <summary>
    /// Attaches the tokens a failed run used to its exception, so a fallback result (and the briefing
    /// total built from it) still reports what Foundry billed. Read with <see cref="AgentTelemetry.TokenUsageOf"/>.
    /// </summary>
    private Exception WithUsage(Exception exception, UsageTally usage)
    {
        exception.Data[AgentTelemetry.TokenUsageDataKey] = usage.Total;
        if (_runOptions.Pricing.CostOf(usage.Model, usage.Total) is { } cost)
        {
            exception.Data[AgentTelemetry.CostDataKey] = cost;
        }

        return exception;
    }

    /// <summary>Adds up the usage of every response in one run, and how many model and tool calls it made.</summary>
    private sealed class UsageTally
    {
        public TokenUsage Total { get; private set; } = TokenUsage.None;

        public int InferenceCalls { get; set; }

        public int ToolCalls { get; set; }

        /// <summary>The model that answered, from the first response; it prices the run, even one that later fails.</summary>
        public string? Model { get; set; }

        public void Add(ResponseTokenUsage? usage)
        {
            if (usage is not null)
            {
                Total += new TokenUsage(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount)
                {
                    CachedInputTokens = usage.InputTokenDetails?.CachedTokenCount ?? 0,
                };
            }
        }
    }

    private static CancellationTokenSource CreateTimeoutSource(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    /// <summary>
    /// The first input: the evidence, cut to size, redacted, scanned for planted instructions and fenced as data, then the
    /// prompt. Cut before redacting, so a redactor or shield that calls a service only checks what's sent.
    /// </summary>
    private async Task<IReadOnlyList<ResponseItem>> BuildInitialInputItemsAsync(string agentName, string prompt, string? additionalContext,
        CancellationToken cancellationToken)
    {
        var redactedPrompt = await _runOptions.RedactAsync(prompt, cancellationToken).ConfigureAwait(false);
        var promptItem = ResponseItem.CreateUserMessageItem(redactedPrompt);
        if (string.IsNullOrWhiteSpace(additionalContext))
        {
            return [promptItem];
        }

        var evidence = await _runOptions.RedactAsync(LimitEvidence(agentName, additionalContext), cancellationToken).ConfigureAwait(false);
        if (evidenceShield is not null
            && await evidenceShield.FindsAttackAsync(redactedPrompt, evidence, cancellationToken).ConfigureAwait(false))
        {
            throw new AgentGuardrailException(agentName, AgentGuardrailException.EvidenceStage);
        }

        return [ResponseItem.CreateUserMessageItem(PromptText.FenceReferenceMaterial(evidence)), promptItem];
    }

    /// <summary>
    /// Keeps evidence within <see cref="AgentRunOptions.MaxEvidenceCharacters"/>, keeping the start (the most relevant
    /// results), so oversized evidence can't exceed the model's context or run up the bill. Cut before fencing.
    /// </summary>
    private string LimitEvidence(string agentName, string evidence)
    {
        var limit = _runOptions.MaxEvidenceCharacters;
        if (evidence.Length <= limit)
        {
            return evidence;
        }

        _logger.LogWarning("Evidence for agent {AgentName} was {Length} characters; truncated to {Limit}", agentName, evidence.Length, limit);
        return evidence[..limit] + string.Format(PromptText.EvidenceTruncated, evidence.Length - limit);
    }

    /// <summary>Throws when the final response failed or stopped early, with Foundry's reason.</summary>
    private static void ThrowIfNotCompleted(string agentName, ResponseResult response)
    {
        if (response.Status is null || response.Status == ResponseStatus.Completed)
        {
            return;
        }

        var detail = string.Empty;
        if (response.Error is not null)
        {
            detail = $": {response.Error.Message}";
        }
        else if (response.IncompleteStatusDetails?.Reason is { } reason)
        {
            detail = $": {reason}";
        }

        throw new InvalidOperationException(
            string.Format(ErrorMessages.ResponseDidNotComplete, agentName, response.Id, response.Status, detail));
    }

    /// <summary>Runs the model's tool calls in this app and returns their outputs, capped, redacted and fenced.</summary>
    private async Task<IReadOnlyList<ResponseItem>> ResolveToolCallsAsync(ResponseResult response,
        IReadOnlyList<FunctionCallResponseItem> toolCalls,
        ToolCallResolver? resolveToolCalls,
        CancellationToken cancellationToken)
    {
        if (resolveToolCalls is null)
        {
            throw new InvalidOperationException(string.Format(ErrorMessages.ToolCallsRequiredNoCallback, response.Id));
        }

        var requests = toolCalls
            .Select(call => new ToolCallRequest(call.CallId, call.FunctionName, call.FunctionArguments.ToString()))
            .ToList();
        var outputs = (await resolveToolCalls(requests, cancellationToken).ConfigureAwait(false)).ToList();

        var duplicate = outputs.GroupBy(o => o.CallId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(string.Format(ErrorMessages.DuplicateToolCallOutput, duplicate.Key));
        }

        var outputByCallId = outputs.ToDictionary(o => o.CallId, o => o.Output);

        var outputItems = new List<ResponseItem>();
        foreach (var callId in toolCalls.Select(toolCall => toolCall.CallId))
        {
            if (!outputByCallId.TryGetValue(callId, out var output))
            {
                throw new InvalidOperationException(string.Format(ErrorMessages.MissingToolCallOutput, callId));
            }

            var limited = await _runOptions.RedactAsync(LimitToolOutput(callId, output), cancellationToken).ConfigureAwait(false);
            outputItems.Add(ResponseItem.CreateFunctionCallOutputItem(callId,
                _runOptions.FenceToolOutput ? PromptText.FenceToolOutput(limited) : limited));
        }

        return outputItems;
    }

    /// <summary>
    /// Keeps a tool's output within <see cref="AgentRunOptions.MaxToolOutputCharacters"/>, so one large
    /// result can't flood the model's context (and the bill) or carry an unbounded injection payload.
    /// </summary>
    private string LimitToolOutput(string callId, string output)
    {
        var limit = _runOptions.MaxToolOutputCharacters;
        if (output.Length <= limit)
        {
            return output;
        }

        _logger.LogWarning("Tool output for call {CallId} was {Length} characters; truncated to {Limit}", callId, output.Length, limit);
        return output[..limit] + string.Format(PromptText.ToolOutputTruncated, output.Length - limit);
    }
}

using System.Text.Json;
using System.Text;
using GovUK.Dfe.AI.Agents.Mcp.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Constants;
using GovUK.Dfe.AI.Agents.Mcp.Exceptions;
using GovUK.Dfe.AI.Agents.Mcp.Options;
using GovUK.Dfe.AI.Agents.Mcp.Providers;
using GovUK.Dfe.AI.Agents.Mcp.Sessions.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Sessions;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.Mcp.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Mcp.Clients;

/// <summary>
/// Connects this app to one MCP server. Its tools are given to agents as plain function definitions
/// (name, description, input schema), and when the model calls one, this client runs it on the server
/// with the app's own credentials. Foundry never contacts the server and never holds a credential for it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Only tools in <see cref="McpServerConnectionOptions.AllowedToolNames"/> can ever be described to
/// an agent or run - a tool added to the server later stays unavailable until it's added to the list.</item>
/// <item>A connection that fails is dropped and reopened on the next call, so a server restart or an
/// expired session recovers on its own. Listing tools and reading prompts retry once on a fresh
/// connection; tool calls don't, because repeating one could repeat its side effects.</item>
/// <item>Each app instance has its own connection, token and tool-list cache.</item>
/// </list>
/// </remarks>
public sealed class McpToolClient : IMcpToolClient, IDisposable
{
    internal const string HttpClientName = "GovUK.Dfe.AI.Agents.McpToolClient";

    private const int MaxFunctionNameLength = 64;

    /// <summary>The longest a synchronous <see cref="Dispose"/> waits for the connection to close.</summary>
    private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly McpServerConnectionOptions _options;
    private readonly ILogger<McpToolClient> _logger;
    private readonly Func<CancellationToken, Task<IMcpSession>> _connect;
    private readonly HashSet<string> _allowedFunctionNames;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _toolsLock = new(1, 1);
    private readonly TimeSpan _disposeTimeout;
    private IMcpSession? _session;
    private int _disposed;
    private IReadOnlyList<Tool>? _cachedTools;
    private DateTimeOffset _toolsCachedAt;

    public McpToolClient(McpServerConnectionOptions options, IHttpClientFactory httpClientFactory,
        ILogger<McpToolClient> logger, string? httpClientName = null)
        : this(options, logger, cancellationToken => ConnectAsync(options, httpClientFactory, httpClientName ?? HttpClientName, logger, cancellationToken))
    {
    }

    internal McpToolClient(McpServerConnectionOptions options, ILogger<McpToolClient> logger, Func<CancellationToken, Task<IMcpSession>> connect,
        TimeSpan? disposeTimeout = null)
    {
        _options = options;
        _logger = logger;
        _connect = connect;
        _disposeTimeout = disposeTimeout ?? DefaultDisposeTimeout;

        // Deny by default: with no allow-list (options not validated), nothing can be described or run.
        _allowedFunctionNames = [.. (options.AllowedToolNames ?? []).Select(ToFunctionName)];
    }

    /// <summary>
    /// Describes the server's tools in <see cref="McpServerConnectionOptions.AllowedToolNames"/> as function tools.
    /// Implemented explicitly, so the public overload below is the only one with optional parameters.
    /// </summary>
    Task<IReadOnlyList<AgentTool>> IAgentToolProvider.GetToolsAsync(CancellationToken cancellationToken)
        => GetToolsAsync(allowedToolNames: null, cancellationToken);

    /// <summary>
    /// Describes <paramref name="allowedToolNames"/> as function tools, or all the server's allowed tools when none are given.
    /// Every name must be in the server's <see cref="McpServerConnectionOptions.AllowedToolNames"/>, which is the most any
    /// agent can have; <see cref="McpAllowedToolsProvider"/> gives an agent a subset.
    /// </summary>
    /// <param name="allowedToolNames">The tools to describe, or null/empty for all of the server's allowed tools.</param>
    public async Task<IReadOnlyList<AgentTool>> GetToolsAsync(IReadOnlyList<string>? allowedToolNames = null,
        CancellationToken cancellationToken = default)
    {
        var names = allowedToolNames is { Count: > 0 } requested ? requested : _options.AllowedToolNames ?? [];

        var notAllowed = names.Where(name => !_allowedFunctionNames.Contains(ToFunctionName(name))).ToList();
        if (notAllowed.Count > 0)
        {
            throw new McpToolConfigurationException(string.Format(ErrorMessages.McpToolNotAllowed, string.Join(", ", notAllowed), _options.ServerLabel));
        }

        var serverTools = await GetServerToolsAsync(cancellationToken).ConfigureAwait(false);
        var missing = names.Where(name => serverTools.All(tool => tool.Name != name)).ToList();
        if (missing.Count > 0)
        {
            throw new McpToolConfigurationException(
                string.Format(ErrorMessages.McpToolsNotFoundOnServer, _options.ServerLabel, string.Join(", ", missing)));
        }

        return BuildFunctionTools(_options.ServerLabel, serverTools.Where(tool => names.Contains(tool.Name)));
    }

    /// <summary>
    /// Runs the call when it's one of this server's allowed tools; otherwise returns <see langword="null"/>
    /// so another provider can claim it (or the run fails because nothing does).
    /// </summary>
    public async Task<string?> TryExecuteAsync(ToolCallRequest call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!_allowedFunctionNames.Contains(call.FunctionName))
        {
            return null;
        }

        return FindTool(await GetServerToolsAsync(cancellationToken).ConfigureAwait(false), call.FunctionName) is null
            ? null
            : await CallToolAsync(call.FunctionName, call.Arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs an allowed tool. Malformed arguments and the tool's own errors go back to the model as text.</summary>
    public async Task<string> CallToolAsync(string functionName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);

        // Checked here as well as in TryExecuteAsync, so no caller can reach a tool outside the allow-list.
        if (!_allowedFunctionNames.Contains(functionName))
        {
            throw new InvalidOperationException(string.Format(ErrorMessages.McpToolNotAllowed, functionName, _options.ServerLabel));
        }

        var toolName = FindTool(await GetServerToolsAsync(cancellationToken).ConfigureAwait(false), functionName)?.Name
            ?? throw new InvalidOperationException(string.Format(ErrorMessages.McpToolsNotFoundOnServer, _options.ServerLabel, functionName));

        // Malformed arguments are the model's mistake: tell it, rather than failing the run or dropping the connection.
        if (!TryParseArguments(argumentsJson, out var arguments))
        {
            _logger.LogWarning("Tool {ToolName} on MCP server '{ServerLabel}' was called with arguments that aren't a JSON object",
                toolName, _options.ServerLabel);
            return string.Format(ErrorMessages.McpToolArgumentsInvalid, functionName);
        }

        CallToolResult result;
        try
        {
            result = await WithSessionAsync(session => session.CallToolAsync(toolName, arguments, cancellationToken),
                retryOnFreshConnection: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Call to tool {ToolName} on MCP server '{ServerLabel}' failed", toolName, _options.ServerLabel);
            throw new InvalidOperationException(string.Format(ErrorMessages.McpToolCallFailed, toolName, _options.ServerLabel), ex);
        }

        var output = ReadOutput(result);
        if (result.IsError == true)
        {
            // The error text goes back to the model, which can often correct its arguments and retry. It
            // isn't logged: tool errors can quote the data the tool handles, such as pupil records.
            _logger.LogWarning("Tool {ToolName} on MCP server '{ServerLabel}' reported an error ({Length} characters)",
                toolName, _options.ServerLabel, output.Length);
            return $"The tool reported an error: {output}";
        }

        return output;
    }

    public async Task<string> GetPromptAsync(string name, string promptType,
        CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, object?> { ["promptType"] = promptType };
        var result = await WithSessionAsync(session => session.GetPromptAsync(name, arguments, cancellationToken),
            retryOnFreshConnection: true, cancellationToken).ConfigureAwait(false);

        var text = new StringBuilder();
        foreach (var message in result.Messages)
        {
            if (message.Content is TextContentBlock textContent)
            {
                text.AppendLine(textContent.Text);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// For a service provider disposed with <c>Dispose()</c> (console apps, tests), which can't call <see cref="DisposeAsync"/>.
    /// The connection is closed on the thread pool, so a caller's synchronization context can't deadlock it, and for at most
    /// a few seconds, so an unresponsive server can't hang shutdown.
    /// </summary>
    public void Dispose()
    {
        if (!Task.Run(() => DisposeAsync().AsTask()).Wait(_disposeTimeout))
        {
            _logger.LogWarning("Closing the connection to MCP server '{ServerLabel}' took longer than {Timeout}; not waiting for it",
                _options.ServerLabel, _disposeTimeout);
        }
    }

    /// <summary>Closes the connection. Later calls fail with <see cref="ObjectDisposedException"/> rather than reconnecting.</summary>
    /// <remarks>The locks aren't disposed: a call still waiting on one would otherwise fail with an unrelated error.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Describes MCP tools as function tools, ordered by name. Deterministic for the same tools, so it
    /// never causes a new agent version on its own, and it carries no credential of any kind.
    /// </summary>
    internal static IReadOnlyList<AgentTool> BuildFunctionTools(string serverLabel, IEnumerable<Tool> serverTools)
    {
        var tools = serverTools.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();

        var clash = tools.GroupBy(tool => ToFunctionName(tool.Name)).FirstOrDefault(group => group.Count() > 1);
        if (clash is not null)
        {
            throw new McpToolConfigurationException(string.Format(ErrorMessages.McpToolNamesClash, serverLabel,
                string.Join(", ", clash.Select(tool => tool.Name)), clash.Key));
        }

        return [.. tools.Select(tool => AgentTool.Function(ToFunctionName(tool.Name), tool.Description,
            BinaryData.FromString(tool.InputSchema.GetRawText())))];
    }

    /// <summary>
    /// Function names may only contain letters, digits, '_' and '-', up to 64 characters. MCP tool names
    /// that don't fit are adjusted the same way everywhere, so calls map back to the right tool.
    /// </summary>
    internal static string ToFunctionName(string toolName)
    {
        var safe = new string([.. toolName.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')]);
        return safe.Length <= MaxFunctionNameLength ? safe : safe[..MaxFunctionNameLength];
    }

    internal static string ReadOutput(CallToolResult result)
    {
        var text = string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        if (text.Length == 0 && result.StructuredContent is { } structured)
        {
            text = structured.ToString();
        }

        return text;
    }

    private static bool TryParseArguments(string argumentsJson, out Dictionary<string, object?> arguments)
    {
        arguments = [];
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            arguments = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The server tool behind a function name, or null if the server doesn't have it.</summary>
    private static Tool? FindTool(IReadOnlyList<Tool> serverTools, string functionName)
        => serverTools.FirstOrDefault(tool => ToFunctionName(tool.Name) == functionName);

    private bool ToolListIsFresh => _cachedTools is not null && DateTimeOffset.UtcNow - _toolsCachedAt < _options.ToolListCacheDuration;

    /// <summary>The server's tools, cached for <see cref="McpServerConnectionOptions.ToolListCacheDuration"/>.</summary>
    private async Task<IReadOnlyList<Tool>> GetServerToolsAsync(CancellationToken cancellationToken)
    {
        if (ToolListIsFresh)
        {
            return _cachedTools!;
        }

        await _toolsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ToolListIsFresh)
            {
                return _cachedTools!;
            }

            _cachedTools = await WithSessionAsync(session => session.ListToolsAsync(cancellationToken),
                retryOnFreshConnection: true, cancellationToken).ConfigureAwait(false);
            _toolsCachedAt = DateTimeOffset.UtcNow;
            return _cachedTools;
        }
        finally
        {
            _toolsLock.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on the current connection. If it fails, that connection is
    /// dropped so the next call opens a new one - and, for operations that are safe to repeat, the
    /// operation is tried once more straight away on the new connection.
    /// </summary>
    private async Task<T> WithSessionAsync<T>(Func<IMcpSession, Task<T>> operation, bool retryOnFreshConnection,
        CancellationToken cancellationToken)
    {
        var maxAttempts = retryOnFreshConnection ? 2 : 1;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(session).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await DropSessionAsync(session).ConfigureAwait(false);

                if (attempt == maxAttempts)
                {
                    throw;
                }

                _logger.LogWarning(ex, "MCP server '{ServerLabel}' connection failed; retrying on a new connection", _options.ServerLabel);
            }
        }

        throw new System.Diagnostics.UnreachableException();
    }

    private async Task<IMcpSession> GetSessionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (Volatile.Read(ref _session) is { } current)
        {
            return current;
        }

        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (_session is { } connected)
            {
                return connected;
            }

            // A failed connect isn't stored, so the next call tries again rather than failing forever.
            var opened = await _connect(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _session, opened);

            // Disposed while connecting: close the new connection too, rather than leave it open.
            if (Volatile.Read(ref _disposed) == 1 && Interlocked.CompareExchange(ref _session, null, opened) == opened)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(GetType().FullName);
            }

            return opened;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task DropSessionAsync(IMcpSession failed)
    {
        // Only the connection that failed is dropped; another caller may already have replaced it.
        if (Interlocked.CompareExchange(ref _session, null, failed) != failed)
        {
            return;
        }

        try
        {
            await failed.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.IgnoringCloseError(ex, _options.ServerLabel);
        }
    }

    private static async Task<IMcpSession> ConnectAsync(McpServerConnectionOptions options, IHttpClientFactory httpClientFactory,
        string httpClientName, ILogger logger, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = options.ServerUri,
            Name = options.ServerLabel,
            TransportMode = HttpTransportMode.StreamableHttp,
        }, httpClientFactory.CreateClient(httpClientName));

        try
        {
            var client = await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = options.ProtocolVersion },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new McpClientSession(client);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to connect to MCP server '{ServerLabel}' at {ServerUri}", options.ServerLabel, options.ServerUri);
            throw new InvalidOperationException(string.Format(ErrorMessages.McpConnectionFailed, options.ServerLabel, options.ServerUri), ex);
        }
    }
}

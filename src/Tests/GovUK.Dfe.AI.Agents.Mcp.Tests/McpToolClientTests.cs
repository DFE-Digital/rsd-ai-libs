using System.Text.Json;
using GovUK.Dfe.AI.Agents.Mcp.Clients;
using GovUK.Dfe.AI.Agents.Mcp.Exceptions;
using GovUK.Dfe.AI.Agents.Mcp.Options;
using GovUK.Dfe.AI.Agents.Mcp.Sessions.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using NSubstitute;
using OpenAI.Responses;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Mcp.Tests;

/// <summary>
/// McpToolClient against a fake connection, so connection failures and reconnects can be tested
/// without a real MCP server.
/// </summary>
public sealed class McpToolClientTests
{
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    private sealed class FakeSession(Func<int, IReadOnlyList<Tool>> listTools, Func<string, CallToolResult>? callTool = null,
        Task? closing = null, Func<GetPromptResult>? prompt = null) : IMcpSession
    {
        public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }

        public int ListCalls { get; private set; }
        public int ToolCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken) => Task.FromResult(listTools(++ListCalls));

        public Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            ToolCalls++;
            LastArguments = arguments;
            return Task.FromResult(callTool?.Invoke(toolName) ?? new CallToolResult { Content = [new TextContentBlock { Text = $"{toolName} ok" }] });
        }

        public Task<GetPromptResult> GetPromptAsync(string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
        {
            LastArguments = arguments;
            return Task.FromResult(prompt?.Invoke() ?? new GetPromptResult { Messages = [] });
        }

        public int DisposeCalls { get; private set; }

        /// <summary>Closing waits for <c>closing</c>, e.g. a server that never answers.</summary>
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposeCalls++;
            return new ValueTask(closing ?? Task.CompletedTask);
        }
    }

    private static Tool ServerTool(string name) => new()
    {
        Name = name,
        InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
    };

    private static readonly IReadOnlyList<Tool> ServerTools = [ServerTool("get_performance_data"), ServerTool("update_school_record")];

    private static McpServerConnectionOptions Options(params string[] allowed) => new()
    {
        ServerLabel = "school-performance-mcp",
        ServerUri = new Uri("https://mcp.example.gov.uk/mcp"),
        AllowedToolNames = allowed,
        ToolListCacheDuration = TimeSpan.Zero,
        Credential = NSubstitute.Substitute.For<Azure.Core.TokenCredential>(),
        Scope = "api://mcp/.default",
    };

    [Fact]
    public async Task AFailedConnection_IsReplaced_AndSafeOperationsRetryOnTheNewOne()
    {
        var broken = new FakeSession(_ => throw new HttpRequestException("Session expired."));
        var healthy = new FakeSession(_ => ServerTools);
        var sessions = new Queue<IMcpSession>([broken, healthy]);
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ => Task.FromResult(sessions.Dequeue()));

        var tools = await sut.GetToolsAsync(cancellationToken: cancellationToken);

        Assert.Equal("get_performance_data", Assert.IsType<FunctionTool>(Assert.Single(tools)).FunctionName);
        Assert.True(broken.Disposed);
        Assert.Equal(1, healthy.ListCalls);
    }

    [Fact]
    public async Task AFailedConnect_IsNotRemembered_SoTheNextCallTriesAgain()
    {
        var attempts = 0;
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ =>
            ++attempts == 1
                ? Task.FromException<IMcpSession>(new HttpRequestException("Server restarting."))
                : Task.FromResult<IMcpSession>(new FakeSession(_ => ServerTools)));

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.GetToolsAsync(cancellationToken: cancellationToken));
        var tools = await sut.GetToolsAsync(cancellationToken: cancellationToken);

        Assert.Single(tools);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AFailedToolCall_DropsTheConnection_ButIsNotRetried_SoItsSideEffectsArentRepeated()
    {
        var failing = new FakeSession(_ => ServerTools, _ => throw new HttpRequestException("Connection reset."));
        var replacement = new FakeSession(_ => ServerTools);
        var sessions = new Queue<IMcpSession>([failing, replacement]);
        var sut = new McpToolClient(Options("update_school_record"), NullLogger<McpToolClient>.Instance, _ => Task.FromResult(sessions.Dequeue()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CallToolAsync("update_school_record", "{}", cancellationToken));

        Assert.Equal(1, failing.ToolCalls);
        Assert.True(failing.Disposed);
        Assert.Equal("update_school_record ok", await sut.CallToolAsync("update_school_record", "{}", cancellationToken));
    }

    [Fact]
    public async Task AToolOutsideAllowedToolNames_IsNeverDescribedOrRun_EvenThoughTheServerHasIt()
    {
        var session = new FakeSession(_ => ServerTools);
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ => Task.FromResult<IMcpSession>(session));

        var described = await sut.GetToolsAsync(cancellationToken: cancellationToken);
        var viaExecutor = await sut.TryExecuteAsync(new ToolCallRequest("call-1", "update_school_record", "{}"), cancellationToken);

        Assert.DoesNotContain(described.OfType<FunctionTool>(), tool => tool.FunctionName == "update_school_record");
        Assert.Null(viaExecutor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CallToolAsync("update_school_record", "{}", cancellationToken));
        await Assert.ThrowsAsync<McpToolConfigurationException>(() => sut.GetToolsAsync(["update_school_record"], cancellationToken));
        Assert.Equal(0, session.ToolCalls);
    }

    [Fact]
    public async Task AToolReportedError_GoesBackToTheModel()
    {
        var session = new FakeSession(_ => ServerTools, _ => new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "URN 999999 not found." }],
        });
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ => Task.FromResult<IMcpSession>(session));

        var output = await sut.CallToolAsync("get_performance_data", "{\"urn\":\"999999\"}", cancellationToken);

        Assert.Equal("The tool reported an error: URN 999999 not found.", output);
    }

    [Theory]
    [InlineData("{\"urn\":")]       // truncated
    [InlineData("[\"100000\"]")]    // not an object
    public async Task MalformedArguments_GoBackToTheModel_WithoutCallingTheServerOrDroppingTheConnection(string arguments)
    {
        var session = new FakeSession(_ => ServerTools);
        var connects = 0;
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ =>
        {
            connects++;
            return Task.FromResult<IMcpSession>(session);
        });

        var output = await sut.CallToolAsync("get_performance_data", arguments, cancellationToken);
        await sut.CallToolAsync("get_performance_data", "{\"urn\":\"100000\"}", cancellationToken);

        Assert.Contains("valid JSON object", output, StringComparison.Ordinal);
        Assert.Equal(1, session.ToolCalls);
        Assert.Equal(1, connects);
        Assert.False(session.Disposed);
    }

    // ===================== Tools the server lists =====================

    [Fact]
    public async Task AToolAllowedHereButMissingFromTheServer_FailsToBeDescribed_NamingIt()
    {
        var session = new FakeSession(_ => [ServerTool("get_performance_data")]);
        var sut = new McpToolClient(Options("get_performance_data", "get_attendance"), NullLogger<McpToolClient>.Instance,
            _ => Task.FromResult<IMcpSession>(session));

        var ex = await Assert.ThrowsAsync<McpToolConfigurationException>(() => sut.GetToolsAsync(cancellationToken: cancellationToken));

        Assert.Contains("get_attendance", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("get_performance_data", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgentsToolCall_RunsOnTheServer_ButIsDeclinedOnceTheServerDropsTheTool()
    {
        var listed = ServerTools;
        var session = new FakeSession(_ => listed);
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ => Task.FromResult<IMcpSession>(session));
        var call = new ToolCallRequest("call-1", "get_performance_data", """{"urn":"100000"}""");

        var output = await sut.TryExecuteAsync(call, cancellationToken);
        listed = [ServerTool("update_school_record")];   // the server's next release drops get_performance_data
        var afterRemoval = await sut.TryExecuteAsync(call, cancellationToken);

        Assert.Equal("get_performance_data ok", output);
        Assert.Null(afterRemoval);   // declined, so the run fails clearly rather than calling a tool that's gone
        Assert.Equal(1, session.ToolCalls);
    }

    [Fact]
    public async Task TheToolList_IsReusedForItsCacheDuration_SoCallsDontAskTheServerEachTime()
    {
        var session = new FakeSession(_ => ServerTools);
        var sut = new McpToolClient(Options("get_performance_data") with { ToolListCacheDuration = TimeSpan.FromMinutes(5) },
            NullLogger<McpToolClient>.Instance, _ => Task.FromResult<IMcpSession>(session));

        await sut.GetToolsAsync(cancellationToken: cancellationToken);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sut.CallToolAsync("get_performance_data", "{}", cancellationToken)));

        Assert.Equal(1, session.ListCalls);
        Assert.Equal(5, session.ToolCalls);
    }

    // ===================== Tool calls =====================

    [Fact]
    public async Task AToolThatAnswersOnlyWithStructuredData_ReachesTheModelAsJson()
    {
        var session = new FakeSession(_ => ServerTools, _ => new CallToolResult
        {
            Content = [],
            StructuredContent = JsonDocument.Parse("""{"urn":"100000","met":72}""").RootElement,
        });
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ => Task.FromResult<IMcpSession>(session));

        var output = await sut.CallToolAsync("get_performance_data", """{"urn":"100000"}""", cancellationToken);

        Assert.Equal("""{"urn":"100000","met":72}""", output);
    }

    [Fact]
    public async Task AToolCalledWithNoArguments_IsRunWithNone()
    {
        var session = new FakeSession(_ => ServerTools);
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ => Task.FromResult<IMcpSession>(session));

        await sut.CallToolAsync("get_performance_data", "", cancellationToken);

        Assert.Empty(session.LastArguments!);
    }

    // ===================== Connections =====================

    [Fact]
    public async Task CallersArrivingTogether_ShareOneConnection()
    {
        var connecting = new TaskCompletionSource<IMcpSession>();
        var connects = 0;
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ =>
        {
            Interlocked.Increment(ref connects);
            return connecting.Task;
        });

        var calls = Enumerable.Range(0, 4).Select(_ => sut.CallToolAsync("get_performance_data", "{}", cancellationToken)).ToList();
        connecting.SetResult(new FakeSession(_ => ServerTools));
        await Task.WhenAll(calls);

        Assert.Equal(1, connects);
    }

    [Fact]
    public async Task ABrokenConnectionThatAlsoFailsToClose_IsStillReplaced()
    {
        var broken = new FakeSession(_ => throw new HttpRequestException("Session expired."),
            closing: Task.FromException(new IOException("Connection already reset.")));
        var healthy = new FakeSession(_ => ServerTools);
        var sessions = new Queue<IMcpSession>([broken, healthy]);
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ => Task.FromResult(sessions.Dequeue()));

        var tools = await sut.GetToolsAsync(cancellationToken: cancellationToken);

        Assert.Single(tools);
        Assert.True(broken.Disposed);
    }

    [Fact]
    public async Task AnUnreachableServer_FailsWithAnErrorNamingTheServerAndItsAddress()
    {
        // Nothing listens on the discard port, so the real transport fails to connect.
        var options = Options("get_performance_data") with { ServerUri = new Uri("http://127.0.0.1:9/mcp") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        using var sut = new McpToolClient(options, factory, NullLogger<McpToolClient>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetToolsAsync(cancellationToken: cancellationToken));

        Assert.Contains("school-performance-mcp", ex.Message, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:9/mcp", ex.Message, StringComparison.Ordinal);
    }

    // ===================== Prompts =====================

    [Fact]
    public async Task APromptFromTheServer_IsItsTextOnly_ReadAgainOnAFreshConnectionIfTheFirstFails()
    {
        var broken = new FakeSession(_ => ServerTools, prompt: () => throw new HttpRequestException("Session expired."));
        var healthy = new FakeSession(_ => ServerTools, prompt: () => new GetPromptResult
        {
            Messages =
            [
                new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = "You analyse Ofsted reports." } },
                new PromptMessage { Role = Role.User, Content = new ImageContentBlock { Data = new byte[] { 1 }, MimeType = "image/png" } },
                new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = "Cite the evidence." } },
            ],
        });
        var sessions = new Queue<IMcpSession>([broken, healthy]);
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ => Task.FromResult(sessions.Dequeue()));

        var prompt = await sut.GetPromptAsync("ofsted", "system", cancellationToken);

        Assert.Equal($"You analyse Ofsted reports.{Environment.NewLine}Cite the evidence.{Environment.NewLine}", prompt);
        Assert.Equal("system", healthy.LastArguments!["promptType"]);
        Assert.True(broken.Disposed);
    }

    // ===================== Shutdown =====================

    [Fact]
    public async Task OnShutdown_TheConnectionIsClosedOnce_AndALateCallFails_RatherThanReconnecting()
    {
        var session = new FakeSession(_ => ServerTools);
        var connects = 0;
        var mcpToolClient = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ =>
        {
            connects++;
            return Task.FromResult<IMcpSession>(session);
        });
        await mcpToolClient.GetToolsAsync(cancellationToken: cancellationToken);

        await mcpToolClient.DisposeAsync();
        await mcpToolClient.DisposeAsync();
        McpToolClient disposedByAContainer = mcpToolClient;   // as a service provider disposed with Dispose() does
        disposedByAContainer.Dispose();

        Assert.Equal(1, session.DisposeCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => mcpToolClient.CallToolAsync("get_performance_data", "{}", cancellationToken));
        Assert.Equal(1, connects);
    }

    [Fact]
    public async Task ASynchronousShutdown_DoesntHang_WhenTheServerNeverAnswersTheClose()
    {
        var neverCloses = new TaskCompletionSource();
        var session = new FakeSession(_ => ServerTools, closing: neverCloses.Task);
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance,
            _ => Task.FromResult<IMcpSession>(session), disposeTimeout: TimeSpan.FromMilliseconds(100));
        await sut.GetToolsAsync(cancellationToken: cancellationToken);

        var shutdown = Task.Run(sut.Dispose, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(shutdown, await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken)));
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task AConnectionOpenedWhileShuttingDown_IsClosedToo()
    {
        var connecting = new TaskCompletionSource<IMcpSession>();
        var lateSession = new FakeSession(_ => ServerTools);
        var sut = new McpToolClient(Options("get_performance_data"), NullLogger<McpToolClient>.Instance, _ => connecting.Task);

        var call = sut.GetToolsAsync(cancellationToken: cancellationToken);
        await sut.DisposeAsync();
        connecting.SetResult(lateSession);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => call);
        Assert.True(lateSession.Disposed);
    }
}

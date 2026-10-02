using GovUK.Dfe.AI.Agents.Mcp.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Exceptions;
using GovUK.Dfe.AI.Agents.Mcp.Options;
using GovUK.Dfe.AI.Agents.Mcp.Validators;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NSubstitute;
using OpenAI.Responses;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Mcp.Tests;

/// <summary>
/// An MCP server's settings and the startup check: every settings problem named in one error, an allowed tool the server
/// lacks fails startup, and an unreachable server only warns.
/// </summary>
public sealed class McpToolStartupValidatorTests
{
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;
    private readonly IMcpToolClient _client = Substitute.For<IMcpToolClient>();
    private readonly ILogger<McpToolStartupValidator> _logger = Substitute.For<ILogger<McpToolStartupValidator>>();

    private static McpServerConnectionOptions CreateValidOptions() => new()
    {
        ServerLabel = "my-tools",
        ServerUri = new Uri("https://mcp.example.com"),
        AllowedToolNames = ["get_performance_data"],
        Credential = Substitute.For<Azure.Core.TokenCredential>(),
        Scope = "api://mcp/.default",
    };

    private McpToolStartupValidator CreateSut(McpServerConnectionOptions? options = null)
        => new("my-tools", _client, options ?? CreateValidOptions(), _logger);

    [Fact]
    public async Task StartAsync_FailsStartup_WhenTheServerLacksAnAllowedTool()
    {
        var exception = new McpToolConfigurationException("MCP server 'my-tools' does not expose the following configured tool(s): bar.");
        _client.GetToolsAsync(cancellationToken).Throws(exception);
        var sut = CreateSut();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(cancellationToken));
        Assert.Same(exception, thrown.InnerException);
    }

    [Fact]
    public async Task StartAsync_Passes_WhenTheServerHasEveryAllowedTool()
    {
        _client.GetToolsAsync(cancellationToken).Returns([FunctionTool.CreateFunctionTool("get_performance_data", BinaryData.FromString("{}"), false)]);
        var sut = CreateSut();

        await sut.StartAsync(cancellationToken);
        await sut.StopAsync(cancellationToken);

        await _client.Received(1).GetToolsAsync(cancellationToken);
        Assert.Empty(_logger.ReceivedCalls());
    }

    [Fact]
    public async Task StartAsync_OnlyWarns_WhenTheServerIsUnreachable_SoAnOutageCantStopInstancesStarting()
    {
        _client.GetToolsAsync(cancellationToken).Throws(new HttpRequestException("Connection refused."));
        var sut = CreateSut();

        Assert.Null(await Record.ExceptionAsync(() => sut.StartAsync(cancellationToken)));
    }

    [Fact]
    public async Task StartAsync_ThrowsAndNeverConnects_WhenOptionsAreInvalid()
    {
        var invalidOptions = CreateValidOptions() with { ServerLabel = "" };
        var sut = CreateSut(invalidOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.StartAsync(cancellationToken));

        await _client.DidNotReceiveWithAnyArgs().GetToolsAsync(cancellationToken);
    }

    [Fact]
    public void Validate_NamesEveryProblem_AndTheServer_InOneError()
    {
        var options = CreateValidOptions() with
        {
            ServerLabel = " ",
            ServerUri = new Uri("/relative", UriKind.Relative),
            AllowedToolNames = [],
            Credential = null!,   // deliberately missing
            Scope = "",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate("performance-mcp"));

        foreach (var expected in new[] { "performance-mcp", "ServerLabel", "ServerUri", "AllowedToolNames", "Credential", "Scope" })
        {
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        }
    }
}

using Azure.Core;
using Azure.Identity;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Clients;
using GovUK.Dfe.AI.Agents.Mcp.Clients.Interfaces;
using GovUK.Dfe.AI.Agents.Mcp.Options;
using GovUK.Dfe.AI.Agents.Mcp.Providers;
using GovUK.Dfe.AI.Agents.Tests.Integration.Fakes;
using GovUK.Dfe.AI.Agents.Tools;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Mcp.Tests;

/// <summary><c>agents.AddMcpServers()</c>: each server's sign-in, each agent's tools, and a tool call end to end.</summary>
public sealed class McpServersTests : IDisposable
{
    private readonly string _prompt = Path.GetTempFileName();
    private static readonly string[] second = ["get_performance_data"];

    public void Dispose() => File.Delete(_prompt);

    private static Dictionary<string, string?> Settings() => new()
    {
        ["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/briefings",
        ["AiAgents:Foundry:DefaultModel"] = "myconnection/gpt-5.1",
        ["AiAgents:Authentication:TenantId"] = "tenant-1",
        ["AiAgents:Authentication:ClientId"] = "client-1",
        ["AiAgents:Authentication:ClientSecret"] = "secret-1",
        ["AiAgents:McpServers:school-performance:ServerUri"] = "https://mcp.internal.example/mcp",
        ["AiAgents:McpServers:school-performance:Scope"] = "api://school-performance/.default",
        ["AiAgents:McpServers:school-performance:AllowedToolNames:0"] = "get_performance_data",
        ["AiAgents:McpServers:school-performance:AllowedToolNames:1"] = "get_absence_data",
    };

    private static ServiceCollection Services(Dictionary<string, string?> settings, Action<AgentsBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), agents =>
        {
            agents.AddMcpServers();
            configure?.Invoke(agents);
        });
        return services;
    }

    [Fact]
    public void EachServer_GetsItsScopeAndAllowedTools_AndCanSignInWithItsOwnServicePrincipal_WithNoDefault()
    {
        var settings = Settings();
        foreach (var key in settings.Keys.Where(key => key.StartsWith("AiAgents:Authentication:", StringComparison.Ordinal)).ToList())
        {
            settings.Remove(key);
        }

        foreach (var path in new[] { "Foundry", "McpServers:school-performance" })
        {
            settings[$"AiAgents:{path}:Authentication:TenantId"] = "partner-tenant";
            settings[$"AiAgents:{path}:Authentication:ClientId"] = $"{path}-client";
            settings[$"AiAgents:{path}:Authentication:ClientSecret"] = $"{path}-secret";
        }

        using var provider = Services(settings).BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredKeyedService<IMcpToolClient>("school-performance"));
        var server = provider.GetRequiredKeyedService<McpServerConnectionOptions>("school-performance");
        Assert.Equal(["get_performance_data", "get_absence_data"], server.AllowedToolNames);
        Assert.Equal("api://school-performance/.default", server.Scope);
        Assert.IsType<ClientSecretCredential>(server.Credential);
    }

    [Fact]
    public void ACredentialInCode_OverridesOneServer_AndTheOthersUseTheDefault()
    {
        var settings = Settings();
        settings["AiAgents:McpServers:news:ServerUri"] = "https://news.example/mcp";
        settings["AiAgents:McpServers:news:Scope"] = "api://news/.default";
        settings["AiAgents:McpServers:news:AllowedToolNames:0"] = "search_news";
        var defaultCredential = Substitute.For<TokenCredential>();
        var partnerCredential = Substitute.For<TokenCredential>();

        using var provider = Services(settings, agents => agents.UseCredential(defaultCredential)
            .UseMcpCredential("school-performance", partnerCredential)).BuildServiceProvider();

        Assert.Same(partnerCredential, provider.GetRequiredKeyedService<McpServerConnectionOptions>("school-performance").Credential);
        Assert.Same(defaultCredential, provider.GetRequiredKeyedService<McpServerConnectionOptions>("news").Credential);
    }

    [Fact]
    public void AddMcpServers_WithoutAnMcpServersSection_FailsStartup()
    {
        var settings = Settings();
        foreach (var key in settings.Keys.Where(key => key.StartsWith("AiAgents:McpServers:", StringComparison.Ordinal)).ToList())
        {
            settings.Remove(key);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => Services(settings));

        Assert.Contains("AiAgents:McpServers (agents.AddMcpServers() needs this section)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgent_IsGivenOnlyItsAllowedToolsFromTheServer_AndItsToolCallsRunThere_WithTheServersToken()
    {
        await File.WriteAllTextAsync(_prompt, "You summarise school performance.", cancellationToken: TestContext.Current.CancellationToken);
        var settings = Settings();
        settings["AiAgents:PromptFiles:SystemPrompts:Performance"] = _prompt;
        var definition = new AgentDefinition("performance-agent", "Performance") { AllowedTools = ["get_performance_data"] };
        IReadOnlyList<OpenAI.Responses.ResponseTool> tools = [FakeToolServer.FunctionTool("get_performance_data")];
        var server = Substitute.For<IMcpToolClient>();
        server.GetToolsAsync(Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(tools));
        server.CallToolAsync("get_performance_data", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("KS2: 72% met the expected standard.");
        var foundry = new InMemoryFoundry();
        var conversations = new ScriptedConversationClient();
        conversations.Reply("performance-agent",
            FoundryResponses.FunctionCall("r1", "call-1", "get_performance_data"),
            FoundryResponses.Completed("r2", "72% met the expected standard."));

        var services = Services(settings, agents => agents.UseCredential(Substitute.For<TokenCredential>()).AddAgents(definition));
        services.AddSingleton(foundry.Admin);
        services.AddSingleton<IFoundryConversationClient>(conversations);
        services.AddKeyedSingleton("school-performance", server);   // the server's client; the real one would call it over HTTP
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IAgentService>().RunAsync(definition, "Summarise URN 100000.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("72% met the expected standard.", result.Output);
        Assert.IsType<McpAllowedToolsProvider>(Assert.Single(provider.GetServices<AgentToolBinding>()).Provider);
        await server.Received().GetToolsAsync(   // not get_absence_data, which the server also allows
            Arg.Is<IReadOnlyList<string>?>(names => names != null && names.SequenceEqual(second)), Arg.Any<CancellationToken>());
        await server.Received(1).CallToolAsync("get_performance_data", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EveryRequest_CarriesATokenTheLibraryGotFromTheAppsCredential_ForThatServersScope_FetchedOnce()
    {
        var credential = new RecordingCredential();
        var server = new CapturingHandler();
        var services = new ServiceCollection().AddLogging();
        services.AddMcpClientServices("school-performance", new McpServerConnectionOptions
        {
            ServerLabel = "school-performance",
            ServerUri = new Uri("https://school-performance.example.com/mcp"),
            AllowedToolNames = ["get_performance_data"],
            Credential = credential,
            Scope = "api://school-performance/.default",
        });
        services.AddHttpClient($"{McpToolClient.HttpClientName}:school-performance").ConfigurePrimaryHttpMessageHandler(() => server);

        await using var provider = services.BuildServiceProvider();
        using var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient($"{McpToolClient.HttpClientName}:school-performance");
        await http.GetAsync(new Uri("https://school-performance.example.com/mcp"), cancellationToken: TestContext.Current.CancellationToken);
        await http.GetAsync(new Uri("https://school-performance.example.com/mcp"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Bearer token-for-api://school-performance/.default", server.Authorization);
        Assert.Equal(["api://school-performance/.default"], credential.RequestedScopes);   // fetched once, then cached
    }

    private sealed class RecordingCredential : TokenCredential
    {
        public List<string> RequestedScopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestedScopes.AddRange(requestContext.Scopes);
            return new($"token-for-{requestContext.Scopes[0]}", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}

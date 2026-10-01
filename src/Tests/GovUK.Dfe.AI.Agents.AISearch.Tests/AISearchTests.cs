using Azure.Core;
using Azure.Identity;
using GovUK.Dfe.AI.Agents.AISearch.Context;
using GovUK.Dfe.AI.Agents.AISearch.Options;
using GovUK.Dfe.AI.Agents.Context.Interfaces;
using GovUK.Dfe.AI.Agents.Context;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.AISearch.Tests;

/// <summary><c>agents.AddAISearch()</c>: the indexes, their search settings and their sign-in, from <c>AiAgents:Search</c>.</summary>
public sealed class AISearchTests
{
    private static Dictionary<string, string?> Settings() => new()
    {
        ["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/briefings",
        ["AiAgents:Foundry:DefaultModel"] = "myconnection/gpt-5.1",
        ["AiAgents:Search:Endpoint"] = "https://example.search.windows.net",
        ["AiAgents:Search:Indexes:0:Name"] = "ofsted_index",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings, bool useCodeCredential = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), agents =>
        {
            agents.AddAISearch();
            if (useCodeCredential)
            {
                agents.UseCredential(Substitute.For<TokenCredential>());
            }
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void RegistersEachIndex_WithItsFields_SemanticRankingAndVectorFields_SignedInWithItsOwnServicePrincipal()
    {
        var settings = Settings();
        settings["AiAgents:Search:Indexes:0:ContentFields:0"] = "title";
        settings["AiAgents:Search:Indexes:0:ContentFields:1"] = "content";
        settings["AiAgents:Search:Indexes:0:SemanticConfiguration"] = "default";
        settings["AiAgents:Search:Indexes:0:VectorFields:0"] = "contentVector";
        settings["AiAgents:Search:Indexes:1:Name"] = "news_index";
        settings["AiAgents:Foundry:Authentication:TenantId"] = "tenant-1";
        settings["AiAgents:Foundry:Authentication:ClientId"] = "foundry-client";
        settings["AiAgents:Foundry:Authentication:ClientSecret"] = "foundry-secret";
        settings["AiAgents:Search:Authentication:TenantId"] = "tenant-1";
        settings["AiAgents:Search:Authentication:ClientId"] = "search-client";
        settings["AiAgents:Search:Authentication:ClientSecret"] = "search-secret";

        using var provider = Build(settings, useCodeCredential: false);   // no default service principal needed

        Assert.IsType<AzureSearchContextRetriever>(provider.GetRequiredService<IContextRetriever>());
        var indexes = provider.GetRequiredService<AzureSearchContextRetrieverOptions>().Indexes;
        var ofsted = indexes.Single(index => index.Name == "ofsted_index");
        Assert.Equal(["title", "content"], ofsted.ContentFields);
        Assert.Equal("default", ofsted.SemanticConfiguration);
        Assert.Equal(["contentVector"], ofsted.VectorFields);
        Assert.Empty(indexes.Single(index => index.Name == "news_index").VectorFields);   // keyword search only
    }

    [Fact]
    public void TwoIndexesWithTheSameName_FailValidation()
    {
        var settings = Settings();
        settings["AiAgents:Search:Indexes:1:Name"] = "ofsted_index";
        using var provider = Build(settings);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureSearchContextRetrieverOptions>>().Value);

        Assert.Contains("unique Name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAISearch_WithoutASearchSection_FailsStartup()
    {
        var settings = Settings();
        settings.Remove("AiAgents:Search:Endpoint");
        settings.Remove("AiAgents:Search:Indexes:0:Name");

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("AiAgents:Search (agents.AddAISearch() needs this section)", ex.Message, StringComparison.Ordinal);
    }
}

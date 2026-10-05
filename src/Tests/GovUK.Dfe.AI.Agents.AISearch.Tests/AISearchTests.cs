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

    [Fact]
    public void AnIncompleteSearchServicePrincipal_FailsStartup_NamingTheMissingSetting()
    {
        var settings = Settings();
        settings["AiAgents:Search:Authentication:TenantId"] = "tenant-1";
        settings["AiAgents:Search:Authentication:ClientId"] = "search-client";   // no secret

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("AiAgents:Search:Authentication:ClientSecret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseAISearchCredential_ReplacesTheSearchServicePrincipal()
    {
        var settings = Settings();
        settings["AiAgents:Search:Authentication:TenantId"] = "tenant-1";   // incomplete, but the code credential wins

        var services = new ServiceCollection();
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), agents => agents
            .AddAISearch()
            .UseCredential(Substitute.For<TokenCredential>())
            .UseAISearchCredential(Substitute.For<TokenCredential>()));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<AzureSearchContextRetriever>(provider.GetRequiredService<IContextRetriever>());
    }

    [Fact]
    public void AddAISearch_CalledTwice_RegistersOneRetriever()
    {
        var services = new ServiceCollection();
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build(), agents => agents
            .AddAISearch().AddAISearch().UseCredential(Substitute.For<TokenCredential>()));

        Assert.Single(services, service => service.ServiceType == typeof(IContextRetriever));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void AnEvidenceLimitBelowOne_FailsValidation(string limit)
    {
        var settings = Settings();
        settings["AiAgents:Search:MaxEvidenceCharacters"] = limit;
        using var provider = Build(settings);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureSearchContextRetrieverOptions>>().Value);

        Assert.Contains("Search:MaxEvidenceCharacters", ex.Message, StringComparison.Ordinal);
    }

    // ===================== On its own, without agents =====================

    /// <summary>Only the Search section: no Foundry settings at all.</summary>
    private static Dictionary<string, string?> SearchOnly() => new()
    {
        ["AiAgents:Search:Endpoint"] = "https://example.search.windows.net",
        ["AiAgents:Search:Indexes:0:Name"] = "ofsted_index",
    };

    private static ServiceProvider BuildSearchOnly(Dictionary<string, string?> settings, TokenCredential? credential = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAISearch(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), credential);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void OnItsOwn_AnAppCanSearch_WithNoFoundrySettingsAndNoAgents()
    {
        using var provider = BuildSearchOnly(SearchOnly(), Substitute.For<TokenCredential>());

        Assert.IsType<AzureSearchContextRetriever>(provider.GetRequiredService<IContextRetriever>());
        Assert.Null(provider.GetService<GovUK.Dfe.AI.Agents.Services.Interfaces.IAgentService>());
    }

    [Fact]
    public void OnItsOwn_ItCanSignInWithTheSectionsServicePrincipal()
    {
        var settings = SearchOnly();
        settings["AiAgents:Search:Authentication:TenantId"] = "tenant-1";
        settings["AiAgents:Search:Authentication:ClientId"] = "search-client";
        settings["AiAgents:Search:Authentication:ClientSecret"] = "search-secret";

        using var provider = BuildSearchOnly(settings);

        Assert.IsType<AzureSearchContextRetriever>(provider.GetRequiredService<IContextRetriever>());
    }

    [Theory]
    [InlineData(false, "AddAISearch needs a credential: pass one, or set AiAgents:Search:Authentication.")]
    [InlineData(true, "AddAISearch needs a credential: pass one, or set AiAgents:Search:Authentication:ClientSecret.")]
    public void OnItsOwn_WithoutACredential_FailsAtRegistration_NamingWhatToSet(bool partialBlock, string expected)
    {
        var settings = SearchOnly();
        if (partialBlock)
        {
            settings["AiAgents:Search:Authentication:TenantId"] = "tenant-1";
            settings["AiAgents:Search:Authentication:ClientId"] = "search-client";
        }

        var ex = Assert.Throws<InvalidOperationException>(() => BuildSearchOnly(settings));

        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void OnItsOwn_WithoutASearchSection_FailsAtRegistration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildSearchOnly([], Substitute.For<TokenCredential>()));

        Assert.Equal("AddAISearch needs the AiAgents:Search section (Endpoint and Indexes).", ex.Message);
    }

    [Fact]
    public void BothForms_RegisterSearchOnce()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build();
        services.AddAISearch(configuration, Substitute.For<TokenCredential>());
        services.AddAgents(configuration, agents => agents.AddAISearch().UseCredential(Substitute.For<TokenCredential>()));

        Assert.Single(services, service => service.ServiceType == typeof(IContextRetriever));
    }
}

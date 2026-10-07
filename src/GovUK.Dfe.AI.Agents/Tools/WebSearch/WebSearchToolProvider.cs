using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using OpenAI.Responses;

namespace GovUK.Dfe.AI.Agents.Tools.WebSearch;

/// <summary>
/// Gives an agent Foundry's web search, biased towards <paramref name="location"/> (default: London, England, GB). For a
/// different place each run, e.g. a school's town, use it with a temporary agent and <c>WebSearchLocation.ForCity</c>.
/// </summary>
public sealed class WebSearchToolProvider(WebSearchLocation? location = null) : IAgentToolProvider
{
    private readonly WebSearchLocation _location = location ?? WebSearchLocation.UnitedKingdom;

    public Task<IReadOnlyList<ResponseTool>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        var tool = ResponseTool.CreateWebSearchTool(userLocation: WebSearchToolLocation.CreateApproximateLocation(
            country: _location.Country, region: _location.Region, city: _location.City));

        return Task.FromResult<IReadOnlyList<ResponseTool>>([tool]);
    }
}

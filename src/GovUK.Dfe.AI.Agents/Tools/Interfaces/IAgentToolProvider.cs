using OpenAI.Responses;

namespace GovUK.Dfe.AI.Agents.Tools.Interfaces;

/// <summary>Supplies an agent's tools.</summary>
public interface IAgentToolProvider
{
    /// <summary>The tools.</summary>
    Task<IReadOnlyList<ResponseTool>> GetToolsAsync(CancellationToken cancellationToken = default);
}

using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// Source-generated log messages: arguments are only formatted when the level is enabled.
/// </summary>
internal static partial class AgentsLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Foundry agent {AgentName} was already deleted")]
    public static partial void AgentAlreadyDeleted(this ILogger logger, Exception exception, string agentName);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "Not pruning {AgentName}: this environment is pinned to version {Version}")]
    public static partial void NotPruningPinnedAgent(this ILogger logger, string agentName, string version);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Keeping version {Version} of {AgentName} because it's protected")]
    public static partial void KeepingProtectedVersion(this ILogger logger, string version, string agentName);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug, Message = "Skipping stale agent {AgentName} after a failed delete")]
    public static partial void SkippingStaleAgent(this ILogger logger, Exception exception, string agentName);

    /// <remarks>The names are joined with ", " only when the message is written.</remarks>
    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "Deleted {Count} stale Foundry agent(s): {AgentNames}")]
    public static partial void DeletedStaleAgents(this ILogger logger, int count, IEnumerable<string> agentNames);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information,
        Message = "Reusing version {Version} of {AgentName}, which matches this spec but isn't the latest")]
    public static partial void ReusingMatchingVersion(this ILogger logger, string version, string agentName);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug, Message = "Version {Version} of {AgentName} was already deleted")]
    public static partial void AgentVersionAlreadyDeleted(this ILogger logger, Exception exception, string version, string agentName);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Information,
        Message = "Skipping {AgentName}: ephemeral agents are created by the app that runs them")]
    public static partial void SkippingEphemeralAgent(this ILogger logger, string agentName);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Information, Message = "Skipping {AgentName}: it's managed outside this app")]
    public static partial void SkippingExternallyManagedAgent(this ILogger logger, string agentName);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information, Message = "Provisioned {AgentName} at version {Version}")]
    public static partial void ProvisionedAgent(this ILogger logger, string agentName, string? version);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Deleted {Count} orphaned ephemeral agent(s)")]
    public static partial void DeletedOrphanedEphemeralAgents(this ILogger logger, int count);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information,
        Message = "Skipping pinned version drift check for '{AgentName}'; it's managed outside this app.")]
    public static partial void SkippingPinnedDriftCheck(this ILogger logger, string agentName);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information, Message = "Created the run slot container {Container}")]
    public static partial void CreatedRunSlotContainer(this ILogger logger, Uri container);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Warning, Message = "Run observer {Observer} failed for a run of {AgentName}")]
    public static partial void RunObserverFailed(this ILogger logger, Exception exception, string observer, string agentName);
}

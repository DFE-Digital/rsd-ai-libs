namespace GovUK.Dfe.AI.Agents.Extensibility.Interfaces;

/// <summary>
/// An add-on package (e.g. <c>.Mcp</c>), added with <c>AgentsBuilder.AddPackage</c>. It registers its services once
/// <c>AddAgents</c> has read and checked the settings.
/// </summary>
/// <remarks>Part of the public extension point add-ons are built on, so it changes only in a major version.</remarks>
public interface IAgentsPackage
{
    /// <summary>
    /// The package's short name, e.g. "Mcp". Packages are added once per name, and the settings check matches it against
    /// the configuration sections that need a package.
    /// </summary>
    string Name { get; }

    /// <summary>Registers the package's services.</summary>
    void Register(AgentsPackageContext context);
}

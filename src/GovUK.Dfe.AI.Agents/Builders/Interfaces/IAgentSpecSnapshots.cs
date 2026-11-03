namespace GovUK.Dfe.AI.Agents.Builders.Interfaces;

/// <summary>
/// Holds each agent's spec still for a while, e.g. a release: what's tested, approved and provisioned is then the same
/// spec, even if a prompt file or a tool server's tools change meanwhile.
/// </summary>
public interface IAgentSpecSnapshots
{
    /// <summary>
    /// Until disposed, each agent's spec is built once in this async flow, and every run, test copy and provisioning
    /// reuses it. Inside an open snapshot, the outer one is used. A release gate's run opens one.
    /// </summary>
    IDisposable BeginSnapshot();
}

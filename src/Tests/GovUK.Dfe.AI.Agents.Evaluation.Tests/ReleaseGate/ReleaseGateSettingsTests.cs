using GovUK.Dfe.AI.Agents.Evaluation.Options;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary>The release gate's own settings.</summary>
public sealed class ReleaseGateSettingsTests
{
    [Theory]
    [InlineData(null, 60)]          // no RunTimeout: an hour
    [InlineData(2.0, 60)]           // a short RunTimeout: still an hour
    [InlineData(120.0, 122)]        // a RunTimeout over an hour: it, plus 2 minutes
    public void TheGatesCleanUp_LeavesAgentsTheLiveAppMayStillBeUsing(double? runTimeoutMinutes, double expectedMinutes)
    {
        var settings = new ReleaseGateSettings();

        settings.AllowForRunTimeout(runTimeoutMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), settings.LeftoverTestAgentAge);
    }
}

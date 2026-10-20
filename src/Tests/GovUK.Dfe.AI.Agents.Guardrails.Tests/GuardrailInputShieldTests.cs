using GovUK.Dfe.AI.Agents.Exceptions;
using GovUK.Dfe.AI.Agents.Guardrails.Options;
using GovUK.Dfe.AI.Agents.Guardrails.Services;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Guardrails.Tests;

/// <summary>The blocklists, checked in the app, for a model behind your own gateway where Foundry's don't run.</summary>
public sealed class GuardrailInputShieldTests
{
    private readonly CancellationToken _cancellationToken = TestContext.Current.CancellationToken;

    private static GuardrailInputShield Shield(bool checkBlocklists = true)
    {
        var settings = new GuardrailSettings { CheckBlocklists = checkBlocklists };
        settings.Blocklists["personal-identifiers"] = new GuardrailBlocklistSettings
        {
            Patterns = ["[A-Z][0-9]{11}[0-9A-Z]", "[A-CEGHJ-PR-TW-Z]{2}[0-9]{6}[A-D]"],
        };
        settings.Blocklists["code-names"] = new GuardrailBlocklistSettings { Terms = ["Project Falcon"] };
        return new GuardrailInputShield(settings, promptShields: null);
    }

    [Theory]
    [InlineData("The pupil with UPN A12345678901B asked how many pupils are on roll.", "personal-identifiers")]
    [InlineData("Is AB123456C on the staff list?", "personal-identifiers")]
    [InlineData("What is the status of project falcon?", "code-names")]   // terms ignore case
    public async Task BlocksAQuestion_OnABlocklist(string question, string blocklist)
    {
        var block = await Shield().CheckAsync(question, evidence: null, _cancellationToken);

        Assert.Equal(new(AgentGuardrailException.PromptStage, $"The question matched the blocklist '{blocklist}'"), block);
    }

    [Fact]
    public async Task BlocksEvidence_OnABlocklist()
    {
        var block = await Shield().CheckAsync("Summarise the record.", "pupil_upn: A12345678901B", _cancellationToken);

        Assert.Equal(new(AgentGuardrailException.EvidenceStage, "The evidence matched the blocklist 'personal-identifiers'"), block);
    }

    [Fact]
    public async Task LetsOrdinaryInputThrough()
    {
        var block = await Shield().CheckAsync("How many pupils are on roll at URN 999101?",
            "establishment_urn: 999101\nnumber_on_roll: 398\nmp_name: Alex Example", _cancellationToken);

        Assert.Null(block);
    }

    [Fact]
    public async Task ChecksNoBlocklist_WhenCheckBlocklistsIsOff()
    {
        var block = await Shield(checkBlocklists: false).CheckAsync("UPN A12345678901B", evidence: null, _cancellationToken);

        Assert.Null(block);
    }
}

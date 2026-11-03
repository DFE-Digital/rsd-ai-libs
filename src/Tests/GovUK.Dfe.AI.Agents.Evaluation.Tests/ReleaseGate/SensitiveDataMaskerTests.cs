using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Privacy;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary>What the release gate keeps or shows never carries sensitive data.</summary>
public sealed class SensitiveDataMaskerTests
{
    private static readonly string[] PlantedNames = ["Jamie Example"];

    [Fact]
    public async Task Masks_planted_values_blocklisted_terms_and_what_the_apps_redactors_remove()
    {
        var blocklists = new GuardrailBlocklists(new Dictionary<string, string> { ["code-names term 1"] = @"(?i)Project\ Falcon" });
        var redactor = new PatternRedactor(new Dictionary<string, string> { ["UPN"] = @"\b[A-Z]\d{11}[0-9A-Z]\b" });
        var masker = new SensitiveDataMasker(blocklists, [redactor]);

        var masked = await masker.MaskAsync("jamie example, pupil A123456789012, is on project falcon.", PlantedNames,
            TestContext.Current.CancellationToken);

        Assert.Equal("[sensitive value removed], pupil [UPN removed], is on [code-names term 1 removed].", masked);
    }
}

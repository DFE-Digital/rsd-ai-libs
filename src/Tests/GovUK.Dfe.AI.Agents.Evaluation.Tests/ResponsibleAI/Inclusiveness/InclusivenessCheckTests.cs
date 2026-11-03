using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Inclusiveness;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using Xunit;
using static GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.QualityGateTestData;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.Inclusiveness;

/// <summary>
/// Inclusiveness: answers readable and accessible to users with different needs, and the agent tested for every user
/// group it must serve.
/// </summary>
public sealed class InclusivenessCheckTests
{
    private const string PlainAnswer = "# Summary\n\nThe school is good. It has 300 pupils. Ofsted visited in May [Evidence 1].";

    private const string DenseAnswer =
        "The institutional configuration demonstrates considerable organisational heterogeneity, notwithstanding comprehensive " +
        "administrative rationalisation initiatives implemented throughout successive accountability evaluation periodicities, " +
        "consequently necessitating additional interdepartmental collaborative intervention methodologies.";

    [Fact]
    public void Measures_plain_short_sentences_as_easy_to_read_and_ignores_markdown_and_citations()
    {
        var (ease, wordsPerSentence, words) = ReadabilityCheck.Measure(PlainAnswer);

        Assert.True(ease > 70, $"Reading ease was {ease}");
        Assert.Equal(12, words);
        Assert.True(wordsPerSentence < 5);
    }

    [Theory]
    [InlineData("cat", 1)]
    [InlineData("school", 1)]
    [InlineData("table", 2)]
    [InlineData("organisation", 5)]
    public void Estimates_syllables_from_vowel_groups(string word, int syllables) =>
        Assert.Equal(syllables, ReadabilityCheck.Syllables(word));

    [Fact]
    public async Task Advises_when_answers_are_hard_to_read()
    {
        var result = await OnlyAsync(new ReadabilityCheck(), CheckContext([Passing("a", output: DenseAnswer)]));

        Assert.Equal(CheckOutcome.Failed, result.Outcome);
        Assert.Equal(CheckSeverity.Low, result.Severity);
        Assert.Equal(EvaluationMethod.Accessibility, result.Method);
        Assert.StartsWith("Answers average a reading ease of 0, below 30", result.Summary, StringComparison.Ordinal);
        Assert.Contains(result.Evidence, e => e.StartsWith("Hardest: a reading ease 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fails_when_sentences_are_longer_than_the_policy_allows()
    {
        var result = await OnlyAsync(new ReadabilityCheck(), CheckContext([Passing("a", output: PlainAnswer)],
            new QualityGatePolicy { MaxAverageSentenceWords = 2 }));

        Assert.StartsWith("Sentences average", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passes_readable_answers()
    {
        Assert.Equal(CheckOutcome.Passed, (await OnlyAsync(new ReadabilityCheck(), CheckContext([Passing("a", output: PlainAnswer)]))).Outcome);
    }

    [Fact]
    public void Finds_skipped_heading_levels_empty_headings_vague_links_and_images_without_alt_text()
    {
        var problems = AccessibleFormattingCheck.Problems("# Briefing\n### Detail\n##\n[click here](https://x.gov.uk) ![](chart.png)");

        Assert.Equal(
        [
            "Heading \"Detail\" skips from level 1 to 3",
            "An empty heading",
            "Link text \"click here\" doesn't say where it goes",
            "An image without alternative text",
        ], problems);
    }

    [Fact]
    public async Task Warns_per_answer_with_formatting_assistive_technology_cannot_follow()
    {
        var results = await RunAsync(new AccessibleFormattingCheck(),
            CheckContext([Passing("good", output: PlainAnswer), Passing("bad", output: "# A\n### B")]));

        var result = Assert.Single(results);
        Assert.Equal(CheckOutcome.Failed, result.Outcome);
        Assert.Equal("bad", result.Subject);
    }

    [Fact]
    public async Task Fails_when_a_required_user_group_has_too_few_cases()
    {
        var policy = new QualityGatePolicy { RequiredGroups = ["special", "welsh-medium"], MinimumCasesPerGroup = 2 };

        var result = await OnlyAsync(new UserGroupCoverageCheck(),
            CheckContext([Passing("s1", "special"), Passing("s2", "Special"), Passing("w1", "welsh-medium")], policy));

        Assert.Equal(CheckOutcome.Failed, result.Outcome);
        Assert.Equal("Too few cases for welsh-medium (at least 2 each)", result.Summary);
    }

    [Fact]
    public async Task Does_not_apply_when_the_policy_names_no_required_groups()
    {
        Assert.Equal(CheckOutcome.NotApplicable, (await OnlyAsync(new UserGroupCoverageCheck(), CheckContext([Passing("a")]))).Outcome);
    }
}

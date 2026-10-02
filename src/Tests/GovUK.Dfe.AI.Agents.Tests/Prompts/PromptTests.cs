using GovUK.Dfe.AI.Agents.Prompts;
using GovUK.Dfe.AI.Agents.Prompts.Interfaces;
using GovUK.Dfe.AI.Agents.Tests.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Prompts;

/// <summary>Prompt files: read relative to the app, configured by key, and filled with <c>{{Name}}</c> placeholders.</summary>
public sealed class PromptTests : IDisposable
{
    private readonly string _relativePath = $"{Guid.NewGuid():N}.md";
    private readonly string _fullPath;

    public PromptTests() => _fullPath = Path.Combine(AppContext.BaseDirectory, _relativePath);

    public void Dispose()
    {
        if (File.Exists(_fullPath))
        {
            File.Delete(_fullPath);
        }
    }

    private static FileSystemPromptFileReader Reader() => new(NullLogger<FileSystemPromptFileReader>.Instance);

    [Fact]
    public void Read_ReturnsFileContent_RelativeToTheAppBaseDirectory()
    {
        File.WriteAllText(_fullPath, "Real prompt content.");

        Assert.Equal("Real prompt content.", Reader().Read(_relativePath));
    }

    [Fact]
    public void Read_Throws_WhenTheFileDoesNotExist()
        => Assert.Throws<FileNotFoundException>(() => Reader().Read(_relativePath));

    [Fact]
    public void Read_Throws_WhenTheFileIsEmpty()
    {
        File.WriteAllText(_fullPath, "   ");

        Assert.Throws<InvalidOperationException>(() => Reader().Read(_relativePath));
    }

    [Fact]
    public void GetTemplate_Throws_WhenKeyNotConfigured()
    {
        var sut = new FilePromptTemplateStore(
            paths: new Dictionary<string, string>(),
            readFile: _ => throw new InvalidOperationException(TestErrorMessages.ShouldNotBeCalled));

        var ex = Assert.Throws<InvalidOperationException>(() => sut.GetTemplate("greeting"));
        Assert.Contains("greeting", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Academy: {{Name}}\nEvidence: {{Context}}", "Test Academy", "Academy: Test Academy\nEvidence: some evidence")]   // known tokens replaced
    [InlineData("Academy: {{Name}}, Other: {{NotSupplied}}", "Test Academy", "Academy: Test Academy, Other: {{NotSupplied}}")]   // unknown left alone
    [InlineData("Value: [{{Name}}]", null, "Value: []")]                                                                    // null becomes empty
    public void Build_FillsPlaceholders(string template, string? name, string expected)
    {
        var store = Substitute.For<IPromptTemplateStore>();
        store.GetTemplate("template").Returns(template);

        var result = new PromptTemplateBuilder(store).Build("template", new Dictionary<string, string>
        {
            ["Name"] = name!,
            ["Context"] = "some evidence",
        });

        Assert.Equal(expected, result);
    }
}

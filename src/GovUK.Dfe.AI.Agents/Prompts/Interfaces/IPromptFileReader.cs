namespace GovUK.Dfe.AI.Agents.Prompts.Interfaces;

/// <summary>Reads prompt files.</summary>
public interface IPromptFileReader
{
    /// <summary>The text of the prompt file at <paramref name="path"/>.</summary>
    string Read(string path);
}

using GovUK.Dfe.AI.Agents.Constants;
using GovUK.Dfe.AI.Agents.Prompts.Interfaces;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Prompts;

/// <summary>Reads a prompt file relative to the app's base directory; a missing or empty file throws.</summary>
internal sealed class FileSystemPromptFileReader(ILogger<FileSystemPromptFileReader> logger) : IPromptFileReader
{
    public string Read(string path)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, path);

        if (!File.Exists(fullPath))
        {
            logger.LogError("Prompt file not found: {FullPath}", fullPath);
            throw new FileNotFoundException(ErrorMessages.PromptFileNotFound, fullPath);
        }

        var content = File.ReadAllText(fullPath);

        if (string.IsNullOrWhiteSpace(content))
        {
            logger.LogError("Prompt file is empty: {FullPath}", fullPath);
            throw new InvalidOperationException(string.Format(ErrorMessages.PromptFileEmpty, fullPath));
        }

        return content;
    }
}

namespace GovUK.Dfe.AI.Agents.Privacy.Interfaces;

/// <summary>
/// Removes personal data from text before it's sent to a model: every prompt, evidence and tool output, and the copies
/// given to evaluators and run observers. Add one with <c>agents.AddRedactor(...)</c>; several run in the order added.
/// </summary>
public interface IAgentInputRedactor
{
    /// <summary>The text with anything sensitive replaced.</summary>
    string Redact(string text);
}

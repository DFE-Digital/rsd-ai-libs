namespace GovUK.Dfe.AI.Agents.Evaluation.Exceptions;

/// <summary>Thrown when the release gate can't run because a file it reads, such as a baseline, is invalid.</summary>
public sealed class ReleaseGateException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

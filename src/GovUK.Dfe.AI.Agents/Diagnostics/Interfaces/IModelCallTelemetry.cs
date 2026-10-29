using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;

/// <summary>
/// Records a model call made without an agent, e.g. an evaluation judge called on its deployment, in the same span,
/// token, cost and duration telemetry as an agent run, so every call the app is billed for shows up. Registered by
/// <c>AddAgents</c>.
/// </summary>
public interface IModelCallTelemetry
{
    /// <summary>Starts one call, reported under <paramref name="name"/> as an agent's runs are. Dispose it when the call ends.</summary>
    IModelCall Start(string name);
}

/// <summary>One model call: set its outcome, then dispose it to record it, whatever the outcome.</summary>
public interface IModelCall : IDisposable
{
    /// <summary>The call answered: the model that answered and the tokens it was billed for.</summary>
    void Completed(string? model, TokenUsage usage);

    /// <summary>The call failed or was cancelled. Only the exception's type reaches telemetry, never its message.</summary>
    void Failed(Exception exception);
}

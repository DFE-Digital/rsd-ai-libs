namespace GovUK.Dfe.AI.Agents.Resilience;
/// <summary>Runs a step, returning a fallback result when it fails.</summary>
internal static class ResilientAgentStep
{
    /// <summary>
    /// Runs <paramref name="step"/>, or returns <paramref name="fallback"/>'s result for a failure <paramref name="shouldSuppress"/> accepts.
    /// </summary>
    public static async Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> step, Func<Exception, TResult> fallback, Func<Exception, bool>? shouldSuppress = null)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException &&
            (shouldSuppress is null || shouldSuppress(ex)))
        {
            return fallback(ex);
        }
    }
}
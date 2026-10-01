using GovUK.Dfe.AI.Agents.Guardrails.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.ValueObjects;
using GovUK.Dfe.AI.Agents.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Guardrails.Validators;

/// <summary>
/// At startup, checks every deployment in <c>Deployments</c> carries the guardrail. A missing or weaker guardrail fails
/// startup (or warns, with <c>RequireAtStartup: false</c>); an unreachable Resource Manager only warns.
/// </summary>
internal sealed class GuardrailStartupValidator(IFoundryGuardrailsService guardrails, AgentsOptions.GuardrailSettings settings,
    ILogger<GuardrailStartupValidator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        GuardrailReport report;
        try
        {
            report = await guardrails.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't check the Foundry guardrail {Guardrail} at startup", settings.Name);
            return;
        }

        if (report.Passed)
        {
            return;
        }

        var message = string.Format(Constants.ErrorMessages.GuardrailNotApplied, report.Guardrail, string.Join("; ", report.Problems));
        if (settings.RequireAtStartup)
        {
            throw new InvalidOperationException(message);
        }

        logger.LogWarning("{Message}", message);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

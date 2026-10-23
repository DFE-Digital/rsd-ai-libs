using GovUK.Dfe.AI.Agents.Guardrails.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.ValueObjects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using GovUK.Dfe.AI.Agents.Guardrails.Options;

namespace GovUK.Dfe.AI.Agents.Guardrails.Validators;

/// <summary>
/// At startup, checks every deployment in <c>Deployments</c>, or agent version in use, carries the guardrail. A missing or weaker guardrail fails
/// startup (or warns, with <c>RequireAtStartup: false</c>); an unreachable Resource Manager warns, or fails startup with
/// <c>FailWhenUnreachable: true</c>.
/// </summary>
internal sealed class GuardrailStartupValidator(IFoundryGuardrailsService guardrails, GuardrailSettings settings,
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
            if (settings.FailWhenUnreachable)
            {
                throw new InvalidOperationException(string.Format(Constants.ErrorMessages.GuardrailUncheckable, settings.Name), ex);
            }

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

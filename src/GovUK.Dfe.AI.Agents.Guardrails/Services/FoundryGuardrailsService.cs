using GovUK.Dfe.AI.Agents.Guardrails.Constants;
using Azure;
using GovUK.Dfe.AI.Agents.Guardrails.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Policies;
using GovUK.Dfe.AI.Agents.Guardrails.Stores.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.ValueObjects;
using GovUK.Dfe.AI.Agents.Options;
using Microsoft.Extensions.Logging;
using System.Net;
using GovUK.Dfe.AI.Agents.Guardrails.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Guardrails.Services;

internal sealed class FoundryGuardrailsService(IGuardrailStore store, AgentsOptions.GuardrailSettings settings, ILogger<FoundryGuardrailsService> logger)
    : IFoundryGuardrailsService
{
    public async Task<GuardrailReport> ApplyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Blocklists first: the guardrail refers to them.
            foreach (var (name, blocklist) in settings.Blocklists)
            {
                await store.SaveBlocklistAsync(name, GuardrailPolicy.EntriesOf(blocklist), cancellationToken).ConfigureAwait(false);
            }

            var required = GuardrailPolicy.From(settings);
            await store.SaveGuardrailAsync(required, cancellationToken).ConfigureAwait(false);

            foreach (var deployment in settings.Deployments)
            {
                var (exists, current) = await store.GetDeploymentAsync(deployment, cancellationToken).ConfigureAwait(false);
                if (exists && current != required.Name)
                {
                    await store.AssignAsync(deployment, required.Name, cancellationToken).ConfigureAwait(false);
                    logger.AssignedGuardrail(deployment, required.Name, current ?? "none");
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Forbidden)
        {
            throw AccessDenied("Cognitive Services Contributor", ex);
        }

        return await CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GuardrailReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var required = GuardrailPolicy.From(settings);
        var problems = new List<string>();
        try
        {
            if (await store.GetGuardrailAsync(required.Name, cancellationToken).ConfigureAwait(false) is not { } actual)
            {
                problems.Add($"the guardrail '{required.Name}' doesn't exist");
            }
            else
            {
                problems.AddRange(actual.WeakerThan(required).Select(problem => $"'{required.Name}': {problem}"));
            }

            // Counts only: blocklist entries can be sensitive, so they never appear in reports or logs.
            foreach (var (name, blocklist) in settings.Blocklists)
            {
                var entries = await store.GetBlocklistAsync(name, cancellationToken).ConfigureAwait(false);
                var missing = entries is null ? -1 : GuardrailPolicy.EntriesOf(blocklist).Count(entry => !entries.Contains(entry));
                if (missing != 0)
                {
                    problems.Add(missing < 0 ? $"blocklist '{name}' doesn't exist" : $"blocklist '{name}' is missing {missing} of its entries");
                }
            }

            foreach (var deployment in settings.Deployments)
            {
                var (exists, current) = await store.GetDeploymentAsync(deployment, cancellationToken).ConfigureAwait(false);
                if (!exists)
                {
                    problems.Add($"deployment '{deployment}' doesn't exist");
                }
                else if (current != required.Name)
                {
                    problems.Add($"deployment '{deployment}' uses {(current is null ? "no guardrail" : $"'{current}'")}");
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Forbidden)
        {
            throw AccessDenied("Reader", ex);
        }

        return new GuardrailReport(required.Name, problems);
    }

    private InvalidOperationException AccessDenied(string role, RequestFailedException ex)
        => new(string.Format(Constants.ErrorMessages.GuardrailAccessDenied, role, settings.AccountResourceId), ex);
}

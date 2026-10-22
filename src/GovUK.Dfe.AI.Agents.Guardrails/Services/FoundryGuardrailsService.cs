using Azure;
using GovUK.Dfe.AI.Agents.Guardrails.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Policies;
using GovUK.Dfe.AI.Agents.Guardrails.Stores.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Net;
using GovUK.Dfe.AI.Agents.Guardrails.Diagnostics;
using GovUK.Dfe.AI.Agents.Guardrails.Options;
using GovUK.Dfe.AI.Agents.Guardrails.Enums;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;

namespace GovUK.Dfe.AI.Agents.Guardrails.Services;

internal sealed class FoundryGuardrailsService(IGuardrailStore store, GuardrailSettings settings, ILogger<FoundryGuardrailsService> logger,
    IAgentFactory? agentFactory = null) : IFoundryGuardrailsService
{
    private bool OnDeployments => settings.Targets.HasFlag(GuardrailTargets.Deployments);

    private bool OnAgents => settings.Targets.HasFlag(GuardrailTargets.Agents);

    /// <remarks>
    /// Reads first and writes only what differs, so when the guardrail is already in place a job needs only Reader.
    /// </remarks>
    public async Task<GuardrailReport> ApplyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Blocklists first: the guardrail refers to them.
            await SaveBlocklistsAsync(cancellationToken).ConfigureAwait(false);
            var required = await SaveGuardrailAsync(cancellationToken).ConfigureAwait(false);

            // Agent versions can't be changed: this app's agents get the guardrail in each new version they create.
            if (OnDeployments)
            {
                await AssignToDeploymentsAsync(required.Name, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Forbidden)
        {
            throw AccessDenied("Cognitive Services Contributor", ex);
        }

        return await CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveBlocklistsAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, blocklist) in settings.Blocklists)
        {
            var wanted = GuardrailPolicy.EntriesOf(blocklist);
            if (await store.GetBlocklistAsync(name, cancellationToken).ConfigureAwait(false) is not { } current || !current.SetEquals(wanted))
            {
                await store.SaveBlocklistAsync(name, wanted, cancellationToken).ConfigureAwait(false);
                logger.SavedGuardrailPart("blocklist", name);
            }
        }
    }

    private async Task<GuardrailPolicy> SaveGuardrailAsync(CancellationToken cancellationToken)
    {
        var required = GuardrailPolicy.From(settings);
        if (await store.GetGuardrailAsync(required.Name, cancellationToken).ConfigureAwait(false) is not { } actual || !actual.Matches(required))
        {
            await store.SaveGuardrailAsync(required, cancellationToken).ConfigureAwait(false);
            logger.SavedGuardrailPart("guardrail", required.Name);
        }

        return required;
    }

    private async Task AssignToDeploymentsAsync(string guardrail, CancellationToken cancellationToken)
    {
        foreach (var deployment in settings.Deployments)
        {
            var (exists, current) = await store.GetDeploymentAsync(deployment, cancellationToken).ConfigureAwait(false);
            if (exists && current != guardrail)
            {
                await store.AssignAsync(deployment, guardrail, cancellationToken).ConfigureAwait(false);
                logger.AssignedGuardrail(deployment, guardrail, current ?? "none");
            }
        }
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

            if (OnDeployments)
            {
                problems.AddRange(await DeploymentProblemsAsync(required.Name, cancellationToken).ConfigureAwait(false));
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Forbidden)
        {
            throw AccessDenied("Reader", ex);
        }

        if (OnAgents)
        {
            problems.AddRange(await AgentProblemsAsync(cancellationToken).ConfigureAwait(false));
        }

        return new GuardrailReport(required.Name, problems);
    }

    private async Task<List<string>> DeploymentProblemsAsync(string guardrail, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var deployment in settings.Deployments)
        {
            var (exists, current) = await store.GetDeploymentAsync(deployment, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                problems.Add($"deployment '{deployment}' doesn't exist");
            }
            else if (current != guardrail)
            {
                problems.Add($"deployment '{deployment}' uses {(current is null ? "no guardrail" : $"'{current}'")}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Pinned and externally managed versions run as they are, so each must already carry its guardrail. Agents this app
    /// creates aren't checked: a version without the guardrail is never reused.
    /// </summary>
    private async Task<List<string>> AgentProblemsAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        foreach (var agent in settings.Agents)
        {
            var version = agent.Version is null ? "latest version" : $"version {agent.Version}";
            string? current;
            try
            {
                current = await agentFactory!.GetGuardrailAsync(agent.Name, agent.Version, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                problems.Add($"agent '{agent.Name}' {version} doesn't exist");
                continue;
            }

            if (!GuardrailSettings.SameGuardrail(current, agent.Guardrail))
            {
                problems.Add($"agent '{agent.Name}' {version} uses {(current is null ? "no guardrail" : $"'{current}'")}");
            }
        }

        return problems;
    }

    private InvalidOperationException AccessDenied(string role, RequestFailedException ex)
        => new(string.Format(Constants.ErrorMessages.GuardrailAccessDenied, role, settings.ResourceId), ex);
}

using Azure.Core;
using Azure.ResourceManager;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Guardrails.Enums;
using GovUK.Dfe.AI.Agents.Guardrails.Options;
using GovUK.Dfe.AI.Agents.Guardrails.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Services;
using GovUK.Dfe.AI.Agents.Guardrails.Stores;
using GovUK.Dfe.AI.Agents.Guardrails.Stores.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Validators;
using GovUK.Dfe.AI.Agents.Extensibility;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using Microsoft.Extensions.Configuration;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds Foundry guardrails to <c>AddAgents</c>.</summary>
public static class GuardrailsExtensions
{
    private const string SectionName = "Guardrails";

    /// <summary>
    /// Checks at startup that the model deployments, or agent versions, under <c>AiAgents:Guardrails</c> carry its Foundry
    /// guardrail, and registers <see cref="IFoundryGuardrailsService"/> so a provisioning job can apply it. With
    /// <c>Targets</c> including <c>Agents</c>, every agent version this app creates gets the guardrail.
    /// </summary>
    public static AgentsBuilder AddGuardrails(this AgentsBuilder agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.AddPackage(new GuardrailsPackage());
    }

    /// <summary>A credential for Azure Resource Manager, where guardrails are read and applied. Overrides <c>Guardrails:Authentication</c> and the default.</summary>
    public static AgentsBuilder UseGuardrailsCredential(this AgentsBuilder agents, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.UseCredentialFor(SectionName, credential);
    }

    private sealed class GuardrailsPackage : IAgentsPackage
    {
        public string Name => "Guardrails";

        public void Register(AgentsPackageContext context)
        {
            var section = context.Section.GetSection(SectionName);
            if (!section.Exists())
            {
                context.ReportProblem($"{SectionName} (agents.AddGuardrails() needs this section)");
                return;
            }

            var settings = section.Get<GuardrailSettings>() ?? new GuardrailSettings();
            var problems = settings.Problems(context.Options.Foundry.Endpoint).ToList();
            problems.ForEach(problem => context.ReportProblem($"{SectionName}:{problem}"));
            var credential = context.CredentialFor(SectionName, settings.Authentication, $"{SectionName}:Authentication");
            if (problems.Count > 0)
            {
                return;
            }

            settings.ResolveResourceId(context.Options.Foundry.Endpoint);
            if (settings.Targets.HasFlag(GuardrailTargets.Agents) && !UseOnAgents(context, settings))
            {
                return;
            }

            var services = context.Services;
            services.AddSingleton(settings);
            services.AddSingleton<IGuardrailStore>(_ => new ArmGuardrailStore(new ArmClient(credential), new ResourceIdentifier(settings.ResourceId!)));
            services.AddSingleton<IFoundryGuardrailsService, FoundryGuardrailsService>();
            services.AddHostedService<GuardrailStartupValidator>();
        }

        /// <summary>
        /// Makes the guardrail every new agent version's default (<c>Foundry:Guardrail</c>), and lists the versions this app
        /// runs as they are, to check: pinned ones, and externally managed ones in this project.
        /// </summary>
        /// <remarks>
        /// Agents in another project are left to that project's own check: a guardrail belongs to one Foundry resource.
        /// </remarks>
        private static bool UseOnAgents(AgentsPackageContext context, GuardrailSettings settings)
        {
            var foundry = context.Options.Foundry;
            if (!string.IsNullOrWhiteSpace(foundry.Guardrail) && foundry.Guardrail != settings.Name)
            {
                context.ReportProblem($"{SectionName}:Name (Foundry:Guardrail names a different guardrail; remove one)");
                return false;
            }

            foundry.Guardrail = settings.Name;
            var external = context.Options.ExternallyManagedAgents;
            var versions = context.Options.VersionPins.Select(static pin => (pin.Key, (string?)pin.Value))
                .Concat(external.Endpoint is null ? external.Agents.Select(static agent => (agent.Key, FollowsLatest(agent.Value) ? null : (string?)agent.Value)) : []);
            var guardrails = context.Definitions.ToDictionary(static definition => definition.Name, static definition => definition.Guardrail);
            settings.Agents = [.. versions.Select(agent => new AgentInUse(agent.Key, agent.Item2,
                guardrails.GetValueOrDefault(agent.Key) ?? settings.Name!))];
            return true;
        }

        private static bool FollowsLatest(string version)
            => string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase);
    }
}

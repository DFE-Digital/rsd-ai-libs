using Azure.Core;
using Azure.ResourceManager;
using GovUK.Dfe.AI.Agents.Builders;
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
    /// Checks at startup that the model deployments under <c>AiAgents:Guardrails</c> carry its Foundry guardrail, and
    /// registers <see cref="IFoundryGuardrailsService"/> so a provisioning job can apply it.
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
            var problems = settings.Problems().ToList();
            problems.ForEach(problem => context.ReportProblem($"{SectionName}:{problem}"));
            var credential = context.CredentialFor(SectionName, settings.Authentication, $"{SectionName}:Authentication");
            if (problems.Count > 0)
            {
                return;
            }

            var services = context.Services;
            services.AddSingleton(settings);
            services.AddSingleton<IGuardrailStore>(_ => new ArmGuardrailStore(new ArmClient(credential), new ResourceIdentifier(settings.AccountResourceId!)));
            services.AddSingleton<IFoundryGuardrailsService, FoundryGuardrailsService>();
            services.AddHostedService<GuardrailStartupValidator>();
        }
    }
}

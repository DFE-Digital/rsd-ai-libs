using Azure.Core;
using Azure.ResourceManager;
using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.Guardrails.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Services;
using GovUK.Dfe.AI.Agents.Guardrails.Stores;
using GovUK.Dfe.AI.Agents.Guardrails.Stores.Interfaces;
using GovUK.Dfe.AI.Agents.Guardrails.Validators;
using GovUK.Dfe.AI.Agents.Packages;
using GovUK.Dfe.AI.Agents.Packages.Interfaces;

// In Microsoft.Extensions.DependencyInjection, as Microsoft recommends for libraries' Add... methods, so apps can
// call them without extra usings.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds Foundry guardrails to <c>AddAgents</c>.</summary>
public static class GuardrailsExtensions
{
    /// <summary>
    /// Checks at startup that the model deployments under <c>AiAgents:Guardrails</c> carry its Foundry guardrail, and
    /// registers <see cref="IFoundryGuardrailsService"/> so a provisioning job can apply it.
    /// </summary>
    public static AgentsBuilder AddGuardrails(this AgentsBuilder agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return agents.AddPackage(new GuardrailsPackage());
    }

    private sealed class GuardrailsPackage : IAgentsPackage
    {
        public string Name => "Guardrails";

        public void Register(AgentsRegistrationPackage registrationPackage)
        {
            var (services, _, options, _) = registrationPackage;
            var settings = options.Guardrails!;
            var credential = options.CredentialFor(nameof(AzureCredentialTarget.Guardrails), settings.Authentication);

            services.AddSingleton(settings);
            services.AddSingleton<IGuardrailStore>(_ => new ArmGuardrailStore(new ArmClient(credential), new ResourceIdentifier(settings.AccountResourceId!)));
            services.AddSingleton<IFoundryGuardrailsService, FoundryGuardrailsService>();
            services.AddHostedService<GuardrailStartupValidator>();
        }
    }
}

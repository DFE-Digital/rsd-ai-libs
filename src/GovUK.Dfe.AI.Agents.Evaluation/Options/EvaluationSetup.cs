using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace GovUK.Dfe.AI.Agents.Evaluation.Options;

/// <summary>
/// What each of the app's evaluation calls asked for, kept on the service collection until the packages register. Core
/// adds a package once, keeping the first, so a later call's configuration would otherwise be lost. Recorded here, every
/// call counts, whatever its order, like <c>services.Configure&lt;T&gt;</c>.
/// </summary>
internal sealed class EvaluationSetup
{
    private readonly List<JudgeChoice> _judges = [];

    /// <summary>Every <c>AddReleaseGate(configure)</c> delegate, applied in the order called.</summary>
    public List<Action<ReleaseGateSettings>> ReleaseGateChanges { get; } = [];

    /// <summary>The quality gate asked for the default judge, used unless the app chose one itself.</summary>
    public bool DefaultJudgeRequested { get; set; }

    /// <summary>The app's setup, created on first use.</summary>
    public static EvaluationSetup For(IServiceCollection services)
    {
        if (services.FirstOrDefault(static service => service.ServiceType == typeof(EvaluationSetup))?.ImplementationInstance is EvaluationSetup setup)
        {
            return setup;
        }

        setup = new EvaluationSetup();
        services.AddSingleton(setup);
        return setup;
    }

    /// <summary>Records a judge the app chose; choosing the same one again is harmless.</summary>
    public void Choose(JudgeChoice judge)
    {
        if (!_judges.Exists(chosen => ReferenceEquals(chosen.Identity, judge.Identity)))
        {
            _judges.Add(judge);
        }
    }

    /// <summary>The judge to register: the app's own choice, else <paramref name="defaultJudge"/> if asked for, else none.</summary>
    /// <exception cref="InvalidOperationException">The app chose two different judges, so neither is assumed.</exception>
    public JudgeChoice? Judge(JudgeChoice defaultJudge) => _judges.Count switch
    {
        0 => DefaultJudgeRequested ? defaultJudge : null,
        1 => _judges[0].IsNone ? null : _judges[0],
        _ => throw new InvalidOperationException(
            $"Two judges were set up for agent evaluation: {string.Join(" and ", _judges.Select(static judge => judge.Description))}. Choose one."),
    };
}

/// <summary>A judge the app chose: how to create it, and what to call it in an error.</summary>
/// <param name="Identity">The same object for the same choice, e.g. the evaluator instance, so repeating a call isn't a conflict.</param>
/// <param name="EvaluatorFor">Creates the evaluator, given the judge model and whether it runs as an agent; null for no judge.</param>
/// <param name="NeedsJudgeModel">Whether it calls the judge model, so the model's settings are checked.</param>
internal sealed record JudgeChoice(string Description, object Identity,
    Func<string?, bool, Func<IServiceProvider, IAgentRunEvaluator>>? EvaluatorFor, bool NeedsJudgeModel)
{
    private static readonly object NoneIdentity = new();

    public static JudgeChoice None { get; } = new("WithoutJudge()", NoneIdentity, null, NeedsJudgeModel: false);

    public bool IsNone => EvaluatorFor is null;
}

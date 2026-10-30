using GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary><see cref="IModelCallTelemetry"/> as an agent run of one model call, priced like one.</summary>
internal sealed class ModelCallTelemetry(AgentRunOptions runOptions) : IModelCallTelemetry
{
    public IModelCall Start(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Call(runOptions, new AgentRunTelemetry(runOptions.ApplicationName, new AgentReference(name, name)));
    }

    private sealed class Call(AgentRunOptions runOptions, AgentRunTelemetry telemetry) : IModelCall
    {
        private TokenUsage _usage = TokenUsage.None;
        private string? _model;
        private bool _recorded;

        void IModelCall.Completed(string? model, TokenUsage usage)
        {
            ArgumentNullException.ThrowIfNull(usage);
            _model = model;
            _usage = usage;
            telemetry.AnsweredBy(model);
        }

        void IModelCall.Failed(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (exception is OperationCanceledException)
            {
                telemetry.Cancelled();
            }
            else
            {
                telemetry.Failed(exception.GetType().FullName ?? AgentTelemetry.OtherError, "The model call failed.");
            }
        }

        void IDisposable.Dispose()
        {
            if (_recorded)
            {
                return;
            }

            _recorded = true;
            telemetry.Record(_usage, inferenceCalls: 1, toolCalls: 0, runOptions.Pricing.CostOf(_model, _usage), runOptions.Pricing.Currency);
            telemetry.Dispose();
        }
    }
}

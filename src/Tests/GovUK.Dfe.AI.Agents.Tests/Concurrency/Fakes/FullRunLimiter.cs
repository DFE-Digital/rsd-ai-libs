using GovUK.Dfe.AI.Agents.Concurrency.Interfaces;
using NSubstitute;

namespace GovUK.Dfe.AI.Agents.Tests.Concurrency.Fakes;

/// <summary>A run limiter with every slot taken until <see cref="FreeASlot"/>.</summary>
internal sealed class FullRunLimiter : IAgentRunLimiter
{
    private readonly TaskCompletionSource<IAsyncDisposable> _slotFree = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void FreeASlot() => _slotFree.SetResult(Substitute.For<IAsyncDisposable>());

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        => await _slotFree.Task.WaitAsync(cancellationToken);
}

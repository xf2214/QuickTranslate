using QuickTranslate.Infrastructure.Ocr;
using Xunit;

namespace QuickTranslate.Tests.Infrastructure;

public class OcrLifecycleGateTests
{
    [Fact]
    public async Task EnterAsync_SerializesInferenceAndRebuild()
    {
        var gate = new OcrOperationGate();
        var inference = await gate.EnterAsync();
        var rebuild = gate.EnterAsync().AsTask();
        Assert.False(rebuild.IsCompleted);

        inference.Dispose();
        using var rebuilding = await rebuild.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotReleaseActiveLeaseOrConsumeNextLease()
    {
        var gate = new OcrOperationGate();
        var active = await gate.EnterAsync();
        using var cts = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);

        var next = gate.EnterAsync().AsTask();
        Assert.False(next.IsCompleted);
        active.Dispose();
        using var nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StopAsync_DefersCleanupUntilActiveWorkEndsWithoutBlockingCaller()
    {
        var gate = new OcrOperationGate();
        var active = await gate.EnterAsync();
        int cleaned = 0;

        var stopped = gate.StopAsync(() => cleaned++);
        Assert.False(stopped.IsCompleted);
        Assert.Equal(0, cleaned);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => { using var lease = await gate.EnterAsync(); });

        active.Dispose();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, cleaned);
    }

    [Fact]
    public async Task StopAsync_RejectsAlreadyQueuedWorkAndRunsCleanupExactlyOnce()
    {
        var gate = new OcrOperationGate();
        var active = await gate.EnterAsync();
        var queued = gate.EnterAsync().AsTask();
        int cleaned = 0;
        var stopped = gate.StopAsync(() => cleaned++);
        var repeatedStop = gate.StopAsync(() => cleaned += 100);

        active.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(stopped, repeatedStop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, cleaned);
    }

    [Fact]
    public async Task Lease_DoubleDispose_DoesNotAdmitConcurrentWork()
    {
        var gate = new OcrOperationGate();
        var first = await gate.EnterAsync();
        first.Dispose();
        using var second = await gate.EnterAsync();
        first.Dispose();

        using var cts = new CancellationTokenSource();
        var third = gate.EnterAsync(cts.Token).AsTask();
        Assert.False(third.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await third);
    }

    [Fact]
    public async Task StopAsync_CleanupFailure_IsObservableAndStillRejectsNewWork()
    {
        var gate = new OcrOperationGate();

        var stopped = gate.StopAsync(() => throw new InvalidOperationException("cleanup failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => stopped);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => { using var lease = await gate.EnterAsync(); });
    }

    [Fact]
    public async Task ReplacedInitialization_DisposesItsEventuallyCompletedResource()
    {
        var initialized = new TaskCompletionSource<DisposableProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new Lazy<Task<DisposableProbe>>(() => initialized.Task);
        _ = old.Value;
        var cleanup = OcrSessionLifetime.DisposeWhenReadyAsync(old, probe => probe.Dispose());
        var resource = new DisposableProbe();
        Assert.False(cleanup.IsCompleted);

        initialized.SetResult(resource);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public async Task DisposingUnusedInitialization_DoesNotStartModelLoading()
    {
        int loads = 0;
        var unused = new Lazy<Task<DisposableProbe>>(() => { loads++; return Task.FromResult(new DisposableProbe()); });

        await OcrSessionLifetime.DisposeWhenReadyAsync(unused, probe => probe.Dispose());

        Assert.Equal(0, loads);
    }

    private sealed class DisposableProbe : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}

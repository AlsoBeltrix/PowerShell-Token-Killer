using System.Diagnostics;
using System.Security.Cryptography;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class SupervisorWorkerArtifactCaptureTests : IDisposable
{
    private static readonly TimeSpan CheckpointTimeout = TimeSpan.FromSeconds(10);
    private readonly List<string> _roots = [];

    [Fact]
    public async Task Complete_sink_publishes_one_immutable_handle()
    {
        using var store = CreateStore();
        Assert.True(
            store.TryReserve("alpha", out var reservation, out var failure),
            failure);
        using var capture = new SupervisorWorkerArtifactCapture(
            store,
            reservation!,
            maximumBytes: 1024,
            maximumChunkBytes: 128,
            storageWait: TimeSpan.FromSeconds(2));
        capture.BindRequest(requestId: 7);
        var encoded = WorkerOutputArtifactCodec.Encode(
            Content("recoverable output"),
            maximumBytes: 1024);

        Feed(capture, requestId: 7, encoded, chunkBytes: 17);
        await capture.SinkCompletionForTests.WaitAsync(CheckpointTimeout);
        var recovery = await capture.CompleteAtResultAsync();

        Assert.True(recovery.Handle is not null, recovery.DetailCode);
        var handle = Assert.IsType<string>(recovery.Handle);
        Assert.Equal(OutputArtifactState.Available, recovery.State);
        Assert.Contains(
            "recoverable output",
            store.Read(handle, 0, OutputStore.MaximumReadBytes).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stalled_sink_never_delays_result_or_publishes_later()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationClaims = 0;
        using var store = CreateStore(
            maximumArtifactBytes: 1024,
            maximumSessionBytes: 1024,
            maximumAggregateBytes: 1024,
            artifactPublishingClaimedForTests: () =>
                Interlocked.Increment(ref publicationClaims));
        Assert.True(
            store.TryReserve("alpha", out var reservation, out var failure),
            failure);
        using var capture = new SupervisorWorkerArtifactCapture(
            store,
            reservation!,
            maximumBytes: 1024,
            maximumChunkBytes: 128,
            storageWait: TimeSpan.FromMilliseconds(75),
            sinkGateForTests: _ => release.Task);
        capture.BindRequest(requestId: 9);
        var encoded = WorkerOutputArtifactCodec.Encode(
            Content("must never publish"),
            maximumBytes: 1024);
        Feed(capture, requestId: 9, encoded, chunkBytes: 64);

        var stopwatch = Stopwatch.StartNew();
        var recovery = await capture.CompleteAtResultAsync();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), stopwatch.Elapsed.ToString());
        Assert.Null(recovery.Handle);
        Assert.Equal("artifact_sink_incomplete", recovery.DetailCode);
        release.TrySetResult();
        await capture.SinkCompletionForTests.WaitAsync(CheckpointTimeout);
        Assert.Equal(0, Volatile.Read(ref publicationClaims));
        Assert.True(
            SpinWait.SpinUntil(
                () => store.TryReserve(
                    "alpha",
                    out var replacement,
                    out _) && Dispose(replacement),
                CheckpointTimeout),
            "The discarded sink did not return its full quota.");
        Assert.Empty(Directory.GetFiles(store.RootPathForTests));
    }

    [Fact]
    public async Task Full_queue_discards_capture_while_protocol_validation_continues()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var store = CreateStore(
            maximumArtifactBytes: 16,
            maximumSessionBytes: 16,
            maximumAggregateBytes: 16);
        Assert.True(
            store.TryReserve("alpha", out var reservation, out var failure),
            failure);
        using var capture = new SupervisorWorkerArtifactCapture(
            store,
            reservation!,
            maximumBytes: 16,
            maximumChunkBytes: 8,
            storageWait: TimeSpan.FromSeconds(2),
            captureBufferBytesForTests: 8,
            sinkGateForTests: _ => release.Task);
        capture.BindRequest(requestId: 11);
        var bytes = "0123456789abcdef"u8.ToArray();

        Feed(capture, requestId: 11, bytes, chunkBytes: 8);
        var recovery = await capture.CompleteAtResultAsync();

        Assert.True(capture.IsSealed);
        Assert.Null(recovery.Handle);
        Assert.Equal("artifact_queue_full", recovery.DetailCode);
        release.TrySetResult();
        await capture.SinkCompletionForTests.WaitAsync(CheckpointTimeout);
        Assert.Empty(Directory.GetFiles(store.RootPathForTests));
    }

    [Fact]
    public async Task Discard_and_drain_still_rejects_an_invalid_seal()
    {
        using var store = CreateStore(
            maximumArtifactBytes: 16,
            maximumSessionBytes: 16,
            maximumAggregateBytes: 16);
        Assert.True(
            store.TryReserve("alpha", out var reservation, out var failure),
            failure);
        using var capture = new SupervisorWorkerArtifactCapture(
            store,
            reservation!,
            maximumBytes: 16,
            maximumChunkBytes: 8,
            storageWait: TimeSpan.FromSeconds(2),
            captureBufferBytesForTests: 8);
        capture.BindRequest(requestId: 12);
        var bytes = "0123456789abcdef"u8.ToArray();
        capture.Accept(
            new WorkerArtifactChunk(
                12,
                capture.Request.ArtifactId,
                Offset: 0,
                bytes[..8]));
        capture.Accept(
            new WorkerArtifactChunk(
                12,
                capture.Request.ArtifactId,
                Offset: 8,
                bytes[8..]));

        var protocolFailure = Assert.Throws<WorkerProtocolException>(() =>
            capture.Accept(
                new WorkerArtifactSeal(
                    12,
                    capture.Request.ArtifactId,
                    bytes.Length,
                    new string('0', 64))));

        Assert.Equal("artifact_digest_mismatch", protocolFailure.DetailCode);
        Assert.False(capture.IsSealed);
        Assert.Equal(
            "artifact_queue_full",
            (await capture.CompleteAtResultAsync()).DetailCode);
    }

    [Fact]
    public async Task Local_storage_failure_returns_unavailable_without_false_handle()
    {
        using var store = CreateStore(
            artifactCreateStartingForTests: _ =>
                throw new IOException("injected write failure"));
        Assert.True(
            store.TryReserve("alpha", out var reservation, out var failure),
            failure);
        using var capture = new SupervisorWorkerArtifactCapture(
            store,
            reservation!,
            maximumBytes: 1024,
            maximumChunkBytes: 128,
            storageWait: TimeSpan.FromSeconds(2));
        capture.BindRequest(requestId: 13);
        var encoded = WorkerOutputArtifactCodec.Encode(
            Content("storage failure"),
            maximumBytes: 1024);

        Feed(capture, requestId: 13, encoded, chunkBytes: 32);
        await capture.SinkCompletionForTests.WaitAsync(CheckpointTimeout);
        var recovery = await capture.CompleteAtResultAsync();

        Assert.Null(recovery.Handle);
        Assert.Equal("storage_unavailable", recovery.DetailCode);
        Assert.Empty(Directory.GetFiles(store.RootPathForTests));
    }

    [Fact]
    public async Task Irreversible_publication_is_observed_instead_of_stranding_its_handle()
    {
        using var publicationClaimed = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        using var store = CreateStore(
            artifactPublishingClaimedForTests: () =>
            {
                publicationClaimed.Set();
                Assert.True(releasePublication.Wait(CheckpointTimeout));
            });
        Assert.True(
            store.TryReserve("alpha", out var reservation, out var failure),
            failure);
        using var capture = new SupervisorWorkerArtifactCapture(
            store,
            reservation!,
            maximumBytes: 1024,
            maximumChunkBytes: 128,
            storageWait: TimeSpan.FromMilliseconds(75));
        capture.BindRequest(requestId: 14);
        var encoded = WorkerOutputArtifactCodec.Encode(
            Content("publication boundary"),
            maximumBytes: 1024);
        Feed(capture, requestId: 14, encoded, chunkBytes: 32);
        Assert.True(publicationClaimed.Wait(CheckpointTimeout));

        var completion = capture.CompleteAtResultAsync();
        await Task.Delay(150);
        var returnedBeforePublication = completion.IsCompleted;
        releasePublication.Set();
        var recovery = await completion.WaitAsync(CheckpointTimeout);

        Assert.False(
            returnedBeforePublication,
            "The terminal returned before observing an irreversible publication.");
        var handle = Assert.IsType<string>(recovery.Handle);
        Assert.Contains(
            "publication boundary",
            store.Read(handle, 0, OutputStore.MaximumReadBytes).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Healthy_lane_contention_waits_but_wedged_lane_starts_no_contender()
    {
        using var store = CreateStore();
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var starts = 0;
        var first = await store.WaitToStartForegroundOperationAsync(
            () =>
            {
                Interlocked.Increment(ref starts);
                firstEntered.Set();
                releaseFirst.Wait();
                return 1;
            },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.NotNull(first);
        Assert.True(firstEntered.Wait(CheckpointTimeout));

        var healthyWaiter = store.WaitToStartForegroundOperationAsync(
            () =>
            {
                Interlocked.Increment(ref starts);
                return 2;
            },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref starts));
        Assert.False(healthyWaiter.IsCompleted);

        releaseFirst.Set();
        Assert.Equal(1, await first!.WaitAsync(CheckpointTimeout));
        var second = await healthyWaiter.WaitAsync(CheckpointTimeout);
        Assert.NotNull(second);
        Assert.Equal(2, await second!.WaitAsync(CheckpointTimeout));
        Assert.Equal(2, starts);

        firstEntered.Reset();
        releaseFirst.Reset();
        var wedged = await store.WaitToStartForegroundOperationAsync(
            () =>
            {
                Interlocked.Increment(ref starts);
                firstEntered.Set();
                releaseFirst.Wait();
                return 3;
            },
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        Assert.NotNull(wedged);
        Assert.True(firstEntered.Wait(CheckpointTimeout));
        var timedOut = await store.WaitToStartForegroundOperationAsync(
            () =>
            {
                Interlocked.Increment(ref starts);
                return 4;
            },
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.Null(timedOut);
        Assert.Equal(3, starts);
        releaseFirst.Set();
        Assert.Equal(3, await wedged!.WaitAsync(CheckpointTimeout));
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private OutputStore CreateStore(
        long maximumArtifactBytes = 1024,
        long maximumSessionBytes = 2048,
        long maximumAggregateBytes = 4096,
        Action<string>? artifactCreateStartingForTests = null,
        Action? artifactPublishingClaimedForTests = null)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "worker-artifact-tests",
            Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return new OutputStore(new OutputStoreOptions(
            root,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            maximumArtifactBytes,
            maximumSessionBytes,
            maximumAggregateBytes,
            ArtifactCreateStartingForTests: artifactCreateStartingForTests,
            ArtifactPublishingClaimedForTests:
                artifactPublishingClaimedForTests));
    }

    private static OutputArtifactContent Content(string text) =>
        new(
            text,
            StandardError: [],
            Errors: [],
            Warnings: [],
            ExitCode: null,
            OutputProvenance.DirectText);

    private static void Feed(
        IWorkerArtifactCapture capture,
        long requestId,
        byte[] bytes,
        int chunkBytes)
    {
        long offset = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(
                chunkBytes,
                bytes.Length - checked((int)offset));
            capture.Accept(
                new WorkerArtifactChunk(
                    requestId,
                    capture.Request.ArtifactId,
                    offset,
                    bytes.AsSpan(checked((int)offset), count).ToArray()));
            offset += count;
        }

        capture.Accept(
            new WorkerArtifactSeal(
                requestId,
                capture.Request.ArtifactId,
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
    }

    private static bool Dispose(IDisposable? disposable)
    {
        disposable?.Dispose();
        return true;
    }
}

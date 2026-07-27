using PtkMcpServer;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class UnixWorkerContainmentRegistryTests
{
    private static readonly TimeSpan CheckpointTimeout =
        TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Empty_group_and_exact_dead_identities_release_the_slot()
    {
        var native = new RecordingNative();
        var identity = native.AddPendingDomain(10, 20);
        using var registry = new UnixWorkerContainmentRegistry(native);

        await registry.RegisterPendingAsync(
            identity,
            CancellationToken.None);
        native.Arm(identity);
        await registry.RegisterArmedAsync(
            identity,
            CancellationToken.None);
        await WaitUntilAsync(() => registry.HealthyObservationCount > 0);

        var empty = registry.WaitForEmptyAsync(identity);
        native.RemoveDomain(identity);
        var result = await registry.CompleteAsync(
            identity,
            brokerConfirmed: true,
            CancellationToken.None);

        Assert.Equal(
            WorkerContainmentOutcome.ConfirmedEmpty,
            result.Outcome);
        await empty.WaitAsync(CheckpointTimeout);

        await Task.Delay(300);
        var snapshotsAfterCompletion = native.SnapshotCount;
        await Task.Delay(350);
        Assert.Equal(snapshotsAfterCompletion, native.SnapshotCount);
    }

    [Fact]
    public async Task Gated_worker_needs_no_poll_observation_to_confirm_empty()
    {
        var native = new RecordingNative();
        var identity = native.AddPendingDomain(10, 20);
        using var registry = new UnixWorkerContainmentRegistry(native);

        await registry.RegisterPendingAsync(
            identity,
            CancellationToken.None);
        var empty = registry.WaitForEmptyAsync(identity);
        native.RemoveDomain(identity);
        native.ProcessTableUnavailable = true;

        var result = await registry.CompleteAsync(
            identity,
            brokerConfirmed: true,
            CancellationToken.None);

        Assert.Equal(
            WorkerContainmentOutcome.ConfirmedEmpty,
            result.Outcome);
        await empty.WaitAsync(CheckpointTimeout);
    }

    [Fact]
    public async Task Observed_group_escape_is_unknown_and_blocks_replacement_until_dead()
    {
        var native = new RecordingNative();
        var identity = native.AddPendingDomain(10, 20);
        using var registry = new UnixWorkerContainmentRegistry(native);

        await registry.RegisterPendingAsync(
            identity,
            CancellationToken.None);
        native.Arm(identity);
        native.AddDescendant(30, parent: 20, processGroup: 20);
        await registry.RegisterArmedAsync(
            identity,
            CancellationToken.None);
        await WaitUntilAsync(() => registry.HealthyObservationCount > 0);

        native.SetProcessGroup(30, 30);
        await WaitUntilAsync(() => registry.EscapeObserved);
        var empty = registry.WaitForEmptyAsync(identity);
        native.RemoveDomain(identity);

        var result = await registry.CompleteAsync(
            identity,
            brokerConfirmed: true,
            CancellationToken.None);

        Assert.Equal(
            WorkerContainmentOutcome.DescendantsUnknown,
            result.Outcome);
        Assert.Equal("descendants_unknown", result.DetailCode);
        Assert.False(empty.IsCompleted);

        var replacement = native.AddPendingDomain(11, 21);
        var blocked = await Assert.ThrowsAsync<WorkerProcessException>(
            async () => await registry.RegisterPendingAsync(
                replacement,
                CancellationToken.None));
        Assert.Equal(
            "previous_containment_unconfirmed",
            blocked.DetailCode);

        native.RemoveProcess(30);
        await empty.WaitAsync(CheckpointTimeout);
        await registry.RegisterPendingAsync(
            replacement,
            CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + CheckpointTimeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class RecordingNative : IUnixWorkerNative
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<int, UnixProcessIdentity> _identities = [];
        private readonly Dictionary<int, ProcessTableRow> _rows = [];
        private readonly HashSet<int> _groups = [];
        private int _snapshotCount;

        internal int SnapshotCount => Volatile.Read(ref _snapshotCount);
        internal bool ProcessTableUnavailable { get; set; }

        internal UnixWorkerContainmentIdentity AddPendingDomain(
            int brokerProcessId,
            int workerProcessId)
        {
            var broker = new UnixProcessIdentity(
                1,
                checked((ulong)brokerProcessId));
            var worker = new UnixProcessIdentity(
                2,
                checked((ulong)workerProcessId));
            lock (_gate)
            {
                _identities[brokerProcessId] = broker;
                _identities[workerProcessId] = worker;
                _rows[brokerProcessId] = new(
                    brokerProcessId,
                    Environment.ProcessId,
                    100);
                _rows[workerProcessId] = new(
                    workerProcessId,
                    brokerProcessId,
                    100);
            }

            return new UnixWorkerContainmentIdentity(
                brokerProcessId,
                broker,
                workerProcessId,
                worker,
                workerProcessId);
        }

        internal void Arm(UnixWorkerContainmentIdentity identity)
        {
            lock (_gate)
            {
                _rows[identity.WorkerProcessId] = new(
                    identity.WorkerProcessId,
                    identity.BrokerProcessId,
                    identity.WorkerProcessGroup);
                _groups.Add(identity.WorkerProcessGroup);
            }
        }

        internal void AddDescendant(
            int processId,
            int parent,
            int processGroup)
        {
            lock (_gate)
            {
                _identities[processId] = new(
                    3,
                    checked((ulong)processId));
                _rows[processId] = new(
                    processId,
                    parent,
                    processGroup);
            }
        }

        internal void SetProcessGroup(int processId, int processGroup)
        {
            lock (_gate)
            {
                var existing = _rows[processId];
                _rows[processId] = existing with { Pgid = processGroup };
            }
        }

        internal void RemoveDomain(UnixWorkerContainmentIdentity identity)
        {
            lock (_gate)
            {
                _identities.Remove(identity.BrokerProcessId);
                _identities.Remove(identity.WorkerProcessId);
                _rows.Remove(identity.BrokerProcessId);
                _rows.Remove(identity.WorkerProcessId);
                _groups.Remove(identity.WorkerProcessGroup);
            }
        }

        internal void RemoveProcess(int processId)
        {
            lock (_gate)
            {
                _identities.Remove(processId);
                _rows.Remove(processId);
                _groups.Remove(processId);
            }
        }

        public int Spawn(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            IReadOnlyList<(int Source, int Target)> descriptorMappings,
            out int processId) =>
            throw new NotSupportedException();

        public UnixProcessIdentity QueryIdentity(int processId)
        {
            lock (_gate)
            {
                return _identities.TryGetValue(processId, out var identity)
                    ? identity
                    : throw new ArgumentException("process absent");
            }
        }

        public int GetProcessGroup(int processId)
        {
            if (processId == 0)
                return 100;
            lock (_gate)
            {
                return _rows.TryGetValue(processId, out var row)
                    ? row.Pgid
                    : throw new ArgumentException("process absent");
            }
        }

        public bool ProcessGroupExists(int processGroup)
        {
            lock (_gate)
                return _groups.Contains(processGroup);
        }

        public List<ProcessTableRow>? TryTakeProcessTable()
        {
            Interlocked.Increment(ref _snapshotCount);
            if (ProcessTableUnavailable)
                return null;
            lock (_gate)
                return _rows.Values.ToList();
        }

        public Task<int> WaitForExitCodeAsync(int processId) =>
            throw new NotSupportedException();
    }
}

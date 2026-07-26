using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PtkMcpServer.GuardianHost;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class WorkerPrivateHostRuntimeTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb"));
    private static readonly HostGeneration HostGeneration = new(3);
    private static readonly PrivateHostServerIdentity Identity = new(
        Guardian,
        Host,
        HostGeneration,
        hostPid: 42);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(4);
    private const long Deadline = 10_000;

    [Fact]
    public async Task Production_operation_surface_routes_only_through_worker_protocols()
    {
        var rig = new RuntimeRig(generations: [9]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var worker = rig.Runtime.WorkerIdentity!;
        var foreground = Request(
            10,
            worker,
            new InvokeForegroundOperation(
                Call(10),
                Dispatch(10),
                Output(10),
                "Get-Date",
                raw: false,
                GuardianHostInvokeRoute.Auto));
        var background = Request(
            11,
            worker,
            new InvokeBackgroundOperation(
                Call(11),
                Dispatch(11),
                Output(11),
                "Get-Process",
                raw: false,
                GuardianHostInvokeRoute.Pwsh,
                new PublicJobId(71)));

        var foregroundOutcome = await rig.Runtime.ExecuteOperationAsync(
            foreground,
            TestContext.Current.CancellationToken);
        var backgroundOutcome = await rig.Runtime.ExecuteOperationAsync(
            background,
            TestContext.Current.CancellationToken);
        var backgroundResult = Assert.IsType<InvokeBackgroundResult>(
            backgroundOutcome.Result);
        var jobRequests = new[]
        {
            Request(
                12,
                worker,
                new JobListOperation(Call(12), Dispatch(12))),
            Request(
                13,
                worker,
                new JobStatusOperation(
                    Call(13),
                    Dispatch(13),
                    new PublicJobId(71),
                    backgroundResult.JobCapability)),
            Request(
                14,
                worker,
                new JobOutputOperation(
                    Call(14),
                    Dispatch(14),
                    Output(14),
                    new PublicJobId(71),
                    backgroundResult.JobCapability,
                    offset: 5)),
            Request(
                15,
                worker,
                new JobKillOperation(
                    Call(15),
                    Dispatch(15),
                    new PublicJobId(71),
                    backgroundResult.JobCapability)),
        };
        var jobOutcomes = new List<PrivateHostOperationOutcome>();
        foreach (var request in jobRequests)
        {
            jobOutcomes.Add(await rig.Runtime.ExecuteOperationAsync(
                request,
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            "foreground",
            Assert.IsType<InvokeForegroundResult>(
                foregroundOutcome.Result).Text);
        Assert.Equal(new PublicJobId(71), backgroundResult.PublicJobId);
        Assert.Equal(
            "jobs",
            Assert.IsType<JobListResult>(jobOutcomes[0].Result).Text);
        Assert.Equal(
            "status",
            Assert.IsType<JobStatusResult>(jobOutcomes[1].Result).Text);
        // The fake returns a realistic page with its `next offset:` framing;
        // what this identity pins is that the worker's own text is what comes
        // back, not the exact fixture string.
        Assert.StartsWith(
            "output",
            Assert.IsType<JobOutputResult>(jobOutcomes[2].Result).Text,
            StringComparison.Ordinal);
        Assert.Equal(
            "kill",
            Assert.IsType<PtkSharedContracts.JobKillResult>(
                jobOutcomes[3].Result).Text);

        var process = Assert.Single(rig.Launch.Processes);
        Assert.Equal(
            [
                WorkerPreparedInvokeKind.Foreground,
                WorkerPreparedInvokeKind.Background,
            ],
            process.PreparedKinds);
        Assert.Equal(
            [
                WorkerSessionOperationCodec.JobListOperation,
                WorkerSessionOperationCodec.JobStatusOperation,
                WorkerSessionOperationCodec.JobOutputOperation,
                WorkerSessionOperationCodec.JobKillOperation,
            ],
            process.OrdinaryOperations);
        Assert.DoesNotContain(
            WorkerSessionOperationCodec.InvokeOperation,
            process.OrdinaryOperations);
        Assert.Collection(
            rig.Output.Transfers,
            transfer =>
            {
                Assert.Same(foreground, transfer.Request);
                Assert.Equal("foreground", transfer.Text);
            },
            transfer =>
            {
                Assert.Same(jobRequests[2], transfer.Request);
                Assert.StartsWith("output", transfer.Text, StringComparison.Ordinal);
            });
        Assert.All(
            rig.Events.Events.OfType<OperationDeliveryEvent>(),
            delivery =>
            {
                if (delivery.DeliveryState !=
                    GuardianHostDeliveryState.NotDispatched)
                {
                    Assert.NotEqual(
                        delivery.RequestId,
                        delivery.WorkerRequestId);
                }
            });
    }

    [Fact]
    public async Task Reset_shuts_down_old_generation_before_new_slot_becomes_ready()
    {
        var rig = new RuntimeRig(generations: [9, 10]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var oldWorker = rig.Runtime.WorkerIdentity!;
        var reset = Request(
            20,
            oldWorker,
            new ResetOperation(
                Call(20),
                Dispatch(20),
                expectedGeneration: 9,
                force: false));

        var outcome = await rig.Runtime.ExecuteOperationAsync(
            reset,
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<ResetResult>(outcome.Result);
        Assert.Equal(10, result.WorkerIdentity?.Generation.Value);
        Assert.NotEqual(oldWorker.BootId, result.WorkerIdentity?.BootId);
        Assert.True(result.WarmStateLost);
        Assert.Equal(2, rig.Launch.Processes.Count);
        Assert.True(rig.Launch.Processes[0].Shutdown);
        Assert.True(rig.Launch.Processes[0].Disposed);
        Assert.False(rig.Launch.Processes[1].Shutdown);
        Assert.Equal(
            ["launch:9", "shutdown:9", "launch:10"],
            rig.Launch.Order);
        Assert.Collection(
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>(),
            initial => Assert.Equal(9, initial.WorkerIdentity?.Generation.Value),
            replacement =>
            {
                Assert.Equal(
                    GuardianHostSessionLifecycleReason.RequestedReset,
                    replacement.Reason);
                Assert.Equal(10, replacement.WorkerIdentity?.Generation.Value);
                Assert.Equal(reset.RequestId, replacement.RequestId);
            });
        var resetDeliveries = rig.Events.Events
            .OfType<OperationDeliveryEvent>()
            .Where(value => value.RequestId == reset.RequestId)
            .ToArray();
        Assert.Equal(2, resetDeliveries.Length);
        Assert.Equal(
            GuardianHostDeliveryState.WriteStarted,
            resetDeliveries[0].DeliveryState);
        Assert.Equal(
            GuardianHostDeliveryState.TerminalDecoded,
            resetDeliveries[1].DeliveryState);
        Assert.Equal(
            resetDeliveries[0].WorkerRequestId,
            resetDeliveries[1].WorkerRequestId);
    }

    [Fact]
    public async Task Foreign_job_capability_is_refused_before_worker_write()
    {
        var rig = new RuntimeRig(generations: [9]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var worker = rig.Runtime.WorkerIdentity!;
        var process = Assert.Single(rig.Launch.Processes);
        var request = Request(
            30,
            worker,
            new JobStatusOperation(
                Call(30),
                Dispatch(30),
                new PublicJobId(71),
                Token(0x77)));

        var outcome = await rig.Runtime.ExecuteOperationAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            GuardianHostPrivateDetailCode.JobCapabilityInvalid,
            outcome.Error?.DetailCode);
        Assert.Empty(process.OrdinaryOperations);
        var refusal = Assert.IsType<OperationDeliveryEvent>(
            rig.Events.SnapshotEvents().Last());
        Assert.Equal(
            GuardianHostDeliveryState.NotDispatched,
            refusal.DeliveryState);
        Assert.Null(refusal.WorkerRequestId);
    }

    [Fact]
    public async Task Shutdown_drains_worker_and_disposes_slot_once()
    {
        var rig = new RuntimeRig(generations: [9]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var process = Assert.Single(rig.Launch.Processes);

        await rig.Runtime.ShutdownAsync(
            Shutdown(),
            TestContext.Current.CancellationToken);
        await rig.Runtime.ShutdownAsync(
            Shutdown(),
            TestContext.Current.CancellationToken);

        Assert.True(process.Shutdown);
        Assert.True(process.Disposed);
        Assert.Equal(WorkerPrivateHostRuntimeState.Stopped, rig.Runtime.State);
    }

    [Fact]
    public async Task Declared_cold_dynamic_alias_holds_no_slot_until_opened()
    {
        var defaultBinding = new RecoveryBinding(
            Alias,
            RecoveryBindingKind.Default,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground: true,
            DesiredSessionState.Ready,
            Transition,
            Digest('a'));
        var dynamicAlias = new CanonicalAlias("scratch");
        var dynamicBinding = new RecoveryBinding(
            dynamicAlias,
            RecoveryBindingKind.Dynamic,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground: true,
            DesiredSessionState.Cold,
            new SessionTransitionVersion(1),
            Digest('d'));
        var manifest = new RecoveryManifest(
            Guardian,
            HostGeneration,
            Digest('b'),
            Digest('c'),
            [],
            [defaultBinding, dynamicBinding],
            [
                new WorkerGenerationHighWatermarkEntry(
                    Alias,
                    new WorkerGenerationHighWatermark(8)),
                new WorkerGenerationHighWatermarkEntry(
                    dynamicAlias,
                    new WorkerGenerationHighWatermark(1)),
            ],
            HostGeneration);
        var initialization = new PrivateHostInitialization(
            manifest,
            new PrivateRequestId(1),
            new ManifestId(
                Guid.Parse("11111111-1111-4111-8111-111111111111")),
            Sha256Digest.Compute(RecoveryManifestCodec.Encode(manifest)));
        var rig = new RuntimeRig(generations: [9]);

        await rig.Runtime.InitializeAsync(
            initialization,
            TestContext.Current.CancellationToken);

        var process = Assert.Single(rig.Launch.Processes);
        Assert.Equal(9, process.Generation);
        var defaultWorker = rig.Runtime.WorkerIdentity!;
        var invoke = Request(
            20,
            defaultWorker,
            new JobListOperation(Call(20), Dispatch(20)));
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            invoke,
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);

        var cold = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(21),
            Deadline,
            dynamicAlias,
            new SessionTransitionVersion(1),
            new GuardianHostWorkerIdentity(
                defaultWorker.BootId,
                new WorkerGeneration(1)),
            null,
            new JobListOperation(Call(21), Dispatch(21)));
        var coldOutcome = await rig.Runtime.ExecuteOperationAsync(
            cold,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.WorkerLost,
            coldOutcome.Error?.DetailCode);
        Assert.Single(rig.Launch.Processes);
    }

    [Fact]
    public async Task Open_creates_a_new_alias_worker_and_returns_its_identity()
    {
        var rig = new RuntimeRig(generations: [9, 10]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var alias = new CanonicalAlias("scratch");
        var open = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(20),
            Deadline,
            alias,
            new SessionTransitionVersion(1),
            workerIdentity: null,
            null,
            new SessionOpenOperation(
                Call(20),
                Dispatch(20),
                template: null,
                allowColdBackground: true));

        var outcome = await rig.Runtime.ExecuteOperationAsync(
            open,
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<SessionOpenResult>(outcome.Result);
        Assert.Equal(alias, result.Alias);
        Assert.Equal(PublicSessionState.Ready, result.State);
        Assert.Equal(10, result.WorkerIdentity?.Generation.Value);
        Assert.True(result.ReadyForEffects);
        Assert.False(result.WarmStateLost);
        Assert.Equal(BootstrapState.Restored, result.BootstrapState);
        Assert.Equal(2, rig.Launch.Processes.Count);
        Assert.Equal(
            ["launch:9", "launch:10"],
            rig.Launch.Order);
        var lifecycle = Assert.IsType<SessionLifecycleEvent>(
            rig.Events.SnapshotEvents().Last());
        Assert.Equal(
            GuardianHostSessionLifecycleReason.RequestedOpen,
            lifecycle.Reason);
        Assert.Equal(new PrivateRequestId(20), lifecycle.RequestId);
        Assert.Equal(alias, lifecycle.SessionAlias);
        Assert.Equal(10, lifecycle.WorkerIdentity?.Generation.Value);

        var invoke = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(21),
            Deadline,
            alias,
            new SessionTransitionVersion(1),
            result.WorkerIdentity,
            null,
            new JobListOperation(Call(21), Dispatch(21)));
        var invokeOutcome = await rig.Runtime.ExecuteOperationAsync(
            invoke,
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(invokeOutcome.Result);
        Assert.Equal(
            WorkerSessionOperationCodec.JobListOperation,
            Assert.Single(rig.Launch.Processes[1].OrdinaryOperations));

        var reopen = await rig.Runtime.ExecuteOperationAsync(
            open,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.SessionBusy,
            reopen.Error?.DetailCode);
        Assert.Equal(2, rig.Launch.Processes.Count);

        var templated = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(22),
            Deadline,
            new CanonicalAlias("templated"),
            new SessionTransitionVersion(1),
            workerIdentity: null,
            null,
            new SessionOpenOperation(
                Call(22),
                Dispatch(22),
                new CanonicalAlias("template-one"),
                allowColdBackground: true));
        var templatedOutcome = await rig.Runtime.ExecuteOperationAsync(
            templated,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.UnsupportedOperation,
            templatedOutcome.Error?.DetailCode);
        Assert.Equal(2, rig.Launch.Processes.Count);
    }

    [Fact]
    public async Task Failed_reset_faults_only_its_alias_and_clears_its_job_budget()
    {
        var rig = new RuntimeRig(generations: [9, 10]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);
        var background = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(30),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            scratchWorker,
            new GuardianHostOperationIdentity(
                Plan(30),
                new OperationId(GuidFrom(30, version: 4))),
            new InvokeBackgroundOperation(
                Call(30),
                Dispatch(30),
                Output(30),
                "Get-Process",
                raw: false,
                GuardianHostInvokeRoute.Pwsh,
                new PublicJobId(71)));
        var backgroundOutcome = await rig.Runtime.ExecuteOperationAsync(
            background,
            TestContext.Current.CancellationToken);
        Assert.IsType<InvokeBackgroundResult>(backgroundOutcome.Result);
        Assert.Equal(1, rig.Runtime.OutstandingJobCapabilityCount);

        rig.Launch.Processes[1].FailShutdown = true;
        var reset = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(31),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            scratchWorker,
            null,
            new ResetOperation(
                Call(31),
                Dispatch(31),
                expectedGeneration: 10,
                force: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Runtime.ExecuteOperationAsync(
                reset,
                TestContext.Current.CancellationToken).AsTask());

        var scratchOutcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(32),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                scratchWorker,
                null,
                new JobListOperation(Call(32), Dispatch(32))),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.SessionFaulted,
            scratchOutcome.Error?.DetailCode);

        var defaultWorker = rig.Runtime.WorkerIdentity!;
        var defaultOutcome = await rig.Runtime.ExecuteOperationAsync(
            Request(33, defaultWorker, new JobListOperation(Call(33), Dispatch(33))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(defaultOutcome.Result);
        Assert.Equal(9, rig.Launch.Processes[0].Generation);
        Assert.False(rig.Launch.Processes[0].Shutdown);
        Assert.Equal(WorkerPrivateHostRuntimeState.Ready, rig.Runtime.State);
        Assert.Equal(0, rig.Runtime.OutstandingJobCapabilityCount);
        Assert.Equal(2, rig.Launch.Processes.Count);

        var fault = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Last();
        Assert.Equal(scratch, fault.SessionAlias);
        Assert.Equal(PublicSessionState.Resetting, fault.PreviousState);
        Assert.Equal(PublicSessionState.Faulted, fault.State);
        Assert.Equal(
            GuardianHostSessionLifecycleReason.ContainmentUnconfirmed,
            fault.Reason);
        Assert.Equal(10, fault.WorkerIdentity?.Generation.Value);
        Assert.False(fault.ReadyForEffects);
        Assert.True(fault.WarmStateLost);
        Assert.Equal(BootstrapState.Failed, fault.BootstrapState);
    }

    [Fact]
    public async Task Failed_close_faults_only_its_alias()
    {
        var rig = new RuntimeRig(generations: [9, 10]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);
        rig.Launch.Processes[1].FailShutdown = true;

        var close = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(31),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            scratchWorker,
            null,
            new SessionCloseOperation(
                Call(31),
                Dispatch(31),
                expectedGeneration: 10,
                force: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Runtime.ExecuteOperationAsync(
                close,
                TestContext.Current.CancellationToken).AsTask());

        var scratchOutcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(32),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                scratchWorker,
                null,
                new JobListOperation(Call(32), Dispatch(32))),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.SessionFaulted,
            scratchOutcome.Error?.DetailCode);

        var defaultWorker = rig.Runtime.WorkerIdentity!;
        var defaultOutcome = await rig.Runtime.ExecuteOperationAsync(
            Request(33, defaultWorker, new JobListOperation(Call(33), Dispatch(33))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(defaultOutcome.Result);
        Assert.Equal(WorkerPrivateHostRuntimeState.Ready, rig.Runtime.State);

        var fault = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Last();
        Assert.Equal(PublicSessionState.Closing, fault.PreviousState);
        Assert.Equal(PublicSessionState.Faulted, fault.State);
        Assert.Equal(
            GuardianHostSessionLifecycleReason.ContainmentUnconfirmed,
            fault.Reason);
    }

    [Fact]
    public async Task Failed_relaunch_reports_bootstrap_failed_with_the_old_identity()
    {
        var rig = new RuntimeRig(generations: [9, 10]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);
        rig.Launch.FailNextLaunch = true;

        var reset = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(31),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            scratchWorker,
            null,
            new ResetOperation(
                Call(31),
                Dispatch(31),
                expectedGeneration: 10,
                force: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Runtime.ExecuteOperationAsync(
                reset,
                TestContext.Current.CancellationToken).AsTask());

        var fault = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Last();
        Assert.Equal(scratch, fault.SessionAlias);
        Assert.Equal(PublicSessionState.Resetting, fault.PreviousState);
        Assert.Equal(PublicSessionState.Faulted, fault.State);
        Assert.Equal(
            GuardianHostSessionLifecycleReason.BootstrapFailed,
            fault.Reason);
        Assert.Equal(10, fault.WorkerIdentity?.Generation.Value);

        var scratchOutcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(32),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                scratchWorker,
                null,
                new JobListOperation(Call(32), Dispatch(32))),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.SessionFaulted,
            scratchOutcome.Error?.DetailCode);
        var defaultOutcome = await rig.Runtime.ExecuteOperationAsync(
            Request(
                33,
                rig.Runtime.WorkerIdentity!,
                new JobListOperation(Call(33), Dispatch(33))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(defaultOutcome.Result);
        Assert.Equal(WorkerPrivateHostRuntimeState.Ready, rig.Runtime.State);
    }

    [Fact]
    public async Task Post_ready_failure_commits_the_announced_replacement()
    {
        var rig = new RuntimeRig(generations: [9, 10, 11]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);
        rig.Events.FailNextTerminalDecodedDelivery = true;

        var reset = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(31),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            scratchWorker,
            null,
            new ResetOperation(
                Call(31),
                Dispatch(31),
                expectedGeneration: 10,
                force: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Runtime.ExecuteOperationAsync(
                reset,
                TestContext.Current.CancellationToken).AsTask());

        Assert.DoesNotContain(
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>(),
            lifecycle => lifecycle.State == PublicSessionState.Faulted);
        var ready = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Last();
        Assert.Equal(scratch, ready.SessionAlias);
        Assert.Equal(PublicSessionState.Ready, ready.State);
        Assert.Equal(11, ready.WorkerIdentity?.Generation.Value);

        var replacement = rig.Launch.Processes[2];
        var replacementWorker = new GuardianHostWorkerIdentity(
            new WorkerBootId(replacement.WorkerBootId),
            new WorkerGeneration(replacement.Generation));
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(32),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                replacementWorker,
                null,
                new JobListOperation(Call(32), Dispatch(32))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);
        Assert.Equal(WorkerPrivateHostRuntimeState.Ready, rig.Runtime.State);
        Assert.Equal(
            WorkerSessionOperationCodec.JobListOperation,
            Assert.Single(replacement.OrdinaryOperations));
    }

    [Fact]
    public async Task Post_cold_close_failure_leaves_the_alias_cold()
    {
        var rig = new RuntimeRig(generations: [9, 10]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);
        rig.Events.FailNextTerminalDecodedDelivery = true;

        var close = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(31),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            scratchWorker,
            null,
            new SessionCloseOperation(
                Call(31),
                Dispatch(31),
                expectedGeneration: 10,
                force: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Runtime.ExecuteOperationAsync(
                close,
                TestContext.Current.CancellationToken).AsTask());

        Assert.DoesNotContain(
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>(),
            lifecycle => lifecycle.State == PublicSessionState.Faulted);
        var cold = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Last();
        Assert.Equal(scratch, cold.SessionAlias);
        Assert.Equal(PublicSessionState.Closing, cold.PreviousState);
        Assert.Equal(PublicSessionState.Cold, cold.State);
        Assert.Equal(
            GuardianHostSessionLifecycleReason.RequestedClose,
            cold.Reason);

        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(32),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                scratchWorker,
                null,
                new JobListOperation(Call(32), Dispatch(32))),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.WorkerLost,
            outcome.Error?.DetailCode);
        var defaultOutcome = await rig.Runtime.ExecuteOperationAsync(
            Request(
                33,
                rig.Runtime.WorkerIdentity!,
                new JobListOperation(Call(33), Dispatch(33))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(defaultOutcome.Result);
        Assert.Equal(WorkerPrivateHostRuntimeState.Ready, rig.Runtime.State);
    }

    private static async Task<GuardianHostWorkerIdentity> OpenAliasAsync(
        RuntimeRig rig,
        CanonicalAlias alias,
        long expectedGeneration)
    {
        var open = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(20),
            Deadline,
            alias,
            new SessionTransitionVersion(1),
            workerIdentity: null,
            null,
            new SessionOpenOperation(
                Call(20),
                Dispatch(20),
                template: null,
                allowColdBackground: true));
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            open,
            TestContext.Current.CancellationToken);
        var result = Assert.IsType<SessionOpenResult>(outcome.Result);
        Assert.Equal(expectedGeneration, result.WorkerIdentity?.Generation.Value);
        return result.WorkerIdentity!;
    }

    [Fact]
    public async Task A_worker_dying_during_initialization_recovers_once_ready()
    {
        var defaultBinding = new RecoveryBinding(
            Alias,
            RecoveryBindingKind.Default,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground: true,
            DesiredSessionState.Ready,
            Transition,
            Digest('a'));
        var dynamicAlias = new CanonicalAlias("scratch");
        var manifest = new RecoveryManifest(
            Guardian,
            HostGeneration,
            Digest('b'),
            Digest('c'),
            [],
            [
                defaultBinding,
                new RecoveryBinding(
                    dynamicAlias,
                    RecoveryBindingKind.Dynamic,
                    templateName: null,
                    templateDigest: null,
                    bootstrapDigest: null,
                    allowColdBackground: true,
                    DesiredSessionState.Ready,
                    new SessionTransitionVersion(1),
                    Digest('d')),
            ],
            [
                new WorkerGenerationHighWatermarkEntry(
                    Alias,
                    new WorkerGenerationHighWatermark(8)),
                new WorkerGenerationHighWatermarkEntry(
                    dynamicAlias,
                    new WorkerGenerationHighWatermark(1)),
            ],
            HostGeneration);
        var initialization = new PrivateHostInitialization(
            manifest,
            new PrivateRequestId(1),
            new ManifestId(
                Guid.Parse("11111111-1111-4111-8111-111111111111")),
            Sha256Digest.Compute(RecoveryManifestCodec.Encode(manifest)));
        var rig = new RuntimeRig(generations: [9, 10, 11]);
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Launch.OnLaunchCall = index =>
        {
            if (index == 1) rig.Launch.BlockNextLaunch = gate;
        };

        var initialize = rig.Runtime.InitializeAsync(
                initialization,
                TestContext.Current.CancellationToken)
            .AsTask();
        await WaitUntilAsync(() =>
            rig.Launch.Processes.Count == 1,
            "the first slot launching while the second is gated");
        rig.Launch.Processes[0].Kill();
        // The dead worker's watcher continuation must run (and be refused by
        // the pre-Ready lease gate) before initialization is allowed to reach
        // Ready — otherwise the old in-loop arming recovers anyway and this
        // test cannot prove the post-Ready arming is what saves the alias.
        await Task.Delay(
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);
        gate.TrySetResult();
        await initialize.WaitAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(() =>
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Any(
                lifecycle => lifecycle.Reason ==
                    GuardianHostSessionLifecycleReason.AutomaticRecovery &&
                lifecycle.SessionAlias == Alias &&
                lifecycle.WorkerIdentity?.Generation.Value == 11),
            "the init-time death's recovery after ready");

        var replacement = rig.Launch.Processes[2];
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            Request(
                40,
                new GuardianHostWorkerIdentity(
                    new WorkerBootId(replacement.WorkerBootId),
                    new WorkerGeneration(replacement.Generation)),
                new JobListOperation(Call(40), Dispatch(40))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);
        var scratchOutcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(41),
                Deadline,
                dynamicAlias,
                new SessionTransitionVersion(1),
                new GuardianHostWorkerIdentity(
                    new WorkerBootId(rig.Launch.Processes[1].WorkerBootId),
                    new WorkerGeneration(rig.Launch.Processes[1].Generation)),
                null,
                new JobListOperation(Call(41), Dispatch(41))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(scratchOutcome.Result);
    }

    [Fact]
    public async Task Operations_during_the_recovery_gap_report_worker_lost()
    {
        var rig = new RuntimeRig(generations: [9, 10, 11]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Launch.BlockNextLaunch = gate;

        rig.Launch.Processes[1].Kill();
        await WaitUntilAsync(() => rig.Launch.LaunchCalls >= 3,
            "the recovery reaching the gated launch");

        var gap = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(40),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                scratchWorker,
                null,
                new JobListOperation(Call(40), Dispatch(40))),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.WorkerLost,
            gap.Error?.DetailCode);

        gate.TrySetResult();
        await WaitUntilAsync(() =>
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Any(
                lifecycle => lifecycle.Reason ==
                    GuardianHostSessionLifecycleReason.AutomaticRecovery &&
                lifecycle.SessionAlias == scratch &&
                lifecycle.WorkerIdentity?.Generation.Value == 11),
            "the recovery completing");
        var replacement = rig.Launch.Processes[2];
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(41),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                new GuardianHostWorkerIdentity(
                    new WorkerBootId(replacement.WorkerBootId),
                    new WorkerGeneration(replacement.Generation)),
                null,
                new JobListOperation(Call(41), Dispatch(41))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);
    }

    [Fact]
    public async Task A_manual_reset_clears_the_consecutive_death_counter()
    {
        var rig = new RuntimeRig(generations: [9, 10, 11, 12, 13, 14]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);

        rig.Launch.Processes[1].Kill();
        await WaitUntilAsync(() => rig.Launch.Processes.Count >= 3,
            "first relaunch");
        rig.Launch.Processes[2].Kill();
        await WaitUntilAsync(() => rig.Launch.Processes.Count >= 4,
            "second relaunch");

        var resetWorker = new GuardianHostWorkerIdentity(
            new WorkerBootId(rig.Launch.Processes[3].WorkerBootId),
            new WorkerGeneration(rig.Launch.Processes[3].Generation));
        var reset = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(30),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            resetWorker,
            null,
            new ResetOperation(
                Call(30),
                Dispatch(30),
                expectedGeneration: 12,
                force: false));
        var resetOutcome = await rig.Runtime.ExecuteOperationAsync(
            reset,
            TestContext.Current.CancellationToken);
        Assert.IsType<ResetResult>(resetOutcome.Result);

        rig.Launch.Processes[4].Kill();
        await WaitUntilAsync(() => rig.Launch.Processes.Count >= 6,
            "the post-reset relaunch");
        Assert.Equal(6, rig.Launch.Processes.Count);
        Assert.DoesNotContain(
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>(),
            lifecycle => lifecycle.State == PublicSessionState.Faulted);
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(40),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                new GuardianHostWorkerIdentity(
                    new WorkerBootId(rig.Launch.Processes[5].WorkerBootId),
                    new WorkerGeneration(rig.Launch.Processes[5].Generation)),
                null,
                new JobListOperation(Call(40), Dispatch(40))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);
    }

    [Fact]
    public async Task A_failed_close_delivery_clears_the_counter_through_reopen()
    {
        var rig = new RuntimeRig(generations: [9, 10, 11, 12, 13, 14]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);

        rig.Launch.Processes[1].Kill();
        await WaitUntilAsync(() => rig.Launch.Processes.Count >= 3,
            "first relaunch");
        rig.Launch.Processes[2].Kill();
        await WaitUntilAsync(() => rig.Launch.Processes.Count >= 4,
            "second relaunch");

        rig.Events.FailNextTerminalDecodedDelivery = true;
        var close = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(30),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            new GuardianHostWorkerIdentity(
                new WorkerBootId(rig.Launch.Processes[3].WorkerBootId),
                new WorkerGeneration(rig.Launch.Processes[3].Generation)),
            null,
            new SessionCloseOperation(
                Call(30),
                Dispatch(30),
                expectedGeneration: 12,
                force: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Runtime.ExecuteOperationAsync(
                close,
                TestContext.Current.CancellationToken).AsTask());

        var reopen = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(31),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            workerIdentity: null,
            null,
            new SessionOpenOperation(
                Call(31),
                Dispatch(31),
                template: null,
                allowColdBackground: true));
        var reopenOutcome = await rig.Runtime.ExecuteOperationAsync(
            reopen,
            TestContext.Current.CancellationToken);
        Assert.IsType<SessionOpenResult>(reopenOutcome.Result);

        rig.Launch.Processes[4].Kill();
        await WaitUntilAsync(() => rig.Launch.Processes.Count >= 6,
            "the post-reopen relaunch");
        Assert.Equal(6, rig.Launch.Processes.Count);
        Assert.DoesNotContain(
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>(),
            lifecycle => lifecycle.State == PublicSessionState.Faulted);
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(40),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                new GuardianHostWorkerIdentity(
                    new WorkerBootId(rig.Launch.Processes[5].WorkerBootId),
                    new WorkerGeneration(rig.Launch.Processes[5].Generation)),
                null,
                new JobListOperation(Call(40), Dispatch(40))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);
    }

    [Fact]
    public async Task Spaced_deaths_reset_the_counter_after_the_stability_window()
    {        var rig = new RuntimeRig(
            generations: [9, 10, 11, 12, 13],
            stabilityWindow: TimeSpan.FromMilliseconds(200));
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        _ = await OpenAliasAsync(rig, scratch, 10);

        for (var index = 1; index <= 3; index++)
        {
            rig.Launch.Processes[index].Kill();
            await WaitUntilAsync(() =>
                rig.Launch.Processes.Count >= index + 2,
                $"relaunch {index}");
            await Task.Delay(
                TimeSpan.FromMilliseconds(400),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(5, rig.Launch.Processes.Count);
        Assert.DoesNotContain(
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>(),
            lifecycle => lifecycle.State == PublicSessionState.Faulted);
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(40),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                new GuardianHostWorkerIdentity(
                    new WorkerBootId(rig.Launch.Processes[4].WorkerBootId),
                    new WorkerGeneration(rig.Launch.Processes[4].Generation)),
                null,
                new JobListOperation(Call(40), Dispatch(40))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);
    }

    [Fact]
    public async Task Unexpected_worker_death_relaunches_the_alias_at_the_next_generation()
    {
        var rig = new RuntimeRig(generations: [9, 10, 11]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);

        rig.Launch.Processes[1].Kill();
        await WaitUntilAsync(() =>
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Any(lifecycle =>
                lifecycle.Reason ==
                    GuardianHostSessionLifecycleReason.AutomaticRecovery &&
                lifecycle.SessionAlias == scratch &&
                lifecycle.WorkerIdentity?.Generation.Value == 11),
            "automatic recovery of the dead worker");

        var replacement = rig.Launch.Processes[2];
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(40),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                new GuardianHostWorkerIdentity(
                    new WorkerBootId(replacement.WorkerBootId),
                    new WorkerGeneration(replacement.Generation)),
                null,
                new JobListOperation(Call(40), Dispatch(40))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);

        var defaultOutcome = await rig.Runtime.ExecuteOperationAsync(
            Request(
                41,
                rig.Runtime.WorkerIdentity!,
                new JobListOperation(Call(41), Dispatch(41))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(defaultOutcome.Result);
        Assert.Equal(9, rig.Launch.Processes[0].Generation);
        Assert.False(rig.Launch.Processes[0].Shutdown);
        Assert.Equal(WorkerPrivateHostRuntimeState.Ready, rig.Runtime.State);
        Assert.Contains("launch:11", rig.Launch.Order);

        var recovered = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>()
            .Last(lifecycle => lifecycle.SessionAlias == scratch);
        Assert.Null(recovered.RequestId);
        Assert.True(recovered.WarmStateLost);
        Assert.Equal(BootstrapState.Restored, recovered.BootstrapState);
        Assert.True(recovered.ReadyForEffects);
    }

    [Fact]
    public async Task Unexpected_worker_death_announces_recovery_before_the_alias_returns_ready()
    {
        // The guardian projects the alias's last lifecycle. Without a recovering
        // event it keeps projecting the dead worker's Ready for the whole
        // death-to-relaunch window: a caller sees a usable session that cannot
        // run anything, and an invalidated dispatch target has no recovery
        // evidence to be refused with.
        var rig = new RuntimeRig(generations: [9, 10, 11]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        await OpenAliasAsync(rig, scratch, 10);

        rig.Launch.Processes[1].Kill();
        await WaitUntilAsync(() =>
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Any(lifecycle =>
                lifecycle.Reason ==
                    GuardianHostSessionLifecycleReason.AutomaticRecovery &&
                lifecycle.SessionAlias == scratch &&
                lifecycle.WorkerIdentity?.Generation.Value == 11),
            "automatic recovery of the dead worker");

        var aliasLifecycles = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>()
            .Where(lifecycle => lifecycle.SessionAlias == scratch)
            .ToArray();
        var recoveringIndex = Array.FindIndex(
            aliasLifecycles,
            lifecycle => lifecycle.State == PublicSessionState.Recovering);
        var readyIndex = Array.FindLastIndex(
            aliasLifecycles,
            lifecycle => lifecycle.State == PublicSessionState.Ready);

        Assert.True(recoveringIndex >= 0, "the alias announced it was recovering");
        Assert.True(
            recoveringIndex < readyIndex,
            "recovery is announced before the alias returns ready");

        var recovering = aliasLifecycles[recoveringIndex];
        Assert.Equal(GuardianHostSessionLifecycleReason.WorkerExit, recovering.Reason);
        Assert.False(recovering.ReadyForEffects);
        Assert.True(recovering.WarmStateLost);
        Assert.Equal(BootstrapState.Pending, recovering.BootstrapState);
        Assert.Equal(RecoveryPhase.Containment, recovering.RecoveryPhase);
        // The attempt ordinal is the alias's real consecutive-death count, and
        // the event names the worker being contained, not its replacement.
        Assert.Equal(1, recovering.RecoveryAttempt);
        Assert.Equal(
            ContractLimits.MinimumRetryAfterMilliseconds,
            recovering.RetryAfterMilliseconds);
        Assert.Equal(10, recovering.WorkerIdentity?.Generation.Value);
    }

    [Fact]
    public async Task Execution_timeout_contains_the_worker_and_recovers_a_fresh_baseline()
    {
        // A worker that blew its deadline is still running whatever overran, so
        // its warm state cannot be trusted. The approved contract delivers the
        // single timeout terminal, confirms the old tree dead, then brings the
        // alias back on a fresh declared baseline - and never reruns the call.
        var rig = new RuntimeRig(generations: [9, 10, 11]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);
        var timingOut = rig.Launch.Processes[1];
        timingOut.TimeOutNextOperation = true;

        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(50),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                scratchWorker,
                null,
                new JobListOperation(Call(50), Dispatch(50))),
            TestContext.Current.CancellationToken);

        // The single terminal comes back to the caller; it is not swallowed by
        // the containment that follows it.
        Assert.Equal(
            GuardianHostPrivateDetailCode.RequestDeadlineExpired,
            outcome.Error?.DetailCode);

        await WaitUntilAsync(() =>
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Any(lifecycle =>
                lifecycle.SessionAlias == scratch &&
                lifecycle.State == PublicSessionState.Recovering &&
                lifecycle.Reason ==
                    GuardianHostSessionLifecycleReason.ExecutionTimeout),
            "the timed-out worker is announced as recovering");

        await WaitUntilAsync(() =>
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Any(lifecycle =>
                lifecycle.SessionAlias == scratch &&
                lifecycle.State == PublicSessionState.Ready &&
                lifecycle.WorkerIdentity?.Generation.Value == 11),
            "the alias returns on its next generation");

        // Old tree confirmed dead, replacement launched, and the timed-out call
        // is never rerun on it.
        Assert.True(timingOut.Disposed);
        Assert.Contains("launch:11", rig.Launch.Order);
        var replacement = rig.Launch.Processes[2];
        Assert.Equal(11, replacement.Generation);
        Assert.Empty(replacement.OrdinaryOperations);

        // The recovery is honest about losing warm state, and only this alias
        // is affected - the default alias keeps its worker.
        var recovered = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>()
            .Last(lifecycle => lifecycle.SessionAlias == scratch);
        Assert.True(recovered.WarmStateLost);
        Assert.Equal(9, rig.Launch.Processes[0].Generation);
        Assert.False(rig.Launch.Processes[0].Disposed);
    }

    [Fact]
    public async Task A_crash_loop_faults_the_alias_instead_of_relaunching_forever()
    {
        var rig = new RuntimeRig(generations: [9, 10, 11, 12, 13]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        _ = await OpenAliasAsync(rig, scratch, 10);

        rig.Launch.Processes[1].Kill();
        await WaitUntilAsync(() =>
            rig.Launch.Processes.Count >= 3,
            "first automatic relaunch");
        rig.Launch.Processes[2].Kill();
        await WaitUntilAsync(() =>
            rig.Launch.Processes.Count >= 4,
            "second automatic relaunch");
        rig.Launch.Processes[3].Kill();
        await WaitUntilAsync(() =>
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Any(lifecycle =>
                lifecycle.State == PublicSessionState.Faulted &&
                lifecycle.SessionAlias == scratch),
            "the crash-loop fault");

        Assert.Equal(4, rig.Launch.Processes.Count);
        var fault = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>()
            .Last(lifecycle => lifecycle.SessionAlias == scratch);
        Assert.Equal(PublicSessionState.Ready, fault.PreviousState);
        Assert.Equal(PublicSessionState.Faulted, fault.State);
        Assert.Equal(
            GuardianHostSessionLifecycleReason.CircuitTransition,
            fault.Reason);
        Assert.Null(fault.RequestId);
        Assert.Equal(BootstrapState.Failed, fault.BootstrapState);

        var scratchWorker = new GuardianHostWorkerIdentity(
            new WorkerBootId(rig.Launch.Processes[3].WorkerBootId),
            new WorkerGeneration(rig.Launch.Processes[3].Generation));
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(40),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                scratchWorker,
                null,
                new JobListOperation(Call(40), Dispatch(40))),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.SessionFaulted,
            outcome.Error?.DetailCode);
        var defaultOutcome = await rig.Runtime.ExecuteOperationAsync(
            Request(
                41,
                rig.Runtime.WorkerIdentity!,
                new JobListOperation(Call(41), Dispatch(41))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(defaultOutcome.Result);
    }

    [Fact]
    public async Task A_completed_manual_reset_disarms_the_dead_workers_watcher()
    {
        var rig = new RuntimeRig(generations: [9, 10, 11]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);

        var reset = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(30),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            scratchWorker,
            null,
            new ResetOperation(
                Call(30),
                Dispatch(30),
                expectedGeneration: 10,
                force: false));
        var resetOutcome = await rig.Runtime.ExecuteOperationAsync(
            reset,
            TestContext.Current.CancellationToken);
        Assert.IsType<ResetResult>(resetOutcome.Result);

        rig.Launch.Processes[1].Kill();
        await Task.Delay(
            TimeSpan.FromMilliseconds(300),
            TestContext.Current.CancellationToken);
        Assert.Equal(3, rig.Launch.Processes.Count);
        Assert.DoesNotContain(
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>(),
            lifecycle => lifecycle.SessionAlias == scratch &&
                lifecycle.Reason ==
                    GuardianHostSessionLifecycleReason.AutomaticRecovery);

        var replacement = rig.Launch.Processes[2];
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(40),
                Deadline,
                scratch,
                new SessionTransitionVersion(1),
                new GuardianHostWorkerIdentity(
                    new WorkerBootId(replacement.WorkerBootId),
                    new WorkerGeneration(replacement.Generation)),
                null,
                new JobListOperation(Call(40), Dispatch(40))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (timeout.IsCancellationRequested)
                throw new TimeoutException($"Timed out waiting for {description}.");
            await Task.Delay(
                TimeSpan.FromMilliseconds(25),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Reopen_reuses_the_declared_binding_and_advances_the_generation()
    {        var rig = new RuntimeRig(generations: [9, 10, 11]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var scratch = new CanonicalAlias("scratch");
        var scratchWorker = await OpenAliasAsync(rig, scratch, 10);

        var close = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(30),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            scratchWorker,
            null,
            new SessionCloseOperation(
                Call(30),
                Dispatch(30),
                expectedGeneration: 10,
                force: false));
        var closeOutcome = await rig.Runtime.ExecuteOperationAsync(
            close,
            TestContext.Current.CancellationToken);
        Assert.IsType<SessionCloseResult>(closeOutcome.Result);

        var reopen = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(31),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            workerIdentity: null,
            null,
            new SessionOpenOperation(
                Call(31),
                Dispatch(31),
                template: null,
                allowColdBackground: true));
        var reopenOutcome = await rig.Runtime.ExecuteOperationAsync(
            reopen,
            TestContext.Current.CancellationToken);
        var result = Assert.IsType<SessionOpenResult>(reopenOutcome.Result);
        Assert.Equal(scratch, result.Alias);
        Assert.Equal(PublicSessionState.Ready, result.State);
        Assert.Equal(11, result.WorkerIdentity?.Generation.Value);
        Assert.True(result.WorkerIdentity?.Generation.Value > 10);
        Assert.Equal(
            ["launch:9", "launch:10", "shutdown:10", "launch:11"],
            rig.Launch.Order);

        var lifecycle = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().Last();
        Assert.Equal(scratch, lifecycle.SessionAlias);
        Assert.Equal(PublicSessionState.Ready, lifecycle.State);
        Assert.Equal(
            GuardianHostSessionLifecycleReason.RequestedOpen,
            lifecycle.Reason);
        Assert.Equal(11, lifecycle.WorkerIdentity?.Generation.Value);

        var replacement = rig.Launch.Processes[2];
        var invoke = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(32),
            Deadline,
            scratch,
            new SessionTransitionVersion(1),
            new GuardianHostWorkerIdentity(
                new WorkerBootId(replacement.WorkerBootId),
                new WorkerGeneration(replacement.Generation)),
            null,
            new JobListOperation(Call(32), Dispatch(32)));
        var invokeOutcome = await rig.Runtime.ExecuteOperationAsync(
            invoke,
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(invokeOutcome.Result);
    }

    [Fact]
    public async Task Terminal_releases_the_outstanding_slot_and_keeps_completed_output_authorized()
    {
        var rig = new RuntimeRig(generations: [9]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var worker = rig.Runtime.WorkerIdentity!;
        var capabilities = new Dictionary<long, CapabilityToken>();
        for (var index = 0; index < 64; index++)
        {
            var jobId = 71L + index;
            capabilities[jobId] = (await StartBackgroundAsync(
                    rig,
                    worker,
                    30 + index,
                    jobId))
                .JobCapability;
        }
        Assert.Equal(64, rig.Runtime.OutstandingJobCapabilityCount);

        var busy = await rig.Runtime.ExecuteOperationAsync(
            BackgroundRequest(94, 135, worker),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.SessionBusy,
            busy.Error?.DetailCode);

        await DeliverJobTerminalAsync(rig, worker, 30, 71);
        Assert.Equal(63, rig.Runtime.OutstandingJobCapabilityCount);
        capabilities[135] = (await StartBackgroundAsync(rig, worker, 94, 135))
            .JobCapability;
        Assert.Equal(64, rig.Runtime.OutstandingJobCapabilityCount);

        var completedOutput = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(95),
                Deadline,
                new CanonicalAlias("default"),
                Transition,
                worker,
                null,
                new JobOutputOperation(
                    Call(95),
                    Dispatch(95),
                    Output(95),
                    new PublicJobId(71),
                    capabilities[71],
                    offset: 0)),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobOutputResult>(completedOutput.Result);

        var outstandingOutput = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(96),
                Deadline,
                new CanonicalAlias("default"),
                Transition,
                worker,
                null,
                new JobOutputOperation(
                    Call(96),
                    Dispatch(96),
                    Output(96),
                    new PublicJobId(72),
                    capabilities[72],
                    offset: 0)),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobOutputResult>(outstandingOutput.Result);

        var bogus = await rig.Runtime.ExecuteOperationAsync(
            new OperationRequest(
                Guardian,
                Host,
                HostGeneration,
                new PrivateRequestId(97),
                Deadline,
                new CanonicalAlias("default"),
                Transition,
                worker,
                null,
                new JobOutputOperation(
                    Call(97),
                    Dispatch(97),
                    Output(97),
                    new PublicJobId(999),
                    capabilities[71],
                    offset: 0)),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.JobCapabilityInvalid,
            bogus.Error?.DetailCode);
    }

    [Fact]
    public async Task The_oldest_completed_capability_is_evicted_past_the_bound()
    {
        var rig = new RuntimeRig(generations: [9]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var worker = rig.Runtime.WorkerIdentity!;
        var capabilities = new Dictionary<long, CapabilityToken>();
        for (var index = 0; index < 64; index++)
        {
            var jobId = 71L + index;
            capabilities[jobId] = (await StartBackgroundAsync(
                    rig,
                    worker,
                    30 + index,
                    jobId))
                .JobCapability;
        }
        for (var index = 0; index < 64; index++)
        {
            await DeliverJobTerminalAsync(rig, worker, 30 + index, 71L + index);
        }
        Assert.Equal(0, rig.Runtime.OutstandingJobCapabilityCount);

        capabilities[135] = (await StartBackgroundAsync(rig, worker, 94, 135))
            .JobCapability;
        await DeliverJobTerminalAsync(rig, worker, 94, 135);

        var evicted = await rig.Runtime.ExecuteOperationAsync(
            JobOutputRequest(95, 71, capabilities[71], worker),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.JobCapabilityInvalid,
            evicted.Error?.DetailCode);
        var retained = await rig.Runtime.ExecuteOperationAsync(
            JobOutputRequest(96, 134, capabilities[134], worker),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobOutputResult>(retained.Result);
        var newest = await rig.Runtime.ExecuteOperationAsync(
            JobOutputRequest(97, 135, capabilities[135], worker),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobOutputResult>(newest.Result);
    }

    private static OperationRequest BackgroundRequest(
        long requestId,
        long publicJobId,
        GuardianHostWorkerIdentity worker) => new(
        Guardian,
        Host,
        HostGeneration,
        new PrivateRequestId(requestId),
        Deadline,
        new CanonicalAlias("default"),
        Transition,
        worker,
        new GuardianHostOperationIdentity(
            Plan(requestId),
            new OperationId(GuidFrom(requestId, version: 4))),
        new InvokeBackgroundOperation(
            Call(requestId),
            Dispatch(requestId),
            Output(requestId),
            "Get-Process",
            raw: false,
            GuardianHostInvokeRoute.Pwsh,
            new PublicJobId(publicJobId)));

    private static OperationRequest JobOutputRequest(
        long requestId,
        long publicJobId,
        CapabilityToken jobCapability,
        GuardianHostWorkerIdentity worker) => new(
        Guardian,
        Host,
        HostGeneration,
        new PrivateRequestId(requestId),
        Deadline,
        new CanonicalAlias("default"),
        Transition,
        worker,
        null,
        new JobOutputOperation(
            Call(requestId),
            Dispatch(requestId),
            Output(requestId),
            new PublicJobId(publicJobId),
            jobCapability,
            offset: 0));

    /// <summary>
    /// The guardian mints an output capability for every background invoke and
    /// then waits for a seal that only the job's terminal can produce. This
    /// pins the wiring end of that contract inside the runtime: the capture is
    /// retained across the job's life and sealed with the worker's own job
    /// output once the terminal arrives (r6x-2 #3).
    /// </summary>
    [Fact]
    public async Task Job_terminal_seals_the_retained_background_output_capture()
    {
        var rig = new RuntimeRig(generations: [9]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var worker = rig.Runtime.WorkerIdentity!;

        _ = await StartBackgroundAsync(rig, worker, 30, 71);
        var capture = Assert.Single(rig.Output.Captures);
        Assert.False(capture.Sealed.IsCompleted);

        await DeliverJobTerminalAsync(rig, worker, 30, 71);

        var content = await capture.Sealed.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.StartsWith("output", content.StandardOutput, StringComparison.Ordinal);
        Assert.Null(capture.IncompleteReason);
    }

    /// <summary>
    /// The worker bounds one output read, so a page is not necessarily the
    /// whole spool. Sealing a bounded page as complete advertised a complete
    /// artifact that silently omitted the rest (r6x-2 #3, raised in review).
    /// </summary>
    [Fact]
    public async Task Bounded_job_output_seals_incomplete_rather_than_claiming_the_whole_spool()
    {
        var rig = new RuntimeRig(generations: [9]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var worker = rig.Runtime.WorkerIdentity!;
        // One page of 6 bytes against a 24-byte spool: the first read is a
        // prefix and three quarters of the output is behind it.
        var process = rig.Launch.Processes.Single();
        process.JobOutputSpoolLength = 24;
        process.JobOutputPageBytes = 6;

        _ = await StartBackgroundAsync(rig, worker, 30, 71);
        var capture = Assert.Single(rig.Output.Captures);
        await DeliverJobTerminalAsync(rig, worker, 30, 71);

        _ = await capture.Sealed.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.Equal("job_output_truncated", capture.IncompleteReason);
    }

    private static async Task<InvokeBackgroundResult> StartBackgroundAsync(
        RuntimeRig rig,
        GuardianHostWorkerIdentity worker,
        long requestId,
        long publicJobId)
    {
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            BackgroundRequest(requestId, publicJobId, worker),
            TestContext.Current.CancellationToken);
        return Assert.IsType<InvokeBackgroundResult>(outcome.Result);
    }

    private static async Task DeliverJobTerminalAsync(
        RuntimeRig rig,
        GuardianHostWorkerIdentity worker,
        long requestId,
        long publicJobId)
    {
        var descriptor = rig.Launch.Processes
            .SelectMany(process => process.PreparedDescriptors)
            .Single(value => value.PlanId == Plan(requestId).Value);
        var envelope = new WorkerEnvelope(
            WorkerProtocol.Version,
            WorkerMessageKind.Event,
            worker.BootId.Value,
            RequestId: null,
            JsonSerializer.SerializeToElement(new
            {
                @event = "job_terminal",
                generation = worker.Generation.Value,
                planId = descriptor.PlanId.ToString("D"),
                descriptorDigest =
                    WorkerPreparedOperationCodec.ComputePreparedDescriptorDigest(
                        descriptor),
                publicJobId,
                state = "completed",
                exitCode = (int?)0,
                outputState = "sealed",
                outputBytes = 1,
                outputDigest = (string?)null,
            }));
        await rig.WorkerEvents.HandleAsync(
            envelope,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Close_on_the_default_alias_is_refused()
    {        var rig = new RuntimeRig(generations: [9]);
        await rig.Runtime.InitializeAsync(
            Initialization(highWatermark: 8),
            TestContext.Current.CancellationToken);
        var worker = rig.Runtime.WorkerIdentity!;

        var close = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(31),
            Deadline,
            new CanonicalAlias("default"),
            Transition,
            worker,
            null,
            new SessionCloseOperation(
                Call(31),
                Dispatch(31),
                expectedGeneration: 9,
                force: false));
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            close,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            GuardianHostPrivateDetailCode.UnsupportedOperation,
            outcome.Error?.DetailCode);

        var jobList = await rig.Runtime.ExecuteOperationAsync(
            Request(32, worker, new JobListOperation(Call(32), Dispatch(32))),
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(jobList.Result);
        Assert.False(rig.Launch.Processes[0].Shutdown);
        Assert.DoesNotContain(
            rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>(),
            lifecycle => lifecycle.State is PublicSessionState.Cold or
                PublicSessionState.Faulted);
    }

    [Fact]
    public async Task Initialization_rejects_template_bindings_until_the_template_slice()
    {
        var bootstrapBytes = new byte[] { 0x01 };
        var bootstrapDigest = Sha256Digest.Compute(bootstrapBytes);
        var defaultBinding = new RecoveryBinding(
            Alias,
            RecoveryBindingKind.Default,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground: true,
            DesiredSessionState.Ready,
            Transition,
            Digest('a'));
        var templateBinding = new RecoveryBinding(
            new CanonicalAlias("scratch"),
            RecoveryBindingKind.Template,
            new CanonicalAlias("template-one"),
            Digest('e'),
            bootstrapDigest,
            allowColdBackground: true,
            DesiredSessionState.Cold,
            new SessionTransitionVersion(1),
            Digest('d'));
        var manifest = new RecoveryManifest(
            Guardian,
            HostGeneration,
            Digest('b'),
            Digest('c'),
            [
                new RecoveryTemplate(
                    new CanonicalAlias("template-one"),
                    "description",
                    30,
                    "target",
                    "identity",
                    allowColdBackground: true,
                    Digest('e'),
                    bootstrapDigest,
                    bootstrapBytes),
            ],
            [defaultBinding, templateBinding],
            [
                new WorkerGenerationHighWatermarkEntry(
                    Alias,
                    new WorkerGenerationHighWatermark(8)),
                new WorkerGenerationHighWatermarkEntry(
                    templateBinding.Alias,
                    new WorkerGenerationHighWatermark(1)),
            ],
            HostGeneration);
        var initialization = new PrivateHostInitialization(
            manifest,
            new PrivateRequestId(1),
            new ManifestId(
                Guid.Parse("11111111-1111-4111-8111-111111111111")),
            Sha256Digest.Compute(RecoveryManifestCodec.Encode(manifest)));
        var rig = new RuntimeRig(generations: [9]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            rig.Runtime.InitializeAsync(
                initialization,
                TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(WorkerPrivateHostRuntimeState.Faulted, rig.Runtime.State);
        Assert.Empty(rig.Launch.Processes);
    }

    [Fact]
    public async Task Initialization_restores_a_ready_dynamic_binding_and_serves_both_aliases()
    {
        var defaultBinding = new RecoveryBinding(
            Alias,
            RecoveryBindingKind.Default,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground: true,
            DesiredSessionState.Ready,
            Transition,
            Digest('a'));
        var dynamicAlias = new CanonicalAlias("scratch");
        var dynamicBinding = new RecoveryBinding(
            dynamicAlias,
            RecoveryBindingKind.Dynamic,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground: true,
            DesiredSessionState.Ready,
            new SessionTransitionVersion(1),
            Digest('d'));
        var manifest = new RecoveryManifest(
            Guardian,
            HostGeneration,
            Digest('b'),
            Digest('c'),
            [],
            [defaultBinding, dynamicBinding],
            [
                new WorkerGenerationHighWatermarkEntry(
                    Alias,
                    new WorkerGenerationHighWatermark(8)),
                new WorkerGenerationHighWatermarkEntry(
                    dynamicAlias,
                    new WorkerGenerationHighWatermark(1)),
            ],
            HostGeneration);
        var initialization = new PrivateHostInitialization(
            manifest,
            new PrivateRequestId(1),
            new ManifestId(
                Guid.Parse("11111111-1111-4111-8111-111111111111")),
            Sha256Digest.Compute(RecoveryManifestCodec.Encode(manifest)));
        var rig = new RuntimeRig(generations: [9, 10]);

        await rig.Runtime.InitializeAsync(
            initialization,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, rig.Launch.Processes.Count);
        Assert.Equal(
            ["launch:9", "launch:10"],
            rig.Launch.Order);
        var lifecycles = rig.Events.SnapshotEvents().OfType<SessionLifecycleEvent>().ToArray();
        Assert.Equal(2, lifecycles.Length);
        Assert.Equal(Alias, lifecycles[0].SessionAlias);
        Assert.Equal(dynamicAlias, lifecycles[1].SessionAlias);
        Assert.All(
            lifecycles,
            lifecycle => Assert.Equal(
                GuardianHostSessionLifecycleReason.AutomaticRecovery,
                lifecycle.Reason));

        var dynamicWorker = new GuardianHostWorkerIdentity(
            new WorkerBootId(rig.Launch.Processes[1].WorkerBootId),
            new WorkerGeneration(rig.Launch.Processes[1].Generation));
        var invoke = new OperationRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(20),
            Deadline,
            dynamicAlias,
            new SessionTransitionVersion(1),
            dynamicWorker,
            null,
            new JobListOperation(Call(20), Dispatch(20)));
        var outcome = await rig.Runtime.ExecuteOperationAsync(
            invoke,
            TestContext.Current.CancellationToken);
        Assert.IsType<JobListResult>(outcome.Result);
        Assert.Equal(
            WorkerSessionOperationCodec.JobListOperation,
            Assert.Single(rig.Launch.Processes[1].OrdinaryOperations));
    }

    private static PrivateHostInitialization Initialization(
        long highWatermark)
    {
        var binding = new RecoveryBinding(
            Alias,
            RecoveryBindingKind.Default,
            templateName: null,
            templateDigest: null,
            bootstrapDigest: null,
            allowColdBackground: true,
            DesiredSessionState.Ready,
            Transition,
            Digest('a'));
        var manifest = new RecoveryManifest(
            Guardian,
            HostGeneration,
            Digest('b'),
            Digest('c'),
            [],
            [binding],
            [
                new WorkerGenerationHighWatermarkEntry(
                    Alias,
                    new WorkerGenerationHighWatermark(highWatermark)),
            ],
            HostGeneration);
        return new PrivateHostInitialization(
            manifest,
            new PrivateRequestId(1),
            new ManifestId(
                Guid.Parse("11111111-1111-4111-8111-111111111111")),
            Sha256Digest.Compute(RecoveryManifestCodec.Encode(manifest)));
    }

    private static OperationRequest Request(
        long requestId,
        GuardianHostWorkerIdentity worker,
        GuardianHostOperation operation) => new(
        Guardian,
        Host,
        HostGeneration,
        new PrivateRequestId(requestId),
        Deadline,
        Alias,
        Transition,
        worker,
        operation is InvokeForegroundOperation or InvokeBackgroundOperation
            ? new GuardianHostOperationIdentity(
                Plan(requestId),
                new OperationId(GuidFrom(requestId, version: 4)))
            : null,
        operation);

    private static GuardianHostShutdown Shutdown() => new(
        Guardian,
        Host,
        HostGeneration,
        new PrivateRequestId(99),
        Deadline,
        GuardianHostShutdownReason.GuardianShutdown);

    private static CallId Call(long value) => new(
        GuidFrom(value, version: 7));

    private static PlanId Plan(long value) => new(
        GuidFrom(value, version: 4));

    private static Guid GuidFrom(long value, int version)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        bytes[7] = (byte)(version << 4);
        bytes[8] = 0x80;
        return new Guid(bytes);
    }

    private static DispatchCapability Dispatch(long value) => new(
        Token((byte)(value + 1)),
        Call(value),
        Deadline);

    private static OutputCapability Output(long value) => new(
        Token((byte)(value + 32)),
        1024,
        Deadline);

    private static Sha256Digest Digest(char value) =>
        new(new string(value, 64));

    private static CapabilityToken Token(byte value)
    {
        var bytes = Enumerable.Repeat(
                value,
                ContractLimits.CapabilityTokenBytes)
            .ToArray();
        return new CapabilityToken(
            Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'));
    }

    private sealed class RuntimeRig
    {
        internal RuntimeRig(IReadOnlyList<long> generations, TimeSpan? stabilityWindow = null)
        {
            Events = new RecordingHostChannel();
            Output = new RecordingOutputTransfer();
            Launch = new RecordingLaunchAuthority();
            var capabilities = new SequencedCapabilitySource(generations);
            var bootIds = new Queue<WorkerBootId>(
                generations.Select(value => new WorkerBootId(
                    GuidFrom(100 + value, version: 4))));
            var slots = new PrivateHostWorkerSlotFactory(
                capabilities,
                Launch,
                workerBootId: () => bootIds.Dequeue(),
                utcNow: () => DateTimeOffset.FromUnixTimeMilliseconds(1));
            var workerEvents = new PrivateHostWorkerEventBridge(
                Identity,
                Events);
            WorkerEvents = workerEvents;
            var authorizer = new PrivateHostPreparedDispatchAuthorizer(
                Identity,
                Events,
                unixTimeMilliseconds: () => 1);
            var prepared = new PrivateHostPreparedInvokeDispatcher(
                Identity,
                Events,
                authorizer,
                workerEvents);
            Runtime = new WorkerPrivateHostRuntime(
                Identity,
                Events,
                slots,
                prepared,
                workerEvents,
                Output,
                createJobCapability: () => Token(0x55),
                unixTimeMilliseconds: () => 1,
                stabilityWindow: stabilityWindow);
        }

        internal RecordingHostChannel Events { get; }
        internal RecordingOutputTransfer Output { get; }
        internal RecordingLaunchAuthority Launch { get; }
        internal WorkerPrivateHostRuntime Runtime { get; }
        internal PrivateHostWorkerEventBridge WorkerEvents { get; }
    }

    private sealed class SequencedCapabilitySource(
        IReadOnlyList<long> generations) :
        IPrivateHostWorkerCreateCapabilitySource
    {
        private readonly Queue<long> _generations = new(generations);

        public ValueTask<PrivateHostWorkerCreateCapability> RequestAsync(
            RecoveryBinding binding,
            WorkerGenerationHighWatermark generationHighWatermark,
            long startupDeadlineUnixTimeMilliseconds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = _generations.Dequeue();
            Assert.True(generation > generationHighWatermark.Value);
            return ValueTask.FromResult(
                new PrivateHostWorkerCreateCapability(
                    new WorkerGeneration(generation),
                    Token((byte)generation),
                    startupDeadlineUnixTimeMilliseconds,
                    () => 1));
        }
    }

    private sealed class RecordingLaunchAuthority :
        IPrivateHostWorkerLaunchAuthority
    {
        internal List<RecordingProcessClient> Processes { get; } = [];
        internal List<string> Order { get; } = [];
        internal bool FailNextLaunch { get; set; }
        internal TaskCompletionSource? BlockNextLaunch { get; set; }
        internal Action<int>? OnLaunchCall { get; set; }
        internal int LaunchCalls { get; private set; }

        public async Task<IWorkerProcessClient> LaunchAsync(
            RecoveryBinding binding,
            GuardianHostWorkerIdentity workerIdentity,
            DateTimeOffset deadlineUtc,
            Func<WorkerEnvelope, CancellationToken, ValueTask>? onEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(onEvent);
            if (FailNextLaunch)
            {
                FailNextLaunch = false;
                throw new InvalidOperationException(
                    "Worker launch failed as scripted.");
            }
            LaunchCalls++;
            if (BlockNextLaunch is { } gate)
            {
                BlockNextLaunch = null;
                await gate.Task.WaitAsync(cancellationToken);
            }
            OnLaunchCall?.Invoke(LaunchCalls);
            Order.Add($"launch:{workerIdentity.Generation.Value}");
            var process = new RecordingProcessClient(workerIdentity, Order);
            Processes.Add(process);
            return process;
        }
    }

    private sealed class RecordingProcessClient(
        GuardianHostWorkerIdentity worker,
        List<string> order) : IWorkerProcessClient
    {
        private long _requestId = 40;
        private readonly TaskCompletionSource _fatal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 42;
        public Guid WorkerBootId => worker.BootId.Value;
        public long Generation => worker.Generation.Value;
        public Task Fatal => _fatal.Task;
        public Task<WorkerDiagnosticReport> Diagnostics =>
            Task.FromResult(new WorkerDiagnosticReport(
                new WorkerDiagnosticSummary(0, 0, false, Digest('0').Value),
                new WorkerDiagnosticSummary(0, 0, false, Digest('0').Value)));

        internal void Kill() =>
            _fatal.TrySetException(new InvalidOperationException(
                "Worker died as scripted."));
        internal List<WorkerPreparedInvokeKind> PreparedKinds { get; } = [];
        internal List<WorkerPreparedPlanDescriptor> PreparedDescriptors { get; } = [];
        internal List<string> OrdinaryOperations { get; } = [];
        private long? _preparedPublicJobId;
        internal bool Shutdown { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool FailShutdown { get; set; }

        /// <summary>
        /// Makes the next ordinary operation return its execution-timeout
        /// terminal, exactly as a worker that blew its deadline would.
        /// </summary>
        internal bool TimeOutNextOperation { get; set; }

        /// <summary>Total bytes the fake job's spool holds. Set it at or below
        /// one page for a complete artifact, above it for a bounded one.</summary>
        internal long JobOutputSpoolLength { get; set; } = 6;

        internal long JobOutputPageBytes { get; set; } = 6;

        public async Task<WorkerOperationResponse> ExecuteAsync(
            string operation,
            WorkerSessionOperationArguments arguments,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OrdinaryOperations.Add(operation);
            var requestId = ++_requestId;
            if (beforeWrite is not null)
                await beforeWrite(requestId, cancellationToken);
            if (TimeOutNextOperation)
            {
                TimeOutNextOperation = false;
                return WorkerOperationResponse.TimedOut(
                    requestId,
                    Generation,
                    "request_deadline_expired");
            }
            string JobOutputPage(long offset)
            {
                var next = Math.Min(
                    offset + JobOutputPageBytes,
                    JobOutputSpoolLength);
                return offset >= JobOutputSpoolLength
                    ? $"(no new output)\n[job 71 exited 0] next offset: {next}"
                    : $"output\n[job 71 exited 0] next offset: {next}";
            }

            WorkerSessionOperationResult result = operation switch
            {
                WorkerSessionOperationCodec.JobListOperation =>
                    new WorkerJobListResult("jobs"),
                WorkerSessionOperationCodec.JobStatusOperation =>
                    new WorkerJobStatusResult("status"),
                // A real job-output page always ends with `next offset: N`
                // (SessionRuntime's "output" case). The runtime uses that to
                // decide whether the page is the whole spool, so the fake has
                // to carry it or the completeness logic cannot be tested.
                // JobOutputSpoolLength controls the fixture: a page ending at
                // the spool length is the whole output, and a shorter one is a
                // bounded page with more behind it.
                WorkerSessionOperationCodec.JobOutputOperation =>
                    new WorkerJobOutputResult(JobOutputPage(
                        ((WorkerJobOutputArguments)arguments).Offset)),
                WorkerSessionOperationCodec.JobKillOperation =>
                    new WorkerJobKillResult("kill"),
                _ => throw new InvalidOperationException(
                    "Unexpected ordinary worker operation."),
            };
            return WorkerOperationResponse.Completed(
                requestId,
                Generation,
                WorkerSessionOperationCodec.CreateResult(operation, result));
        }

        public Task<WorkerPreparedPlanDescriptor> PrepareAsync(
            WorkerInvokePreparePayload prepare,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparedKinds.Add(prepare.Kind);
            _preparedPublicJobId = prepare.PublicJobId;
            var descriptor = new WorkerPreparedPlanDescriptor(
                prepare.PlanId,
                WorkerBootId,
                prepare.ScriptDigest,
                Generation,
                prepare.DeadlineUtc,
                ExecutionDomain.PowerShell,
                prepare.Arguments.Route switch
                {
                    WorkerInvokeRoute.Pwsh =>
                        RequestedExecutionRoute.PowerShell,
                    WorkerInvokeRoute.Rtk =>
                        RequestedExecutionRoute.Rtk,
                    _ => RequestedExecutionRoute.Auto,
                },
                ExecutionPath.PowerShellDirect,
                PreExecutionValidation.None,
                prepare.Kind == WorkerPreparedInvokeKind.Foreground
                    ? ResolutionContext.Warm
                    : ResolutionContext.Cold,
                OutputProvenance.PowerShellObjects,
                ImmutableArray<ExecutionPath>.Empty,
                FallbackReason: null,
                WorkingDirectoryDigest: null,
                RtkBinaryDigest: null,
                BashBinaryDigest: null,
                OutputShapingRtkBinaryDigest: null);
            PreparedDescriptors.Add(descriptor);
            return Task.FromResult(descriptor);
        }

        public async Task<WorkerOperationResponse> CommitAsync(
            WorkerCommitPayload commit,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestId = ++_requestId;
            if (beforeWrite is not null)
                await beforeWrite(requestId, cancellationToken);
            var background = PreparedKinds.Last() ==
                WorkerPreparedInvokeKind.Background;
            return WorkerOperationResponse.Completed(
                requestId,
                Generation,
                background
                    ? JsonSerializer.SerializeToElement(new
                    {
                        text = $"Job {_preparedPublicJobId ?? 71} started.",
                        publicJobId = _preparedPublicJobId ?? 71,
                        started = true,
                    })
                    : WorkerSessionOperationCodec.CreateResult(
                        WorkerSessionOperationCodec.InvokeOperation,
                        new WorkerInvokeResult("foreground")));
        }

        public Task<WorkerOperationResponse> AbortAsync(
            WorkerAbortPayload abort,
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null) =>
            Task.FromResult(WorkerOperationResponse.Canceled(
                ++_requestId,
                Generation,
                "operation_aborted"));

        public async Task ShutdownAsync(
            CancellationToken cancellationToken = default,
            Func<long, CancellationToken, ValueTask>? beforeWrite = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailShutdown)
                throw new InvalidOperationException(
                    "Worker shutdown failed as scripted.");
            var requestId = ++_requestId;
            if (beforeWrite is not null)
                await beforeWrite(requestId, cancellationToken);
            Shutdown = true;
            order.Add($"shutdown:{Generation}");
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            // The real client's DisposeAsync contains the tree and awaits the
            // monitor that completes Fatal, so disposal is observable as a
            // confirmed death. Modelling that here is what lets containment
            // converge on the same death watch the fake previously hid.
            _fatal.TrySetException(new InvalidOperationException(
                "Worker was contained."));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingHostChannel :
        IPrivateHostEventSink,
        IPrivateHostControlEventSink
    {
        private long _sequence;

        internal List<GuardianHostEvent> Events { get; } = [];
        internal bool FailNextTerminalDecodedDelivery { get; set; }

        internal GuardianHostEvent[] SnapshotEvents()
        {
            lock (Events) return Events.ToArray();
        }

        public ValueTask WriteEventAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hostEvent = createEvent(new HostEventSequence(++_sequence));
            if (FailNextTerminalDecodedDelivery &&
                hostEvent is OperationDeliveryEvent
                {
                    DeliveryState: GuardianHostDeliveryState.TerminalDecoded,
                })
            {
                FailNextTerminalDecodedDelivery = false;
                throw new InvalidOperationException(
                    "Outbound channel failed as scripted.");
            }
            lock (Events) Events.Add(hostEvent);
            return ValueTask.CompletedTask;
        }

        public Task<GuardianHostRequest> ExchangeControlAsync(
            Func<HostEventSequence, GuardianHostEvent> createEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source =
                Assert.IsType<PreparedDispatchAuthorizationRequestedEvent>(
                    createEvent(new HostEventSequence(++_sequence)));
            Events.Add(source);
            return Task.FromResult<GuardianHostRequest>(
                new PreparedDispatchAuthorizeRequest(
                    Guardian,
                    Host,
                    HostGeneration,
                    new PrivateRequestId(900 + _sequence),
                    source.Descriptor.DeadlineUnixTimeMilliseconds,
                    source.SessionAlias,
                    source.SessionTransitionVersion,
                    source.WorkerIdentity!,
                    source.OperationIdentity!,
                    source.EventSequence,
                    source.Descriptor.DescriptorDigest));
        }
    }

    private sealed class RecordingOutputTransfer : IPrivateHostOutputTransfer
    {
        internal List<(OperationRequest Request, string Text)> Transfers { get; } =
            [];

        internal List<RecordingCaptureOwner> Captures { get; } = [];

        // The real transfer is event-based, not in-process: it emits the
        // guardian's output chunk/seal events from the private host, and a
        // background job's artifact is sealed through exactly this owner. The
        // fake this replaced threw "Worker runtime cannot create an in-process
        // capture owner", which codified the very gap that left every
        // background job reporting recovery=unavailable (r6x-2 #3).
        public IExecutionOutputCaptureOwner CreateExecutionCapture(
            OperationRequest request)
        {
            var owner = new RecordingCaptureOwner(request);
            lock (Captures) Captures.Add(owner);
            return owner;
        }

        public ValueTask TransferTextAsync(
            OperationRequest request,
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Transfers.Add((request, text));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCaptureOwner(OperationRequest request)
        : IExecutionOutputCaptureOwner
    {
        private readonly TaskCompletionSource<OutputArtifactContent> _sealed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        internal OperationRequest Request { get; } = request;
        internal Task<OutputArtifactContent> Sealed => _sealed.Task;
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;

        public long MaximumArtifactBytes => 1L << 20;

        public Task<OutputCapturePreparation> PrepareAsync(
            DateTimeOffset absoluteDeadlineUtc,
            TimeSpan maximumWait,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OutputCapturePreparation.Pending());
        }

        public Task<OutputRecoverySummary> SealAsync(
            OutputArtifactContent content,
            TimeSpan maximumWait)
        {
            _sealed.TrySetResult(content);
            return Task.FromResult(new OutputRecoverySummary(
                "ptko_" + new string('a', ContractLimits.CapabilityTokenCharacters),
                OutputArtifactState.Available,
                content.StandardOutput.Length,
                DetailCode: null,
                Advertise: true));
        }

        /// <summary>Null when the artifact was sealed complete; otherwise the
        /// reason it was sealed incomplete. This is the only way to tell the
        /// two apart — `SealIncompleteAsync` does not mutate the content, so
        /// asserting on `content.Complete` cannot distinguish them.</summary>
        internal string? IncompleteReason { get; private set; }

        public Task<OutputRecoverySummary> SealIncompleteAsync(
            OutputArtifactContent content,
            string reason,
            TimeSpan maximumWait)
        {
            IncompleteReason = reason;
            return SealAsync(content, maximumWait);
        }

        public bool TryTransferToBackground(out IExecutionOutputCapture? capture)
        {
            capture = this;
            return true;
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}

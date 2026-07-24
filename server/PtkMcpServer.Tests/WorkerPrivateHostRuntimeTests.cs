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
        Assert.Equal(
            "output",
            Assert.IsType<JobOutputResult>(jobOutcomes[2].Result).Text);
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
                Assert.Equal("output", transfer.Text);
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
            rig.Events.Events.OfType<SessionLifecycleEvent>(),
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
            rig.Events.Events.Last());
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
            rig.Events.Events.Last());
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

        var fault = rig.Events.Events.OfType<SessionLifecycleEvent>().Last();
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

        var fault = rig.Events.Events.OfType<SessionLifecycleEvent>().Last();
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

        var fault = rig.Events.Events.OfType<SessionLifecycleEvent>().Last();
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
            rig.Events.Events.OfType<SessionLifecycleEvent>(),
            lifecycle => lifecycle.State == PublicSessionState.Faulted);
        var ready = rig.Events.Events.OfType<SessionLifecycleEvent>().Last();
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
            rig.Events.Events.OfType<SessionLifecycleEvent>(),
            lifecycle => lifecycle.State == PublicSessionState.Faulted);
        var cold = rig.Events.Events.OfType<SessionLifecycleEvent>().Last();
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
        var lifecycles = rig.Events.Events.OfType<SessionLifecycleEvent>().ToArray();
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
        internal RuntimeRig(IReadOnlyList<long> generations)
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
                unixTimeMilliseconds: () => 1);
        }

        internal RecordingHostChannel Events { get; }
        internal RecordingOutputTransfer Output { get; }
        internal RecordingLaunchAuthority Launch { get; }
        internal WorkerPrivateHostRuntime Runtime { get; }
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

        public Task<IWorkerProcessClient> LaunchAsync(
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
            Order.Add($"launch:{workerIdentity.Generation.Value}");
            var process = new RecordingProcessClient(workerIdentity, Order);
            Processes.Add(process);
            return Task.FromResult<IWorkerProcessClient>(process);
        }
    }

    private sealed class RecordingProcessClient(
        GuardianHostWorkerIdentity worker,
        List<string> order) : IWorkerProcessClient
    {
        private long _requestId = 40;

        public int ProcessId => 42;
        public Guid WorkerBootId => worker.BootId.Value;
        public long Generation => worker.Generation.Value;
        public Task Fatal => Task.Delay(Timeout.InfiniteTimeSpan);
        public Task<WorkerDiagnosticReport> Diagnostics =>
            Task.FromResult(new WorkerDiagnosticReport(
                new WorkerDiagnosticSummary(0, 0, false, Digest('0').Value),
                new WorkerDiagnosticSummary(0, 0, false, Digest('0').Value)));
        internal List<WorkerPreparedInvokeKind> PreparedKinds { get; } = [];
        internal List<string> OrdinaryOperations { get; } = [];
        internal bool Shutdown { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool FailShutdown { get; set; }

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
            WorkerSessionOperationResult result = operation switch
            {
                WorkerSessionOperationCodec.JobListOperation =>
                    new WorkerJobListResult("jobs"),
                WorkerSessionOperationCodec.JobStatusOperation =>
                    new WorkerJobStatusResult("status"),
                WorkerSessionOperationCodec.JobOutputOperation =>
                    new WorkerJobOutputResult("output"),
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
            return Task.FromResult(new WorkerPreparedPlanDescriptor(
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
                OutputShapingRtkBinaryDigest: null));
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
                        text = "Job 71 started.",
                        publicJobId = 71,
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
            Events.Add(hostEvent);
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

        public IExecutionOutputCaptureOwner CreateExecutionCapture(
            OperationRequest request) =>
            throw new InvalidOperationException(
                "Worker runtime cannot create an in-process capture owner.");

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
}

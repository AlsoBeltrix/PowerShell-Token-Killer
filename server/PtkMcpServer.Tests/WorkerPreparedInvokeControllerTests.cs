using System.Collections.Immutable;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerPreparedInvokeControllerTests
{
    private static readonly Guid PlanId =
        Guid.ParseExact("12345678-1234-4234-9234-123456789abc", "D");
    private static readonly Guid BootId =
        Guid.ParseExact("87654321-4321-4321-8321-cba987654321", "D");
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(1_900_000_000_000);
    private static readonly DateTimeOffset Deadline = Now.AddMinutes(1);
    private const string Script = "x";
    private const string ScriptDigest =
        "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881";

    [Fact]
    public async Task Prepare_holds_execution_and_duplicate_commit_returns_the_same_terminal()
    {
        var runtime = new RecordingPreparedRuntime(Plan());
        var controller = Controller(runtime);

        var descriptor = await controller.PrepareAsync(
            Prepare(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, runtime.ExecutionCount);
        Assert.Equal(PlanId, descriptor.PlanId);
        var commit = Commit();
        var first = controller.Commit(commit);
        var duplicate = controller.Commit(commit);
        Assert.Same(first, duplicate);

        var terminal = await first.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkerPreparedInvokeTerminalKind.Completed, terminal.Kind);
        Assert.Equal("complete", terminal.Text);
        Assert.Null(terminal.DetailCode);
        Assert.Equal(1, runtime.ExecutionCount);

        controller.Release(PlanId);
        await controller.CancelAndDrainAsync();
    }

    [Fact]
    public async Task Abort_releases_the_reservation_without_starting_user_execution()
    {
        var runtime = new RecordingPreparedRuntime(Plan());
        var controller = Controller(runtime);
        _ = await controller.PrepareAsync(
            Prepare(),
            TestContext.Current.CancellationToken);

        var terminal = await controller.Abort(Abort())
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkerPreparedInvokeTerminalKind.Aborted, terminal.Kind);
        Assert.Equal("prepared_operation_aborted", terminal.DetailCode);
        Assert.Equal(0, runtime.ExecutionCount);
        await controller.CancelAndDrainAsync();
    }

    [Fact]
    public async Task Correlation_mismatch_terminalizes_the_plan_as_replan_required()
    {
        var runtime = new RecordingPreparedRuntime(Plan());
        var controller = Controller(runtime);
        _ = await controller.PrepareAsync(
            Prepare(),
            TestContext.Current.CancellationToken);

        var mismatch = Commit() with { ScriptDigest = new string('0', 64) };
        var first = controller.Commit(mismatch);
        var laterExact = controller.Commit(Commit());

        Assert.Same(first, laterExact);
        var terminal = await first.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkerPreparedInvokeTerminalKind.ReplanRequired, terminal.Kind);
        Assert.Equal("replan_required", terminal.DetailCode);
        Assert.Equal(0, runtime.ExecutionCount);
        await controller.CancelAndDrainAsync();
    }

    [Fact]
    public async Task Deadline_expiry_aborts_a_prepared_plan_without_execution()
    {
        var deadlineReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Now;
        var runtime = new RecordingPreparedRuntime(Plan());
        var controller = Controller(
            runtime,
            utcNow: () => current,
            waitUntilDeadline: (_, cancellationToken) =>
                deadlineReached.Task.WaitAsync(cancellationToken));
        _ = await controller.PrepareAsync(
            Prepare(),
            TestContext.Current.CancellationToken);

        current = Deadline;
        deadlineReached.TrySetResult();
        var terminal = await controller.Commit(Commit())
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkerPreparedInvokeTerminalKind.Expired, terminal.Kind);
        Assert.Equal("prepared_operation_expired", terminal.DetailCode);
        Assert.Equal(0, runtime.ExecutionCount);
        await controller.CancelAndDrainAsync();
    }

    [Fact]
    public async Task Dispatch_that_does_not_belong_to_the_prepared_plan_never_executes()
    {
        var runtime = new RecordingPreparedRuntime(
            Plan(),
            dispatchPlan: Plan());
        var controller = Controller(runtime);
        _ = await controller.PrepareAsync(
            Prepare(),
            TestContext.Current.CancellationToken);

        var terminal = await controller.Commit(Commit())
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkerPreparedInvokeTerminalKind.ReplanRequired, terminal.Kind);
        Assert.Equal(0, runtime.ExecutionCount);
        await controller.CancelAndDrainAsync();
    }

    [Fact]
    public async Task Unknown_commit_plan_id_invalidates_held_reservations()
    {
        var runtime = new RecordingPreparedRuntime(Plan());
        var controller = Controller(runtime);
        _ = await controller.PrepareAsync(
            Prepare(),
            TestContext.Current.CancellationToken);

        var unknown = Commit() with
        {
            PlanId = Guid.ParseExact(
                "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
                "D"),
        };
        var unknownTerminal = await controller.Commit(unknown)
            .WaitAsync(TestContext.Current.CancellationToken);
        var actualTerminal = await controller.Commit(Commit())
            .WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            WorkerPreparedInvokeTerminalKind.ReplanRequired,
            unknownTerminal.Kind);
        Assert.Equal(
            WorkerPreparedInvokeTerminalKind.ReplanRequired,
            actualTerminal.Kind);
        Assert.Equal(0, runtime.ExecutionCount);
        await controller.CancelAndDrainAsync();
    }

    private static WorkerPreparedInvokeController Controller(
        IWorkerPreparedInvokeRuntime runtime,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, CancellationToken, Task>? waitUntilDeadline = null) =>
        new(
            BootId,
            generation: 7,
            runtime,
            new AcceptingPreparedObserver(),
            utcNow: utcNow ?? (() => Now),
            waitUntilDeadline: waitUntilDeadline ??
                ((_, cancellationToken) =>
                    Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)));

    private static WorkerInvokePreparePayload Prepare() => new(
        PlanId,
        7,
        Deadline,
        ScriptDigest,
        new WorkerInvokeArguments(Script, false, WorkerInvokeRoute.Auto));

    private static WorkerCommitPayload Commit() => new(
        PlanId,
        ScriptDigest,
        7,
        Deadline);

    private static WorkerAbortPayload Abort() => new(
        PlanId,
        ScriptDigest,
        7,
        Deadline);

    private static ExecutionPlan Plan() => new(
        Script,
        Script,
        ExecutionDomain.PowerShell,
        ExecutionPath.PowerShellDirect,
        PreExecutionValidation.None,
        ResolutionContext.Warm,
        RequestedExecutionRoute.Auto,
        OutputProvenance.PowerShellObjects,
        ImmutableArray<ExecutionPath>.Empty,
        fallbackReason: null,
        rtkExecutableIdentity: null);

    private sealed class RecordingPreparedRuntime(
        ExecutionPlan preparedPlan,
        ExecutionPlan? dispatchPlan = null) : IWorkerPreparedInvokeRuntime
    {
        private readonly ExecutionPlan _preparedPlan = preparedPlan;
        private readonly ExecutionPlan _dispatchPlan = dispatchPlan ?? preparedPlan;
        private int _executionCount;

        internal int ExecutionCount => Volatile.Read(ref _executionCount);

        public async Task<WorkerPreparedRuntimeResult> InvokeAsync(
            WorkerInvokePreparePayload prepare,
            IInvocationAuthorizer authorizer,
            CancellationToken cancellationToken)
        {
            if (!await authorizer.AuthorizePlanAsync(
                    _preparedPlan,
                    cancellationToken))
            {
                return new WorkerPreparedRuntimeResult(
                    "not started",
                    UserExecutionStarted: false);
            }
            if (!await authorizer.AuthorizeDispatchAsync(
                    ExecutionDispatch.FromPlan(_dispatchPlan),
                    cancellationToken))
            {
                return new WorkerPreparedRuntimeResult(
                    "not started",
                    UserExecutionStarted: false);
            }

            Interlocked.Increment(ref _executionCount);
            return new WorkerPreparedRuntimeResult(
                "complete",
                UserExecutionStarted: true);
        }
    }

    private sealed class AcceptingPreparedObserver : IWorkerPreparedInvokeObserver
    {
        public ValueTask<bool> RecordValidatorStartedAsync(
            WorkerPreparedPlanDescriptor descriptor,
            ExecutionDispatch dispatch,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> RecordValidatorCompletedAsync(
            WorkerPreparedPlanDescriptor descriptor,
            ExecutionDispatch dispatch,
            BashSyntaxValidationResult result,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }
}

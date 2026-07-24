using System.Text;
using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// Owns the effect boundary for one foreground invocation. Preparation is
/// nonexecuting. The worker commit request is reserved first; its pre-write
/// callback then obtains exact guardian authorization and records delivery with
/// that worker request ID. No commit frame can be written before both complete.
/// </summary>
internal sealed class PrivateHostPreparedInvokeDispatcher
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly PrivateHostServerIdentity _identity;
    private readonly IPrivateHostEventSink _events;
    private readonly PrivateHostPreparedDispatchAuthorizer _authorizer;
    private readonly PrivateHostWorkerEventBridge _workerEvents;

    internal PrivateHostPreparedInvokeDispatcher(
        PrivateHostServerIdentity identity,
        IPrivateHostEventSink events,
        PrivateHostPreparedDispatchAuthorizer authorizer,
        PrivateHostWorkerEventBridge workerEvents)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _authorizer = authorizer ??
            throw new ArgumentNullException(nameof(authorizer));
        _workerEvents = workerEvents ??
            throw new ArgumentNullException(nameof(workerEvents));
    }

    internal async ValueTask<WorkerOperationResponse> ExecuteForegroundAsync(
        OperationRequest request,
        PrivateHostWorkerSlot slot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(slot);
        var operation = request.Operation as InvokeForegroundOperation ??
            throw new ArgumentException(
                "Prepared foreground dispatch requires a foreground invocation.",
                nameof(request));
        var operationIdentity = request.OperationIdentity ??
            throw new ArgumentException(
                "Prepared foreground dispatch requires an operation identity.",
                nameof(request));
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(
            request.DeadlineUnixTimeMilliseconds!.Value);
        var prepare = new WorkerInvokePreparePayload(
            operationIdentity.PlanId.Value,
            slot.Identity.Generation.Value,
            deadline,
            Sha256Digest.Compute(
                StrictUtf8.GetBytes(operation.Script)).Value,
            new WorkerInvokeArguments(
                operation.Script,
                operation.Raw,
                MapRoute(operation.Route)));
        return await ExecutePreparedAsync(
                request,
                slot,
                prepare,
                static response => response,
                static (registration, _) => registration.CompleteForeground(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<WorkerBackgroundStartResponse>
        ExecuteBackgroundAsync(
            OperationRequest request,
            PrivateHostWorkerSlot slot,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(slot);
        var operation = request.Operation as InvokeBackgroundOperation ??
            throw new ArgumentException(
                "Prepared background dispatch requires a background invocation.",
                nameof(request));
        var operationIdentity = request.OperationIdentity ??
            throw new ArgumentException(
                "Prepared background dispatch requires an operation identity.",
                nameof(request));
        var deadline = DateTimeOffset.FromUnixTimeMilliseconds(
            request.DeadlineUnixTimeMilliseconds!.Value);
        var prepare = new WorkerInvokePreparePayload(
            operationIdentity.PlanId.Value,
            slot.Identity.Generation.Value,
            deadline,
            Sha256Digest.Compute(
                StrictUtf8.GetBytes(operation.Script)).Value,
            new WorkerInvokeArguments(
                operation.Script,
                operation.Raw,
                MapRoute(operation.Route)),
            WorkerPreparedInvokeKind.Background,
            operation.PublicJobId.Value);
        return await ExecutePreparedAsync(
                request,
                slot,
                prepare,
                response =>
                    WorkerPreparedOperationProtocol.ParseBackgroundStartResult(
                        response,
                        slot.Identity.Generation.Value,
                        operation.PublicJobId.Value),
                static (registration, result) =>
                    registration.CompleteBackgroundStart(result.Started),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<T> ExecutePreparedAsync<T>(
        OperationRequest request,
        PrivateHostWorkerSlot slot,
        WorkerInvokePreparePayload prepare,
        Func<WorkerOperationResponse, T> decode,
        Action<PrivateHostWorkerEventRegistration, T> completeRegistration,
        CancellationToken cancellationToken)
    {
        var prepareAttempted = false;
        var commitWriteStarted = false;
        long? commitRequestId = null;
        PrivateHostWorkerEventRegistration? eventRegistration = null;
        try
        {
            prepareAttempted = true;
            var descriptor = await slot.Process.PrepareAsync(
                    prepare,
                    cancellationToken)
                .ConfigureAwait(false);
            var commit = new WorkerCommitPayload(
                descriptor.PlanId,
                descriptor.ScriptDigest,
                descriptor.Generation,
                descriptor.DeadlineUtc);
            eventRegistration = _workerEvents.Register(
                request,
                slot,
                descriptor);

            var response = await slot.Process.CommitAsync(
                    commit,
                    cancellationToken,
                    async (workerRequestId, token) =>
                    {
                        await _authorizer.AuthorizeAsync(
                                request,
                                slot,
                                descriptor,
                                token)
                            .ConfigureAwait(false);
                        await WriteDeliveryAsync(
                                request,
                                slot.Identity,
                                GuardianHostDeliveryState.WriteStarted,
                                new PrivateRequestId(workerRequestId),
                                token)
                            .ConfigureAwait(false);
                        commitRequestId = workerRequestId;
                        commitWriteStarted = true;
                        eventRegistration.MarkCommitAuthorized();
                    })
                .ConfigureAwait(false);

            if (commitRequestId is null ||
                response.RequestId != commitRequestId.Value)
            {
                throw new InvalidDataException(
                    "Worker commit terminal does not match its reserved request.");
            }

            var decoded = decode(response);
            await WriteDeliveryAsync(
                    request,
                    slot.Identity,
                    GuardianHostDeliveryState.TerminalDecoded,
                    new PrivateRequestId(commitRequestId.Value),
                    cancellationToken)
                .ConfigureAwait(false);
            completeRegistration(eventRegistration, decoded);
            return decoded;
        }
        catch
        {
            if (!commitWriteStarted)
            {
                if (prepareAttempted)
                {
                    await TryAbortAsync(slot, prepare)
                        .ConfigureAwait(false);
                }
                await TryWriteNotDispatchedAsync(request, slot.Identity)
                    .ConfigureAwait(false);
                eventRegistration?.Abandon();
            }
            throw;
        }
    }

    private static WorkerInvokeRoute MapRoute(
        GuardianHostInvokeRoute route) => route switch
    {
        GuardianHostInvokeRoute.Auto => WorkerInvokeRoute.Auto,
        GuardianHostInvokeRoute.Pwsh => WorkerInvokeRoute.Pwsh,
        GuardianHostInvokeRoute.Rtk => WorkerInvokeRoute.Rtk,
        _ => throw new ArgumentException(
            "Prepared invocation route is unsupported.",
            nameof(route)),
    };

    private async ValueTask WriteDeliveryAsync(
        OperationRequest request,
        GuardianHostWorkerIdentity worker,
        GuardianHostDeliveryState state,
        PrivateRequestId? workerRequestId,
        CancellationToken cancellationToken)
    {
        await _events.WriteEventAsync(
                sequence => new OperationDeliveryEvent(
                    _identity.GuardianBootId,
                    _identity.HostBootId,
                    _identity.HostGeneration,
                    sequence,
                    request.RequestId,
                    request.SessionAlias!,
                    request.SessionTransitionVersion!,
                    worker,
                    request.OperationIdentity,
                    request.Operation.DispatchCapability.Token,
                    state,
                    workerRequestId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask TryAbortAsync(
        PrivateHostWorkerSlot slot,
        WorkerInvokePreparePayload prepare)
    {
        try
        {
            _ = await slot.Process.AbortAsync(
                    new WorkerAbortPayload(
                        prepare.PlanId,
                        prepare.ScriptDigest,
                        prepare.Generation,
                        prepare.DeadlineUtc),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private async ValueTask TryWriteNotDispatchedAsync(
        OperationRequest request,
        GuardianHostWorkerIdentity worker)
    {
        try
        {
            await WriteDeliveryAsync(
                    request,
                    worker,
                    GuardianHostDeliveryState.NotDispatched,
                    workerRequestId: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException;
}

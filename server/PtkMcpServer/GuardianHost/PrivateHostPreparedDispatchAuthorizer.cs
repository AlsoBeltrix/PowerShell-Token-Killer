using PtkMcpServer.Worker;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

/// <summary>
/// Projects the worker-owned immutable preparation into the frozen guardian
/// wire contract. Every enum is mapped explicitly so adding a worker routing
/// state cannot silently change the audit representation.
/// </summary>
internal static class PrivateHostPreparedPlanProjection
{
    internal static GuardianHostPreparedPlanDescriptor Project(
        WorkerPreparedPlanDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new GuardianHostPreparedPlanDescriptor(
            new PlanId(descriptor.PlanId),
            new GuardianHostWorkerIdentity(
                new WorkerBootId(descriptor.WorkerBootId),
                new WorkerGeneration(descriptor.Generation)),
            descriptor.DeadlineUtc.ToUnixTimeMilliseconds(),
            new Sha256Digest(descriptor.ScriptDigest),
            descriptor.Domain switch
            {
                null => null,
                ExecutionDomain.PowerShell =>
                    GuardianHostExecutionDomain.PowerShell,
                ExecutionDomain.NativeTerminal =>
                    GuardianHostExecutionDomain.NativeTerminal,
                ExecutionDomain.MixedDataflow =>
                    GuardianHostExecutionDomain.MixedDataflow,
                ExecutionDomain.Bash =>
                    GuardianHostExecutionDomain.Bash,
                _ => throw InvalidDescriptor(),
            },
            descriptor.RequestedRoute switch
            {
                RequestedExecutionRoute.Auto =>
                    GuardianHostRequestedExecutionRoute.Auto,
                RequestedExecutionRoute.PowerShell =>
                    GuardianHostRequestedExecutionRoute.Pwsh,
                RequestedExecutionRoute.Rtk =>
                    GuardianHostRequestedExecutionRoute.Rtk,
                _ => throw InvalidDescriptor(),
            },
            MapEffectiveRoute(descriptor.EffectiveRoute),
            descriptor.PreExecutionValidation switch
            {
                PreExecutionValidation.None =>
                    GuardianHostPreExecutionValidation.None,
                PreExecutionValidation.BashSyntax =>
                    GuardianHostPreExecutionValidation.BashSyntax,
                _ => throw InvalidDescriptor(),
            },
            descriptor.ResolutionContext switch
            {
                ResolutionContext.Warm =>
                    GuardianHostResolutionContext.Warm,
                ResolutionContext.Cold =>
                    GuardianHostResolutionContext.Cold,
                _ => throw InvalidDescriptor(),
            },
            descriptor.OutputProvenance switch
            {
                OutputProvenance.PowerShellObjects =>
                    GuardianHostOutputProvenance.PowerShellObjects,
                OutputProvenance.DirectText =>
                    GuardianHostOutputProvenance.DirectText,
                OutputProvenance.RtkUnknown =>
                    GuardianHostOutputProvenance.RtkUnknown,
                OutputProvenance.RtkFiltered =>
                    GuardianHostOutputProvenance.RtkFiltered,
                OutputProvenance.RtkPassthrough =>
                    GuardianHostOutputProvenance.RtkPassthrough,
                _ => throw InvalidDescriptor(),
            },
            descriptor.PermittedFallbacks
                .Select(MapEffectiveRoute)
                .ToArray(),
            descriptor.FallbackReason switch
            {
                null => null,
                ExecutionFallbackReason.RtkExecutableUnavailable =>
                    GuardianHostExecutionFallbackReason.RtkExecutableUnavailable,
                ExecutionFallbackReason.RtkExecutableBecameUnavailable =>
                    GuardianHostExecutionFallbackReason.RtkExecutableBecameUnavailable,
                ExecutionFallbackReason.RtkIneligibleShape =>
                    GuardianHostExecutionFallbackReason.RtkIneligibleShape,
                ExecutionFallbackReason.RtkSelfInvocation =>
                    GuardianHostExecutionFallbackReason.RtkSelfInvocation,
                ExecutionFallbackReason.RtkResolutionNotApplication =>
                    GuardianHostExecutionFallbackReason.RtkResolutionNotApplication,
                ExecutionFallbackReason.RtkFidelityExclusion =>
                    GuardianHostExecutionFallbackReason.RtkFidelityExclusion,
                ExecutionFallbackReason.RtkExecutionPreparationFailed =>
                    GuardianHostExecutionFallbackReason.RtkExecutionPreparationFailed,
                ExecutionFallbackReason.RtkTargetResolutionChanged =>
                    GuardianHostExecutionFallbackReason.RtkTargetResolutionChanged,
                _ => throw InvalidDescriptor(),
            },
            Digest(descriptor.WorkingDirectoryDigest),
            Digest(descriptor.RtkBinaryDigest),
            Digest(descriptor.BashBinaryDigest),
            Digest(descriptor.OutputShapingRtkBinaryDigest));
    }

    private static GuardianHostEffectiveExecutionRoute MapEffectiveRoute(
        ExecutionPath value) => value switch
    {
        ExecutionPath.PowerShellDirect =>
            GuardianHostEffectiveExecutionRoute.PowerShellDirect,
        ExecutionPath.Rtk =>
            GuardianHostEffectiveExecutionRoute.Rtk,
        ExecutionPath.NativeDirect =>
            GuardianHostEffectiveExecutionRoute.NativeDirect,
        ExecutionPath.BashViaRtk =>
            GuardianHostEffectiveExecutionRoute.BashViaRtk,
        _ => throw InvalidDescriptor(),
    };

    private static Sha256Digest? Digest(string? value) =>
        value is null ? null : new Sha256Digest(value);

    private static InvalidDataException InvalidDescriptor() =>
        new("Worker prepared descriptor contains an unsupported value.");
}

/// <summary>
/// The host's sole prepared-dispatch authority. It binds one worker preparation
/// to the admitted private operation and waits for the exact guardian control
/// response. A caller may return from this method only when that exact plan is
/// authorized for the current alias transition and worker generation.
/// </summary>
internal sealed class PrivateHostPreparedDispatchAuthorizer
{
    private readonly PrivateHostServerIdentity _identity;
    private readonly IPrivateHostControlEventSink _control;
    private readonly Func<long> _unixTimeMilliseconds;

    internal PrivateHostPreparedDispatchAuthorizer(
        PrivateHostServerIdentity identity,
        IPrivateHostControlEventSink control,
        Func<long>? unixTimeMilliseconds = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _unixTimeMilliseconds = unixTimeMilliseconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal async ValueTask AuthorizeAsync(
        OperationRequest request,
        PrivateHostWorkerSlot slot,
        WorkerPreparedPlanDescriptor workerDescriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(workerDescriptor);
        cancellationToken.ThrowIfCancellationRequested();

        var operation = request.OperationIdentity ??
            throw InvalidCorrelation(
                "Prepared dispatch requires an operation identity.");
        var requestWorker = request.Worker ??
            throw InvalidCorrelation(
                "Prepared dispatch requires a request worker identity.");
        if (request.Operation.Kind is not (
                GuardianHostOperationKind.InvokeForeground or
                GuardianHostOperationKind.InvokeBackground) ||
            request.GuardianBootId != _identity.GuardianBootId ||
            request.HostBootId != _identity.HostBootId ||
            request.HostGeneration != _identity.HostGeneration ||
            request.SessionAlias != slot.Binding.Alias ||
            request.SessionTransitionVersion != slot.Binding.TransitionVersion ||
            !WorkerMatches(requestWorker, slot.Identity))
        {
            throw InvalidCorrelation(
                "Prepared dispatch does not match the admitted private operation.");
        }

        var descriptor =
            PrivateHostPreparedPlanProjection.Project(workerDescriptor);
        if (descriptor.PlanId != operation.PlanId ||
            descriptor.WorkerIdentity.BootId != slot.Identity.BootId ||
            descriptor.WorkerIdentity.Generation != slot.Identity.Generation ||
            descriptor.DeadlineUnixTimeMilliseconds !=
                request.DeadlineUnixTimeMilliseconds ||
            descriptor.DeadlineUnixTimeMilliseconds <= _unixTimeMilliseconds())
        {
            throw InvalidCorrelation(
                "Worker preparation does not match the admitted operation.");
        }

        PreparedDispatchAuthorizationRequestedEvent? source = null;
        var response = await _control.ExchangeControlAsync(
                sequence => source =
                    new PreparedDispatchAuthorizationRequestedEvent(
                        _identity.GuardianBootId,
                        _identity.HostBootId,
                        _identity.HostGeneration,
                        sequence,
                        request.RequestId,
                        slot.Binding.Alias,
                        slot.Binding.TransitionVersion,
                        slot.Identity,
                        operation,
                        descriptor),
                cancellationToken)
            .ConfigureAwait(false);

        var authorization = response as PreparedDispatchAuthorizeRequest ??
            throw InvalidCorrelation(
                "Guardian returned the wrong prepared-dispatch control.");
        if (source is null ||
            authorization.GuardianBootId != _identity.GuardianBootId ||
            authorization.HostBootId != _identity.HostBootId ||
            authorization.HostGeneration != _identity.HostGeneration ||
            authorization.SessionAlias != slot.Binding.Alias ||
            authorization.SessionTransitionVersion !=
                slot.Binding.TransitionVersion ||
            !WorkerMatches(authorization.Worker, slot.Identity) ||
            authorization.Identity.PlanId != operation.PlanId ||
            authorization.Identity.OperationId != operation.OperationId ||
            authorization.SourceEventSequence != source.EventSequence ||
            authorization.DeadlineUnixTimeMilliseconds !=
                descriptor.DeadlineUnixTimeMilliseconds ||
            authorization.DescriptorDigest != descriptor.DescriptorDigest ||
            descriptor.DeadlineUnixTimeMilliseconds <= _unixTimeMilliseconds())
        {
            throw InvalidCorrelation(
                "Guardian prepared-dispatch control does not match its source event.");
        }
    }

    private static bool WorkerMatches(
        GuardianHostWorkerIdentity left,
        GuardianHostWorkerIdentity right) =>
        left.BootId == right.BootId &&
        left.Generation == right.Generation;

    private static InvalidDataException InvalidCorrelation(string message) =>
        new(message);
}

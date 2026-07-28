using PtkMcpServer.Sessions;

namespace PtkMcpServer.Worker;

internal interface IWorkerSession : IWorkerOperationExecutor, ISessionLifetime
{
}

/// <summary>
/// The only adapter between the minimal worker protocol and one warm
/// SessionRuntime. It does not own supervisor output storage or audit state.
/// </summary>
internal sealed class WorkerSession : IWorkerSession
{
    private readonly SessionRuntime _runtime;

    internal WorkerSession(SessionRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<WorkerExecutionResult> ExecuteAsync(
        WorkerOperationRequest request,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        switch (request)
        {
            case WorkerInvokeRequest invoke:
                {
                    using var capture = invoke.Artifact is null
                        ? null
                        : new WorkerForegroundOutputCapture(
                            invoke.Artifact.MaximumBytes);
                    var result = await _runtime.InvokeWorkerAsync(
                        invoke.Script,
                        cancellationToken,
                        invoke.Raw,
                        RouteName(invoke.Route),
                        invoke.TimeoutSeconds,
                        deadlineUtc,
                        capture).ConfigureAwait(false);
                    var (status, detailCode) = MapInvokeResult(result);
                    WorkerArtifactPayload? artifact = null;
                    var text = result.Text;
                    if (capture?.TakeContent() is { } content)
                    {
                        try
                        {
                            artifact = new WorkerArtifactPayload(
                                invoke.Artifact!.ArtifactId,
                                WorkerOutputArtifactCodec.Encode(
                                    content,
                                    invoke.Artifact.MaximumBytes));
                        }
                        catch (WorkerProtocolException exception) when (
                            exception.DetailCode == "artifact_content_too_large")
                        {
                            text = AppendRecoveryUnavailable(
                                text,
                                exception.DetailCode);
                        }
                    }
                    return new WorkerInvokeExecutionResult(
                        status,
                        text,
                        detailCode,
                        artifact);
                }
            case WorkerStateQueryRequest state:
                {
                    var result = await _runtime.StateWorkerAsync(
                        state.ListAvailable,
                        cancellationToken).ConfigureAwait(false);
                    return new WorkerStateExecutionResult(
                        result.RunspaceDetailsAvailable,
                        result.Text,
                        result.RunspaceDetailsAvailable ? null : "runspace_busy");
                }
            default:
                throw new WorkerProtocolException(
                    "unsupported_operation",
                    "Worker session received an unsupported operation.");
        }
    }

    public Task ShutdownAsync() => ((ISessionLifetime)_runtime).ShutdownAsync();

    public void Dispose() => _runtime.Dispose();

    private static (WorkerResultStatus Status, string? DetailCode) MapInvokeResult(
        SessionWorkerInvokeResult result)
    {
        if (result.TimedOut)
            return (WorkerResultStatus.TimedOut, "execution_timed_out");
        return result.Disposition switch
        {
            InvokeDisposition.Completed => (WorkerResultStatus.Completed, null),
            InvokeDisposition.NotStarted =>
                (WorkerResultStatus.Refused, "operation_not_started"),
            InvokeDisposition.Canceled =>
                (WorkerResultStatus.Canceled, "operation_canceled"),
            InvokeDisposition.OutcomeUnknown =>
                (WorkerResultStatus.Failed, "outcome_unknown"),
            _ => (WorkerResultStatus.Failed, "execution_failed"),
        };
    }

    private static string RouteName(WorkerInvokeRoute route) => route switch
    {
        WorkerInvokeRoute.Auto => "auto",
        WorkerInvokeRoute.Pwsh => "pwsh",
        WorkerInvokeRoute.Rtk => "rtk",
        _ => throw new WorkerProtocolException(
            "invalid_operation_field",
            "Worker invoke route is invalid."),
    };

    private static string AppendRecoveryUnavailable(
        string text,
        string detailCode) =>
        text.TrimEnd() + Environment.NewLine +
        $"recovery=unavailable: output capture unavailable " +
        $"(detail={detailCode}); command was not rerun";
}

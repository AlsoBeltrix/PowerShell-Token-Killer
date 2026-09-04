using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Worker;

internal interface IWorkerArtifactCapture : IDisposable
{
    WorkerArtifactRequest Request { get; }
    bool IsSealed { get; }
    Task SinkCompletionForTests { get; }

    void BindRequest(long requestId);
    void Accept(WorkerArtifactChunk chunk);
    void Accept(WorkerArtifactSeal seal);
    Task<OutputRecoverySummary> CompleteAtResultAsync();
}

/// <summary>
/// Supervisor-owned optional artifact path. The protocol reader performs only
/// bounded validation and copies into one fixed byte buffer. A separate sink owns
/// decoding, storage, and publication; failure discards recovery without
/// delaying or replaying the ordinary invocation.
/// </summary>
internal sealed class SupervisorWorkerArtifactCapture : IWorkerArtifactCapture
{
    private readonly OutputStore _store;
    private readonly OutputCaptureReservation _reservation;
    private readonly TimeSpan _storageWait;
    private readonly int _maximumChunkBytes;
    private readonly byte[] _buffer;
    private readonly CancellationTokenSource _discard = new();
    private readonly Func<CancellationToken, Task>? _sinkGateForTests;
    private readonly object _discardGate = new();
    private WorkerArtifactReceiver? _receiver;
    private Task<SinkResult>? _sink;
    private string? _failure;
    private int _sealedLength;
    private int _bound;
    private int _discarding;
    private int _completed;
    private int _disposed;

    internal SupervisorWorkerArtifactCapture(
        OutputStore store,
        OutputCaptureReservation reservation,
        long maximumBytes,
        int maximumChunkBytes,
        TimeSpan storageWait,
        int? captureBufferBytesForTests = null,
        Func<CancellationToken, Task>? sinkGateForTests = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reservation = reservation ??
            throw new ArgumentNullException(nameof(reservation));
        if (maximumBytes is < 1 or > WorkerOperationProtocol.MaximumArtifactBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (maximumChunkBytes is < 1 or >
            WorkerOperationProtocol.MaximumArtifactChunkBytes ||
            maximumChunkBytes > maximumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChunkBytes));
        }
        if (storageWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(storageWait));

        var bufferBytes = captureBufferBytesForTests ??
            checked((int)maximumBytes);
        if (bufferBytes is < 1 || bufferBytes > maximumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureBufferBytesForTests));
        }

        Request = new WorkerArtifactRequest(
            reservation.ArtifactGuid,
            maximumBytes);
        _maximumChunkBytes = maximumChunkBytes;
        _storageWait = storageWait;
        _sinkGateForTests = sinkGateForTests;
        _buffer = new byte[bufferBytes];
    }

    public WorkerArtifactRequest Request { get; }
    public bool IsSealed => _receiver?.IsSealed == true;
    public Task SinkCompletionForTests =>
        _sink ?? Task.CompletedTask;

    public void BindRequest(long requestId)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (Interlocked.Exchange(ref _bound, 1) != 0)
            throw new InvalidOperationException("Artifact capture is already bound.");
        _receiver = new WorkerArtifactReceiver(requestId, Request);
    }

    public void Accept(WorkerArtifactChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Bytes.Length > _maximumChunkBytes)
        {
            throw new WorkerProtocolException(
                "artifact_chunk_too_large",
                "Worker artifact chunk exceeds the negotiated bound.");
        }

        var receiver = RequireReceiver();
        receiver.Accept(chunk);
        if (Volatile.Read(ref _discarding) != 0) return;

        var end = checked((int)receiver.Length);
        var start = end - chunk.Bytes.Length;
        if (end > _buffer.Length)
        {
            BeginDiscard("artifact_queue_full");
            return;
        }

        chunk.Bytes.CopyTo(_buffer, start);
    }

    public void Accept(WorkerArtifactSeal seal)
    {
        var receiver = RequireReceiver();
        receiver.Accept(seal);
        _sealedLength = checked((int)receiver.Length);
        if (Volatile.Read(ref _discarding) == 0)
            _sink = RunSinkAsync();
    }

    public Task<OutputRecoverySummary> CompleteAtResultAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return Task.FromResult(
                OutputRecoverySummary.Unavailable(
                    "artifact_result_already_completed",
                    advertise: true));
        }

        var sink = _sink;
        if (sink is null)
        {
            var detailCode =
                Volatile.Read(ref _failure) ?? "artifact_sink_incomplete";
            _ = TryBeginDiscard(detailCode);
            return Task.FromResult(
                OutputRecoverySummary.Unavailable(
                    detailCode,
                    advertise: true));
        }

        return CompleteSinkAtResultAsync(sink);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        BeginDiscard(_failure ?? "artifact_capture_disposed");
        _receiver?.Dispose();
        _receiver = null;
        var sink = _sink;
        if (sink is null || sink.IsCompleted)
        {
            ClearBuffer();
            _discard.Dispose();
        }
        else
            _ = sink.ContinueWith(
                _ =>
                {
                    ClearBuffer();
                    _discard.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private async Task<SinkResult> RunSinkAsync()
    {
        try
        {
            if (_sinkGateForTests is not null)
                await _sinkGateForTests(_discard.Token).ConfigureAwait(false);

            if (Volatile.Read(ref _discarding) != 0)
                return SinkResult.Unavailable(_failure ?? "artifact_discarded");

            Task<OutputSealResult>? storage;
            try
            {
                storage = await _store.WaitToStartForegroundOperationAsync(
                    () => _reservation.Seal(
                        WorkerOutputArtifactCodec.Decode(
                            _buffer.AsSpan(0, _sealedLength),
                            Request.MaximumBytes)),
                    _storageWait,
                    _discard.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return SinkResult.Unavailable(
                    _failure ?? "artifact_sink_canceled");
            }

            if (storage is null)
            {
                _ = _reservation.TryCancel();
                return SinkResult.Unavailable("output_store_seal_timed_out");
            }

            OutputSealResult sealedResult;
            try
            {
                sealedResult = await storage.ConfigureAwait(false);
            }
            catch (WorkerProtocolException exception)
            {
                _ = _reservation.TryCancel();
                return SinkResult.Protocol(exception);
            }
            catch
            {
                _ = _reservation.TryCancel();
                return SinkResult.Unavailable("output_store_unavailable");
            }
            finally
            {
                _reservation.CompleteObserved();
            }

            return sealedResult.Success
                ? SinkResult.Available(
                    OutputRecoverySummary.FromSeal(sealedResult))
                : SinkResult.Unavailable(
                    sealedResult.DetailCode ?? "output_store_unavailable");
        }
        catch (WorkerProtocolException exception)
        {
            _ = _reservation.TryCancel();
            return SinkResult.Protocol(exception);
        }
        catch (OperationCanceledException)
        {
            _ = _reservation.TryCancel();
            return SinkResult.Unavailable(
                _failure ?? "artifact_sink_canceled");
        }
        catch
        {
            _ = _reservation.TryCancel();
            return SinkResult.Unavailable("artifact_sink_failed");
        }
        finally
        {
            ClearBuffer();
        }
    }

    private WorkerArtifactReceiver RequireReceiver() =>
        _receiver ??
        throw new InvalidOperationException("Artifact capture is not bound.");

    private async Task<OutputRecoverySummary> CompleteSinkAtResultAsync(
        Task<SinkResult> sink)
    {
        if (!sink.IsCompleted)
        {
            var deadline = Task.Delay(_storageWait);
            if (await Task.WhenAny(sink, deadline).ConfigureAwait(false) != sink &&
                TryBeginDiscard("artifact_sink_incomplete"))
            {
                Observe(sink);
                return OutputRecoverySummary.Unavailable(
                    "artifact_sink_incomplete",
                    advertise: true);
            }
        }

        var result = await sink.ConfigureAwait(false);
        if (result.ProtocolFailure is not null)
            throw result.ProtocolFailure;
        return result.Recovery;
    }

    private void BeginDiscard(string detailCode) =>
        _ = TryBeginDiscard(detailCode);

    private bool TryBeginDiscard(string detailCode)
    {
        lock (_discardGate)
        {
            if (Volatile.Read(ref _discarding) != 0)
                return true;
            if (!_reservation.TryCancel())
            {
                // The store crossed its irreversible publication claim.
                // Returning unavailable now would strand a valid but
                // unreachable handle, so the terminal coordinator must observe
                // the exact sink result instead.
                return false;
            }
            _failure = detailCode;
            Volatile.Write(ref _discarding, 1);
        }

        try { _discard.Cancel(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    private void ClearBuffer() =>
        CryptographicOperations.ZeroMemory(_buffer);

    private static void Observe(Task? task)
    {
        if (task is null) return;
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record SinkResult(
        OutputRecoverySummary Recovery,
        WorkerProtocolException? ProtocolFailure)
    {
        internal static SinkResult Available(OutputRecoverySummary recovery) =>
            new(recovery, null);

        internal static SinkResult Unavailable(string detailCode) =>
            new(
                OutputRecoverySummary.Unavailable(
                    detailCode,
                    advertise: true),
                null);

        internal static SinkResult Protocol(WorkerProtocolException failure) =>
            new(
                OutputRecoverySummary.Unavailable(
                    failure.DetailCode,
                    advertise: true),
                failure);
    }
}

internal sealed class WorkerForegroundOutputCapture : IForegroundOutputCapture
{
    private OutputArtifactContent? _content;
    private int _sealed;
    private int _disposed;

    internal WorkerForegroundOutputCapture(long maximumArtifactBytes)
    {
        if (maximumArtifactBytes is < 1 or >
            WorkerOperationProtocol.MaximumArtifactBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArtifactBytes));
        }
        MaximumArtifactBytes = maximumArtifactBytes;
    }

    public long MaximumArtifactBytes { get; }

    public Task PrepareAsync(
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        if (maximumWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.CompletedTask;
    }

    public Task<OutputRecoverySummary> SealAsync(
        OutputArtifactContent content,
        TimeSpan maximumWait) =>
        SealCoreAsync(content, incompleteReason: null, maximumWait);

    public Task<OutputRecoverySummary> SealIncompleteAsync(
        OutputArtifactContent content,
        string reason,
        TimeSpan maximumWait) =>
        SealCoreAsync(
            content,
            reason ?? throw new ArgumentNullException(nameof(reason)),
            maximumWait);

    internal OutputArtifactContent? TakeContent()
    {
        ThrowIfDisposed();
        return Interlocked.Exchange(ref _content, null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Interlocked.Exchange(ref _content, null);
    }

    private Task<OutputRecoverySummary> SealCoreAsync(
        OutputArtifactContent content,
        string? incompleteReason,
        TimeSpan maximumWait)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _sealed, 1) != 0)
        {
            return Task.FromResult(
                OutputRecoverySummary.Unavailable("artifact_already_sealed"));
        }

        var captured = incompleteReason is null
            ? content
            : content with
            {
                Complete = false,
                IncompleteReason = incompleteReason,
            };
        Interlocked.Exchange(ref _content, Clone(captured));
        return Task.FromResult(
            OutputRecoverySummary.Unavailable(
                "supervisor_capture_pending",
                advertise: false));
    }

    private static OutputArtifactContent Clone(OutputArtifactContent content) =>
        content with
        {
            StandardError = [.. content.StandardError],
            Errors = [.. content.Errors],
            Warnings = [.. content.Warnings],
            Information = [.. content.Information],
            Verbose = [.. content.Verbose],
        };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}

internal static class WorkerOutputArtifactCodec
{
    private const int SchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static byte[] Encode(
        OutputArtifactContent content,
        long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumBytes is < 1 or >
            WorkerOperationProtocol.MaximumArtifactBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("standardOutput", content.StandardOutput);
            WriteStrings(writer, "standardError", content.StandardError);
            WriteStrings(writer, "errors", content.Errors);
            WriteStrings(writer, "warnings", content.Warnings);
            WriteStrings(writer, "information", content.Information);
            WriteStrings(writer, "verbose", content.Verbose);
            if (content.ExitCode is { } exitCode)
                writer.WriteNumber("exitCode", exitCode);
            else
                writer.WriteNull("exitCode");
            writer.WriteString("provenance", ProvenanceName(content.Provenance));
            writer.WriteBoolean("complete", content.Complete);
            if (content.IncompleteReason is { } reason)
                writer.WriteString("incompleteReason", reason);
            else
                writer.WriteNull("incompleteReason");
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > maximumBytes)
        {
            throw new WorkerProtocolException(
                "artifact_content_too_large",
                "Recoverable output exceeds the negotiated artifact bound.");
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal static OutputArtifactContent Decode(
        ReadOnlySpan<byte> bytes,
        long maximumBytes)
    {
        if (bytes.Length is < 1 || bytes.Length > maximumBytes ||
            bytes.Length > WorkerOperationProtocol.MaximumArtifactBytes)
        {
            throw Invalid("Recoverable output is outside the negotiated bound.");
        }

        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                schemaElement.ValueKind != JsonValueKind.Number ||
                !schemaElement.TryGetInt32(out var schemaVersion))
            {
                throw Invalid("Recoverable output version is invalid.");
            }
            var fields = schemaVersion switch
            {
                LegacySchemaVersion => ClosedObject(
                    document.RootElement,
                    "schemaVersion",
                    "standardOutput",
                    "standardError",
                    "errors",
                    "warnings",
                    "exitCode",
                    "provenance",
                    "complete",
                    "incompleteReason"),
                SchemaVersion => ClosedObject(
                    document.RootElement,
                    "schemaVersion",
                    "standardOutput",
                    "standardError",
                    "errors",
                    "warnings",
                    "information",
                    "verbose",
                    "exitCode",
                    "provenance",
                    "complete",
                    "incompleteReason"),
                _ => throw Invalid("Recoverable output version is unsupported."),
            };

            var complete = Required(fields, "complete").GetBoolean();
            var reasonElement = Required(fields, "incompleteReason");
            var reason = reasonElement.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => reasonElement.GetString(),
                _ => throw Invalid("Recoverable output reason is invalid."),
            };
            if (complete == (reason is not null))
            {
                throw Invalid(
                    "Recoverable output completeness and reason disagree.");
            }

            var exitElement = Required(fields, "exitCode");
            int? exitCode = exitElement.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Number when exitElement.TryGetInt32(out var value) =>
                    value,
                _ => throw Invalid("Recoverable output exit code is invalid."),
            };

            return new OutputArtifactContent(
                RequiredString(fields, "standardOutput"),
                RequiredStrings(fields, "standardError"),
                RequiredStrings(fields, "errors"),
                RequiredStrings(fields, "warnings"),
                exitCode,
                ParseProvenance(RequiredString(fields, "provenance")),
                complete,
                reason)
            {
                Information = schemaVersion == SchemaVersion
                    ? RequiredStrings(fields, "information")
                    : [],
                Verbose = schemaVersion == SchemaVersion
                    ? RequiredStrings(fields, "verbose")
                    : [],
            };
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
                InvalidOperationException or
                FormatException or
                DecoderFallbackException)
        {
            throw new WorkerProtocolException(
                "artifact_content_invalid",
                "Recoverable output content is invalid.",
                exception);
        }
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string name,
        IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static Dictionary<string, JsonElement> ClosedObject(
        JsonElement element,
        params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid("Recoverable output must be one object.");
        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) ||
                !fields.TryAdd(property.Name, property.Value))
            {
                throw Invalid(
                    "Recoverable output has an unknown or duplicate field.");
            }
        }
        if (fields.Count != names.Length)
            throw Invalid("Recoverable output is missing a required field.");
        return fields;
    }

    private static JsonElement Required(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name) =>
        fields.TryGetValue(name, out var value)
            ? value
            : throw Invalid("Recoverable output is missing a required field.");

    private static string RequiredString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = Required(fields, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Invalid("Recoverable output string field is invalid.");
    }

    private static string[] RequiredStrings(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        var value = Required(fields, name);
        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid("Recoverable output string array is invalid.");
        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw Invalid("Recoverable output string array is invalid.");
            values.Add(item.GetString()!);
        }
        return [.. values];
    }

    private static string ProvenanceName(OutputProvenance provenance) =>
        provenance.ToMachineCode();

    private static OutputProvenance ParseProvenance(string value) => value switch
    {
        "powershell_objects" => OutputProvenance.PowerShellObjects,
        "direct_text" => OutputProvenance.DirectText,
        "rtk_unknown" => OutputProvenance.RtkUnknown,
        "rtk_filtered" => OutputProvenance.RtkFiltered,
        "rtk_passthrough" => OutputProvenance.RtkPassthrough,
        _ => throw Invalid("Recoverable output provenance is invalid."),
    };

    private static WorkerProtocolException Invalid(string message) =>
        new("artifact_content_invalid", message);
}

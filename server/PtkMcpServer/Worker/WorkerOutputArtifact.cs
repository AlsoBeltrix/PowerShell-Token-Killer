using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Worker;

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
        };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}

internal static class WorkerOutputArtifactCodec
{
    private const int SchemaVersion = 1;
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
            var fields = ClosedObject(
                document.RootElement,
                "schemaVersion",
                "standardOutput",
                "standardError",
                "errors",
                "warnings",
                "exitCode",
                "provenance",
                "complete",
                "incompleteReason");
            if (Required(fields, "schemaVersion").GetInt32() != SchemaVersion)
                throw Invalid("Recoverable output version is unsupported.");

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
                reason);
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

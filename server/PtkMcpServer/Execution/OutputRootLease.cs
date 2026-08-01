using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer;

internal sealed record OutputRootOwnership(
    Guid CreationId,
    int ProcessId,
    long CreatedUtcTicks)
{
    internal string DirectoryName =>
        $"server-{ProcessId}-{CreationId:N}";

    internal static OutputRootOwnership CreateCurrent() =>
        new(
            Guid.NewGuid(),
            Environment.ProcessId,
            DateTime.UtcNow.Ticks);
}

/// <summary>
/// Owns one production output root. The retained exclusive marker is the live
/// ownership proof; once its handle is gone, another supervisor may validate
/// the recorded identity and reclaim only that exact PTK root.
/// </summary>
internal sealed class OutputRootLease : IDisposable
{
    private const string MarkerName = "owner.v1.json";
    private const int MarkerSchemaVersion = 1;
    private const int MaximumMarkerBytes = 512;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private static readonly ConcurrentDictionary<string, byte> LiveRoots =
        new(PathComparer);

    private readonly string _root;
    private readonly string _parent;
    private FileStream? _marker;
    private int _disposed;

    private OutputRootLease(
        string root,
        string parent,
        FileStream marker)
    {
        _root = root;
        _parent = parent;
        _marker = marker;
        if (!LiveRoots.TryAdd(root, 0))
            throw new IOException("The output root already has a live owner.");
    }

    internal string RootPath => _root;

    internal static OutputRootLease Acquire(
        string requestedRoot,
        OutputRootOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        if (ownership.CreationId == Guid.Empty ||
            ownership.ProcessId <= 0 ||
            ownership.CreatedUtcTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(requestedRoot));
        var parent = Path.GetDirectoryName(root) ??
            throw new IOException("The output root parent is unavailable.");
        if (!string.Equals(
                Path.GetFileName(root),
                ownership.DirectoryName,
                PathComparison))
        {
            throw new ArgumentException(
                "The output root does not match its creation identity.",
                nameof(requestedRoot));
        }

        parent = SecureAuditStorage.PrepareRoot(parent);
        var rootExisted = Directory.Exists(root);
        root = SecureAuditStorage.PrepareRoot(root);
        var markerPath = Path.Combine(root, MarkerName);
        FileStream? marker = null;
        var markerCreated = false;
        try
        {
            marker = SecureAuditStorage.CreateExclusiveFile(
                markerPath,
                access: FileAccess.ReadWrite);
            markerCreated = true;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = MarkerSchemaVersion,
                processId = ownership.ProcessId,
                createdUtcTicks = ownership.CreatedUtcTicks,
                creationId = ownership.CreationId.ToString("N"),
            });
            if (bytes.Length > MaximumMarkerBytes)
                throw new IOException("The output owner marker is oversized.");
            marker.Write(bytes);
            marker.Flush(flushToDisk: true);
            LockMarker(marker);
            SecureAuditStorage.ConfirmRetainedCreatedFileDurability(
                root,
                markerPath,
                marker.SafeFileHandle);

            var lease = new OutputRootLease(
                root,
                parent,
                marker);
            marker = null;
            lease.ReclaimAbandonedSiblings();
            return lease;
        }
        catch
        {
            marker?.Dispose();
            if (markerCreated)
                TryDelete(markerPath);
            if (!rootExisted)
                TryDeleteDirectory(root);
            throw;
        }
    }

    internal void AbandonForTests()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        var marker = Interlocked.Exchange(ref _marker, null);
        SafeUnlock(marker);
        marker?.Dispose();
        LiveRoots.TryRemove(_root, out _);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var marker = Interlocked.Exchange(ref _marker, null);
        SafeUnlock(marker);
        marker?.Dispose();
        LiveRoots.TryRemove(_root, out _);
        TryReclaim(new DirectoryInfo(_root));
    }

    private void ReclaimAbandonedSiblings()
    {
        try
        {
            foreach (var directory in new DirectoryInfo(_parent)
                         .EnumerateDirectories(
                             "server-*",
                             SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(
                        Path.TrimEndingDirectorySeparator(directory.FullName),
                        _root,
                        PathComparison))
                {
                    continue;
                }

                TryReclaim(directory);
            }
        }
        catch
        {
            // Output recovery is optional. Unrecognized or unreadable sibling
            // state is preserved rather than making supervisor startup fail.
        }
    }

    private static void TryReclaim(DirectoryInfo directory)
    {
        FileStream? marker = null;
        try
        {
            var candidateRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(directory.FullName));
            if (LiveRoots.ContainsKey(candidateRoot))
                return;
            if (!TryParseDirectoryName(
                    directory.Name,
                    out var expectedProcessId,
                    out var expectedCreationId))
            {
                return;
            }

            SecureAuditStorage.VerifyExternalProtectedDirectory(
                directory.FullName);
            var entries = directory
                .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
                .ToArray();
            var markerPath = Path.Combine(directory.FullName, MarkerName);
            var artifactPaths = new List<string>();
            var foundMarker = false;
            foreach (var entry in entries)
            {
                if (entry is not FileInfo ||
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                if (string.Equals(
                        entry.Name,
                        MarkerName,
                        StringComparison.Ordinal))
                {
                    if (foundMarker) return;
                    foundMarker = true;
                }
                else if (IsArtifactFileName(entry.Name))
                {
                    artifactPaths.Add(entry.FullName);
                }
                else
                {
                    return;
                }
            }
            if (!foundMarker) return;

            SecureAuditStorage.VerifyExternalProtectedFile(markerPath);
            marker = new FileStream(
                markerPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            LockMarker(marker);
            if (LiveRoots.ContainsKey(candidateRoot))
                return;
            _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                markerPath,
                marker.SafeFileHandle);
            if (!TryReadMarker(
                    marker,
                    expectedProcessId,
                    expectedCreationId))
            {
                return;
            }
            _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
                markerPath,
                marker.SafeFileHandle);

            foreach (var artifactPath in artifactPaths)
            {
                SecureAuditStorage.VerifyExternalProtectedFile(artifactPath);
                File.Delete(artifactPath);
            }
            SafeUnlock(marker);
            marker.Dispose();
            marker = null;
            File.Delete(markerPath);
            Directory.Delete(directory.FullName, recursive: false);
        }
        catch
        {
            // Sharing violations identify live owners. Invalid, changed, or
            // unfamiliar roots are also deliberately left untouched.
        }
        finally
        {
            SafeUnlock(marker);
            marker?.Dispose();
        }
    }

    private static bool TryReadMarker(
        FileStream marker,
        int expectedProcessId,
        Guid expectedCreationId)
    {
        try
        {
            if (marker.Length is < 1 or > MaximumMarkerBytes)
                return false;
            marker.Position = 0;
            var bytes = new byte[checked((int)marker.Length)];
            marker.ReadExactly(bytes);
            if (marker.ReadByte() != -1)
                return false;

            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var fields = new Dictionary<string, JsonElement>(
                StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!fields.TryAdd(property.Name, property.Value))
                    return false;
            }
            if (fields.Count != 4 ||
                !fields.TryGetValue("schemaVersion", out var schema) ||
                schema.GetInt32() != MarkerSchemaVersion ||
                !fields.TryGetValue("processId", out var processId) ||
                processId.GetInt32() != expectedProcessId ||
                !fields.TryGetValue("createdUtcTicks", out var created) ||
                created.GetInt64() <= 0 ||
                !fields.TryGetValue("creationId", out var creationId) ||
                creationId.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(
                    creationId.GetString(),
                    "N",
                    out var parsedCreationId) ||
                parsedCreationId != expectedCreationId)
            {
                return false;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or
                InvalidOperationException or
                FormatException or
                EndOfStreamException or
                IOException)
        {
            return false;
        }
    }

    private static bool TryParseDirectoryName(
        string name,
        out int processId,
        out Guid creationId)
    {
        processId = 0;
        creationId = Guid.Empty;
        const string prefix = "server-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var identitySeparator = name.LastIndexOf('-');
        if (identitySeparator <= prefix.Length ||
            identitySeparator == name.Length - 1)
        {
            return false;
        }
        return int.TryParse(
                   name.AsSpan(prefix.Length, identitySeparator - prefix.Length),
                   out processId) &&
               processId > 0 &&
               Guid.TryParseExact(
                   name.AsSpan(identitySeparator + 1),
                   "N",
                   out creationId) &&
               creationId != Guid.Empty;
    }

    private static bool IsArtifactFileName(string name)
    {
        const string prefix = "artifact-";
        const string suffix = ".out";
        return name.StartsWith(prefix, StringComparison.Ordinal) &&
               name.EndsWith(suffix, StringComparison.Ordinal) &&
               Guid.TryParseExact(
                   name.AsSpan(
                       prefix.Length,
                       name.Length - prefix.Length - suffix.Length),
                   "N",
                   out _);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: false); }
        catch { }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static void SafeUnlock(FileStream? marker)
    {
        if (marker is null || OperatingSystem.IsWindows()) return;
        try
        {
            _ = Flock(
                marker.SafeFileHandle.DangerousGetHandle().ToInt32(),
                LockUnlock);
        }
        catch { }
    }

    private static void LockMarker(FileStream marker)
    {
        if (OperatingSystem.IsWindows()) return;
        if (Flock(
                marker.SafeFileHandle.DangerousGetHandle().ToInt32(),
                LockExclusive | LockNonBlocking) != 0)
        {
            throw new IOException(
                "The output root already has a live owner.");
        }
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);
}

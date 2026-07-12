using System.Security.Cryptography;

namespace PtkMcpServer;

/// <summary>A bounded, canonical executable-file snapshot. It narrows
/// path replacement to the final check/start race; protected installation or
/// an OS-bound executable handle is still required to eliminate that race.</summary>
internal sealed record ExecutableFileIdentity
{
    private const long MaximumExecutableBytes = 128L * 1024 * 1024;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    [ThreadStatic]
    private static Action<string>? _beforeHashForTests;

    private ExecutableFileIdentity(
        string executablePath,
        string binaryDigest,
        UnixFileMode? unixFileMode)
    {
        ExecutablePath = executablePath;
        BinaryDigest = binaryDigest;
        UnixFileMode = unixFileMode;
    }

    internal string ExecutablePath { get; }
    internal string BinaryDigest { get; }
    internal UnixFileMode? UnixFileMode { get; }

    /// <summary>Same-thread test seam for deterministic file growth between
    /// the bounded metadata snapshot and stream open. Production never sets it.</summary>
    internal static Action<string>? BeforeHashForTests
    {
        get => _beforeHashForTests;
        set => _beforeHashForTests = value;
    }

    internal static ExecutableFileIdentity? TryCapture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            if (!file.Exists || file.Length is <= 0 or > MaximumExecutableBytes)
                return null;

            var target = file.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                fullPath = Path.GetFullPath(target.FullName);
                file = new FileInfo(fullPath);
                if (!file.Exists || file.Length is <= 0 or > MaximumExecutableBytes)
                    return null;
            }
            if (file.LinkTarget is not null) return null;
            var expectedLength = file.Length;
            BeforeHashForTests?.Invoke(fullPath);

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (total < expectedLength)
            {
                var requested = checked((int)Math.Min(buffer.Length, expectedLength - total));
                var read = stream.Read(buffer, 0, requested);
                if (read <= 0) return null;
                hash.AppendData(buffer, 0, read);
                total += read;
            }
            if (stream.ReadByte() != -1) return null;
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            file = new FileInfo(fullPath);
            if (!file.Exists || file.LinkTarget is not null ||
                file.Length != expectedLength)
            {
                return null;
            }
            var unixFileMode = OperatingSystem.IsWindows()
                ? (UnixFileMode?)null
                : File.GetUnixFileMode(fullPath);
            return new ExecutableFileIdentity(fullPath, digest, unixFileMode);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return null;
        }
    }

    internal bool MatchesCurrentFile() =>
        TryCapture(ExecutablePath) is { } current &&
        string.Equals(current.ExecutablePath, ExecutablePath, PathComparison) &&
        string.Equals(current.BinaryDigest, BinaryDigest, StringComparison.Ordinal) &&
        current.UnixFileMode == UnixFileMode;
}

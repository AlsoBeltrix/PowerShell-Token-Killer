using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace PtkMcpServer.Audit;

/// <summary>
/// Serializes physical spool inventory, recovery, allocation, and retention
/// work across every supervisor that shares one protected spool root.
/// </summary>
internal sealed class AuditSpoolQuotaLease : IDisposable
{
    internal const string ControlFileName = ".ptk-audit-quota.lock";
    private const byte ControlFormatMarker = 0x50;
    private static readonly TimeSpan WriterWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly string _root;
    private readonly string _controlPath;
    private FileStream? _stream;

    private AuditSpoolQuotaLease(string root, string controlPath, FileStream stream)
    {
        _root = root;
        _controlPath = controlPath;
        _stream = stream;
    }

    /// <summary>
    /// Creates the persistent control when a writer initializes a new spool,
    /// then waits for the same bounded interval used by the journal writer.
    /// This is the only acquisition path that may create the control. The
    /// control is published atomically with its marker already complete, so
    /// a concurrent acquirer never observes a partial control.
    /// </summary>
    internal static AuditSpoolQuotaLease CreateControlAndAcquire(
        string spoolRoot,
        Action? controlPublishCheckedForTests = null,
        Action? controlPublishedForTests = null)
    {
        var root = VerifyExistingRoot(spoolRoot);
        var controlPath = Path.Combine(root, ControlFileName);
        EnsureControlFile(
            root,
            controlPath,
            controlPublishCheckedForTests,
            controlPublishedForTests);
        return AcquireExistingCore(root, controlPath, WriterWait);
    }

    /// <summary>
    /// Waits up to thirty seconds for an already-created quota control. A
    /// missing, malformed, misprotected, or replaced control fails closed.
    /// </summary>
    internal static AuditSpoolQuotaLease AcquireExisting(string spoolRoot)
    {
        var root = VerifyExistingRoot(spoolRoot);
        var controlPath = Path.Combine(root, ControlFileName);
        return AcquireExistingCore(root, controlPath, WriterWait);
    }

    /// <summary>
    /// Non-creating, nonblocking acquisition for recovery inventory. False
    /// means only that another process owns the exact persistent control.
    /// </summary>
    internal static bool TryAcquireExisting(
        string spoolRoot,
        [NotNullWhen(true)] out AuditSpoolQuotaLease? lease)
    {
        var root = VerifyExistingRoot(spoolRoot);
        var controlPath = Path.Combine(root, ControlFileName);
        return TryAcquireExistingCore(root, controlPath, out lease, out _);
    }

    /// <summary>
    /// Re-proves that the protected control path still names this retained
    /// handle and that its frozen one-byte format has not changed.
    /// </summary>
    internal void VerifyOwnership()
    {
        var stream = Volatile.Read(ref _stream)
            ?? throw new ObjectDisposedException(nameof(AuditSpoolQuotaLease));
        VerifyRetainedControl(_root, _controlPath, stream);
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
            return;

        try
        {
            // Detect a path swap or format mutation before releasing the
            // retained lock. Callers therefore cannot silently complete a
            // quota-critical region against a replaced mutex.
            VerifyRetainedControl(_root, _controlPath, stream);
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static AuditSpoolQuotaLease AcquireExistingCore(
        string root,
        string controlPath,
        TimeSpan wait)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (TryAcquireExistingCore(
                    root,
                    controlPath,
                    out var lease,
                    out var contention))
            {
                return lease;
            }

            if (elapsed.Elapsed >= wait)
            {
                ExceptionDispatchInfo.Capture(contention!).Throw();
                throw new UnreachableException();
            }

            Thread.Sleep(RetryDelay);
        }
    }

    private static bool TryAcquireExistingCore(
        string root,
        string controlPath,
        [NotNullWhen(true)] out AuditSpoolQuotaLease? lease,
        [NotNullWhen(false)] out IOException? contention)
    {
        lease = null;
        contention = null;
        VerifyExistingControlPath(root, controlPath);

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                controlPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.WriteThrough,
                });
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            contention = exception;
            return false;
        }

        try
        {
            VerifyRetainedControl(root, controlPath, stream);
            lease = new AuditSpoolQuotaLease(root, controlPath, stream);
            stream = null;
            return true;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static string VerifyExistingRoot(string spoolRoot)
    {
        if (string.IsNullOrWhiteSpace(spoolRoot) ||
            !Path.IsPathFullyQualified(spoolRoot))
        {
            throw new ArgumentException(
                "The audit spool root must be an absolute path.",
                nameof(spoolRoot));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spoolRoot));
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        return root;
    }

    private static void EnsureControlFile(
        string root,
        string controlPath,
        Action? publishCheckedForTests,
        Action? publishedForTests)
    {
        if (EntryExists(controlPath))
            return;

        // The marker is complete and durable before the control name exists.
        // Writing it in place exposed a zero-length control: on Unix, .NET
        // emulates FileShare with flock taken after open(2), so a concurrent
        // acquirer could win the exclusive lock between the creator's open
        // and its lock, verify a zero-length control, fail closed, and leave
        // behind a control that no later start repairs (GitHub Actions run
        // 40377656597, ubuntu, 2026-10-26). The atomic no-replace publish is
        // the primitive the host identity already uses; a creator that loses
        // the publish race discards its copy and acquires the winner's.
        var temporaryPath = Path.Combine(root, $"{ControlFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath);
            try
            {
                stream.WriteByte(ControlFormatMarker);
                stream.Flush(flushToDisk: true);
                if (OperatingSystem.IsWindows())
                {
                    // The no-replace move needs the name released (the
                    // exclusive handle has no delete sharing) and is itself
                    // atomic: the name appears complete or not at all.
                    stream.Dispose();
                }

                // On POSIX the publish is a hard link followed by removal of
                // the temporary name, so the inode briefly carries two links,
                // which the exclusive acquisition rejects as an aliased
                // control (rr-10 reopen). The retained exclusive handle keeps
                // flock(LOCK_EX) on that inode across the whole publish, so an
                // acquirer that opens the published name in that interval
                // meets ordinary lock contention and retries, instead of
                // failing closed on a control that is about to be valid.
                SecureAuditStorage.PublishAtomically(
                    temporaryPath,
                    controlPath,
                    root,
                    publishCheckedForTests,
                    publishedForTests);
            }
            finally
            {
                stream.Dispose();
            }
        }
        catch (Exception exception)
            when (exception is IOException or Win32Exception && EntryExists(controlPath))
        {
            // A concurrent creator published the one persistent control. The
            // exclusive acquisition below validates its exact retained bytes
            // before trusting it.
            SecureAuditStorage.TryDelete(temporaryPath);
            return;
        }
        catch
        {
            SecureAuditStorage.TryDelete(temporaryPath);
            throw;
        }
    }

    private static void VerifyExistingControlPath(string root, string controlPath)
    {
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
        SecureAuditStorage.VerifyExternalProtectedFile(controlPath);
    }

    private static void VerifyRetainedControl(
        string root,
        string controlPath,
        FileStream stream)
    {
        VerifyExistingControlPath(root, controlPath);
        _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
            controlPath,
            stream.SafeFileHandle);

        Span<byte> marker = stackalloc byte[1];
        if (stream.Length != 1 ||
            RandomAccess.Read(stream.SafeFileHandle, marker, fileOffset: 0) != 1 ||
            marker[0] != ControlFormatMarker)
        {
            throw new IOException("The audit spool quota control is invalid.");
        }

        _ = SecureAuditStorage.VerifyRetainedProtectedFileIdentity(
            controlPath,
            stream.SafeFileHandle);
        SecureAuditStorage.VerifyExternalProtectedDirectory(root);
    }

    private static bool EntryExists(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists || file.LinkTarget is not null || Directory.Exists(path);
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var nativeCode = exception.HResult & 0xffff;
        if (OperatingSystem.IsWindows())
            return nativeCode is 32 or 33;
        if (OperatingSystem.IsLinux())
            return exception.HResult == 11 || nativeCode == 11;
        if (OperatingSystem.IsMacOS())
            return exception.HResult == 35 || nativeCode == 35;
        return false;
    }
}

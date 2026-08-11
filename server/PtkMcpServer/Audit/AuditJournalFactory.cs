using System.ComponentModel;
using System.Text;

namespace PtkMcpServer.Audit;

internal static class AuditJournalFactory
{
    private const string HostIdentityFileName = "host.id";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static AuditJournal Open(
        AuditOptions options,
        AuditHealth health,
        string producerVersion,
        string? binaryDigest = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, Guid>? uuidV7Factory = null,
        Func<FileAuditSinkFaultPoint, int, bool>? sinkFaultInjector = null,
        Action<string>? hostIdentityReadCompletedForTests = null,
        Action? hostIdentityDestinationCheckedForTests = null)
    {
        return OpenCore(
            options,
            health,
            producerVersion,
            binaryDigest,
            utcNow,
            uuidV7Factory,
            sinkFaultInjector,
            hostIdentityReadCompletedForTests,
            hostIdentityDestinationCheckedForTests);
    }

    private static AuditJournal OpenCore(
        AuditOptions options,
        AuditHealth health,
        string producerVersion,
        string? binaryDigest,
        Func<DateTimeOffset>? utcNow,
        Func<DateTimeOffset, Guid>? uuidV7Factory,
        Func<FileAuditSinkFaultPoint, int, bool>? sinkFaultInjector,
        Action<string>? hostIdentityReadCompletedForTests,
        Action? hostIdentityDestinationCheckedForTests)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        var root = SecureAuditStorage.PrepareRoot(options.RootDirectory);
        _ = SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        var hostId = LoadOrCreateHostId(
            root,
            hostIdentityReadCompletedForTests,
            hostIdentityDestinationCheckedForTests,
            out var quarantineDetail);
        // Boot lineage is deliberately NOT read here: the journal resolves
        // and publishes it atomically at its first append, under the
        // cross-process quota lease, so concurrently opened boots chain in
        // true first-append order (cr4-3).
        var supervisorBootId = Guid.NewGuid();
        var sink = new FileAuditJournalSink(
            options,
            supervisorBootId,
            utcNow,
            sinkFaultInjector);
        var journal = CreateJournalTakingSink(
            options,
            health,
            producerVersion,
            sink,
            supervisorBootId,
            hostId,
            binaryDigest,
            utcNow,
            uuidV7Factory);
        if (quarantineDetail is not null)
            journal.RecordPendingStartupQuarantine(quarantineDetail);
        return journal;
    }

    /// <summary>
    /// Reconciles every awaiting evidence artifact against a stable all-closed
    /// spool snapshot, then creates the local writer and permits ordinary
    /// retention. Both the MCP supervisor and the out-of-band admin writer
    /// must use this path.
    /// </summary>
    internal static AuditJournal OpenReconciledLocal(
        AuditOptions options,
        AuditHealth health,
        string producerVersion,
        ScriptEvidenceStoreProvider evidence)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        if (options.ProtectionMode != AuditProtectionMode.LocalOnly)
        {
            throw new ArgumentException(
                "Reconciled local journal startup requires local-only audit options.",
                nameof(options));
        }

        AuditEvidenceOrphanReconciler.RequireCompleteBeforeWriter(
            options,
            health,
            evidence);
        return OpenCore(
            options,
            health,
            producerVersion,
            binaryDigest: null,
            utcNow: null,
            uuidV7Factory: null,
            sinkFaultInjector: null,
            hostIdentityReadCompletedForTests: null,
            hostIdentityDestinationCheckedForTests: null);
    }

    /// <summary>
    /// Completes journal construction around an already activated staged
    /// anchored sink. Sink ownership transfers on entry, including failure.
    /// </summary>
    internal static AuditJournal OpenActivatedAnchored(
        AuditOptions options,
        AuditHealth health,
        string producerVersion,
        FileAuditJournalSink sink,
        string? binaryDigest = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, Guid>? uuidV7Factory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            sink.Dispose();
            throw new ArgumentException(
                "An activated anchored sink requires anchored audit options.",
                nameof(options));
        }

        Guid hostId;
        Guid supervisorBootId;
        string? anchoredQuarantineDetail;
        try
        {
            var root = SecureAuditStorage.PrepareRoot(options.RootDirectory);
            _ = SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
            hostId = LoadOrCreateHostId(root, null, null, out anchoredQuarantineDetail);
            supervisorBootId = sink.CurrentSegmentIdentity.SupervisorBootId;
        }
        catch
        {
            sink.Dispose();
            throw;
        }
        var anchoredJournal = CreateJournalTakingSink(
            options,
            health,
            producerVersion,
            sink,
            supervisorBootId,
            hostId,
            binaryDigest,
            utcNow,
            uuidV7Factory);
        if (anchoredQuarantineDetail is not null)
            anchoredJournal.RecordPendingStartupQuarantine(anchoredQuarantineDetail);
        return anchoredJournal;
    }

    private static AuditJournal CreateJournalTakingSink(
        AuditOptions options,
        AuditHealth health,
        string producerVersion,
        FileAuditJournalSink sink,
        Guid supervisorBootId,
        Guid hostId,
        string? binaryDigest,
        Func<DateTimeOffset>? utcNow,
        Func<DateTimeOffset, Guid>? uuidV7Factory)
    {
        try
        {
            return new AuditJournal(
                options,
                health,
                sink,
                producerVersion,
                binaryDigest,
                hostId,
                supervisorBootId,
                previousSupervisorBootId: null,
                utcNow,
                uuidV7Factory);
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }

    internal const string HostIdentityQuarantineDetailCode =
        "quarantine.host_identity";

    private static Guid LoadOrCreateHostId(
        string root,
        Action<string>? readCompletedForTests,
        Action? destinationCheckedForTests,
        out string? quarantineDetail)
    {
        quarantineDetail = null;
        var publishedPath = Path.Combine(root, HostIdentityFileName);
        if (PathExists(publishedPath))
        {
            try
            {
                return ReadHostId(publishedPath, readCompletedForTests);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                quarantineDetail = HostIdentityQuarantineDetailCode;
                // A fuckup cannot be globally terminal (owner ruling,
                // audit-restoration contract rule 3): a corrupt, foreign, or
                // unprotected identity artifact is preserved as quarantine
                // evidence and a fresh identity is minted. Only a failure to
                // quarantine — genuine local write inability — still blocks,
                // by rethrowing the original validation failure.
                QuarantineHostIdentity(root, publishedPath, exception);
            }
        }

        var hostId = Guid.NewGuid();
        var temporaryPath = Path.Combine(root, $".{HostIdentityFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
            {
                var bytes = Encoding.ASCII.GetBytes(hostId.ToString("D") + "\n");
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                SecureAuditStorage.PublishAtomically(
                    temporaryPath,
                    publishedPath,
                    root,
                    destinationCheckedForTests);
                return hostId;
            }
            catch (Exception exception) when (
                IsConcurrentPublishCollision(exception) &&
                PathExists(publishedPath))
            {
                SecureAuditStorage.TryDelete(temporaryPath);
                return ReadHostId(publishedPath, readCompletedForTests);
            }
        }
        catch
        {
            SecureAuditStorage.TryDelete(temporaryPath);
            throw;
        }
    }

    private static Guid ReadHostId(
        string path,
        Action<string>? readCompletedForTests)
    {
        // A RETAINED identity is validated, never repaired: on Windows,
        // VerifyProtectedFile re-applies the owner/DACL, which silently
        // adopted a foreign or over-permissive host.id instead of routing it
        // to quarantine (cr2-1). The non-mutating external boundary makes
        // both platforms validate; protection is applied only to files this
        // process creates.
        SecureAuditStorage.VerifyExternalProtectedFile(path);

        byte[] bytes;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 128,
                   FileOptions.SequentialScan))
        {
            if (stream.Length != 37)
                throw new IOException("The persisted audit host identity is invalid.");
            bytes = new byte[37];
            stream.ReadExactly(bytes);
        }

        readCompletedForTests?.Invoke(path);
        SecureAuditStorage.VerifyExternalProtectedFile(path);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new IOException("The persisted audit host identity is invalid.");
        }

        if (text[^1] != '\n' ||
            !Guid.TryParseExact(text.AsSpan(0, 36), "D", out var hostId) ||
            hostId.ToString("D") != text[..36] ||
            text[14] != '4' ||
            text[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw new IOException("The persisted audit host identity is invalid.");
        }
        return hostId;
    }

    internal const string QuarantineDirectoryName = "quarantine";

    private static void QuarantineHostIdentity(
        string root,
        string publishedPath,
        Exception validationFailure)
    {
        try
        {
            var quarantineDirectory = SecureAuditStorage.PrepareRoot(
                Path.Combine(root, QuarantineDirectoryName));
            var target = Path.Combine(
                quarantineDirectory,
                $"{HostIdentityFileName}.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}");
            File.Move(publishedPath, target);
            Console.Error.WriteLine(
                $"[ptk audit] quarantined an invalid host identity artifact to '{target}' " +
                $"({validationFailure.Message}); a fresh host identity will be created.");
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Quarantine itself failed: this is real local write inability,
            // the one condition that blocks. Surface the original validation
            // failure as the cause.
            throw new IOException(
                "The persisted audit host identity is invalid and could not be quarantined.",
                new AggregateException(validationFailure, exception));
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static bool PathExists(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists || file.LinkTarget is not null;
    }

    internal static bool IsConcurrentPublishCollision(Exception exception) =>
        exception is IOException ||
        exception is Win32Exception
        {
            NativeErrorCode: ErrorUnixFileExists or ErrorFileExists or ErrorAlreadyExists,
        };

    private const int ErrorUnixFileExists = 17;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
}

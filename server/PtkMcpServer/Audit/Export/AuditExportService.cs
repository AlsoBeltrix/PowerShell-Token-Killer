using Microsoft.Extensions.Hosting;

namespace PtkMcpServer.Audit.Export;

/// <summary>
/// Drains the local audit spool to the configured destination in the
/// background, at least once, advancing a durable cursor only after the
/// destination accepts a batch.
///
/// This service is deliberately incapable of gating execution (contract
/// rule 2, owner ruling 2026-08-10: "SIEM connected != stop logging locally.
/// some idiot rebooting the splunk server shouldn't crash every coding
/// session"): it holds no admission lease, shares no lock with the journal
/// writer, reads segments as an ordinary reader, and swallows every delivery
/// failure into bounded retry plus health reporting.
/// </summary>
internal sealed class AuditExportService : IHostedService, IAsyncDisposable
{
    internal const int MaximumBatchRecords = 256;
    internal const int MaximumBatchBytes = 1024 * 1024;

    private readonly AuditOptions _options;
    private readonly IAuditDestination? _destination;
    private readonly AuditExportCursorStore _cursorStore;
    private readonly AuditExportGapStore _gapStore;
    private readonly AuditExportHealth _health;
    private readonly TimeSpan _idleInterval;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maximumRetryDelay;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _pump;
    private int _disposed;

    internal AuditExportService(
        AuditOptions options,
        IAuditDestination? destination,
        AuditExportCursorStore cursorStore,
        AuditExportHealth health,
        TimeSpan? idleInterval = null,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cursorStore);
        ArgumentNullException.ThrowIfNull(health);
        _options = options;
        _destination = destination;
        _cursorStore = cursorStore;
        _gapStore = new AuditExportGapStore(options.RootDirectory);
        _health = health;
        // Gaps recorded by earlier processes are evidence and stay visible.
        var retained = _gapStore.Read();
        if (retained.Count > 0) _health.SetExportGaps(retained.Count);
        _idleInterval = idleInterval ?? TimeSpan.FromSeconds(2);
        _initialRetryDelay = initialRetryDelay ?? TimeSpan.FromSeconds(5);
        _maximumRetryDelay = maximumRetryDelay ?? TimeSpan.FromMinutes(5);
        if (_destination is not null)
            _health.SetConfigured(_destination.Describe());
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_destination is null) return Task.CompletedTask;
        _pump = Task.Run(() => PumpAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_pump is null) return;
        try
        {
            await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown must not wait on a destination; undelivered records
            // stay spooled and export resumes on the next start.
        }
    }

    /// <summary>One drain pass; separated so tests drive delivery
    /// deterministically instead of racing the timer.</summary>
    internal async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        if (_destination is null) return 0;
        var delivered = 0;
        var cursor = _cursorStore.Read();
        var segments = EnumerateSegments();
        _health.RecordPendingBytes(PendingBytes(segments, cursor));

        // Local spool retention can delete a closed segment this exporter has
        // not delivered yet (journal retention is age/capacity driven and does
        // not consult the export cursor). Resuming silently at the oldest
        // surviving segment would hide permanently lost custody, so the gap is
        // recorded durably and reported instead (cr3-2). A segment already
        // consumed to its end is NOT a gap — deleting fully delivered records
        // is retention working correctly.
        if (cursor.SegmentFileName is not null &&
            !cursor.SegmentCompleted &&
            segments.Length > 0 &&
            !segments.Any(file => string.Equals(
                file.Name,
                cursor.SegmentFileName,
                StringComparison.Ordinal)))
        {
            var record = _gapStore.Record(cursor.SegmentFileName);
            _health.SetExportGaps(record.Count);
        }

        foreach (var segment in SegmentsFrom(segments, cursor))
        {
            var isCursorSegment = string.Equals(
                segment.Name,
                cursor.SegmentFileName,
                StringComparison.Ordinal);
            var startOffset = isCursorSegment ? cursor.ByteOffset : 0;

            // Undelivered bytes clear the completed flag immediately, before
            // any delivery is attempted: if this segment is removed while a
            // tail is outstanding, that is real lost custody and must be
            // reported as a gap even though it was once fully delivered.
            if (isCursorSegment &&
                cursor.SegmentCompleted &&
                !ReachedEndOfSegment(segment.FullName, cursor.ByteOffset))
            {
                cursor = cursor with { SegmentCompleted = false };
                _cursorStore.TryWrite(cursor);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = ReadBatch(
                    segment.FullName,
                    startOffset,
                    out var nextOffset,
                    out var readFailure);
                if (readFailure is not null)
                {
                    // Never silently report healthy while delivering nothing
                    // (cr3-1): an unreadable segment — including the live one,
                    // which the journal writer holds exclusively — is a
                    // visible export failure, not an empty segment.
                    _health.RecordFailure(readFailure);
                    break;
                }
                if (batch.Count == 0) break;

                var result = await _destination
                    .DeliverAsync(batch, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Disposition == AuditDeliveryDisposition.Retryable)
                {
                    _health.RecordFailure(result.DetailCode ?? "export.failed");
                    return delivered;
                }
                if (result.Disposition == AuditDeliveryDisposition.Permanent)
                {
                    // A refusal is isolated to the offending record, never
                    // applied to the whole batch (cr3-5): every record is
                    // retried individually, so one poison record costs one
                    // record, not up to MaximumBatchRecords of custody. A
                    // record that is individually refused is reported and
                    // stepped over — the local journal remains complete.
                    var isolated = await DeliverIndividuallyAsync(
                        batch,
                        result.DetailCode ?? "export.refused",
                        cancellationToken).ConfigureAwait(false);
                    if (isolated.Retry) return delivered;
                    delivered += isolated.Delivered;
                }
                else
                {
                    delivered += batch.Count;
                    _health.RecordDelivery(batch.Count, DateTimeOffset.UtcNow);
                }

                startOffset = nextOffset;
                cursor = new AuditExportCursor(segment.Name, nextOffset);
                _cursorStore.TryWrite(cursor);
            }

            // Completion is end-of-file, not "a newer segment exists": after
            // rotation the exporter may hold a cursor on a fully delivered
            // segment that retention then removes, and calling that lost
            // custody would cry wolf on every healthy install (cr3-2
            // verification). Bytes appended later simply clear the flag on
            // the next successful read.
            if (string.Equals(cursor.SegmentFileName, segment.Name, StringComparison.Ordinal))
            {
                var completed = ReachedEndOfSegment(segment.FullName, cursor.ByteOffset);
                if (completed != cursor.SegmentCompleted)
                {
                    cursor = cursor with { SegmentCompleted = completed };
                    _cursorStore.TryWrite(cursor);
                }
            }
        }

        _health.RecordPendingBytes(PendingBytes(EnumerateSegments(), cursor));
        return delivered;
    }

    /// <summary>
    /// Re-delivers a permanently refused batch one record at a time so the
    /// refusal lands on the record that caused it. A retryable answer during
    /// isolation aborts the pass: the cursor stays put and the whole range is
    /// retried, because partial progress cannot be represented in a
    /// single-offset cursor.
    /// </summary>
    private async Task<(bool Retry, int Delivered)> DeliverIndividuallyAsync(
        IReadOnlyList<string> batch,
        string batchDetailCode,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 1)
        {
            _health.RecordFailure(batchDetailCode);
            return (false, 0);
        }

        var delivered = 0;
        var refused = 0;
        foreach (var record in batch)
        {
            var single = await _destination!
                .DeliverAsync([record], cancellationToken)
                .ConfigureAwait(false);
            switch (single.Disposition)
            {
                case AuditDeliveryDisposition.Delivered:
                    delivered++;
                    break;
                case AuditDeliveryDisposition.Retryable:
                    _health.RecordFailure(single.DetailCode ?? "export.failed");
                    return (true, delivered);
                default:
                    refused++;
                    _health.RecordFailure(single.DetailCode ?? batchDetailCode);
                    break;
            }
        }

        if (delivered > 0)
            _health.RecordDelivery(delivered, DateTimeOffset.UtcNow);
        if (refused > 0)
            _health.RecordFailure(batchDetailCode);
        return (false, delivered);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var retryDelay = _initialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var failedBefore = _health.Snapshot().ConsecutiveFailures;
            TimeSpan delay;
            try
            {
                await DrainOnceAsync(cancellationToken).ConfigureAwait(false);
                var failedAfter = _health.Snapshot().ConsecutiveFailures;
                if (failedAfter > failedBefore)
                {
                    delay = retryDelay;
                    retryDelay = Min(retryDelay * 2, _maximumRetryDelay);
                }
                else
                {
                    retryDelay = _initialRetryDelay;
                    delay = _idleInterval;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // An exporter defect must never reach the audited path. It
                // degrades to retry plus visible health, nothing more.
                _health.RecordFailure("export.pump_fault");
                delay = retryDelay;
                retryDelay = Min(retryDelay * 2, _maximumRetryDelay);
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private FileInfo[] EnumerateSegments()
    {
        try
        {
            var directory = new DirectoryInfo(_options.SpoolDirectory);
            if (!directory.Exists) return [];
            return directory
                .GetFiles("*.jsonl")
                .Where(file => AuditSpoolSegmentIdentity.TryParse(file.Name, out _))
                .OrderBy(file => file.CreationTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return [];
        }
    }

    /// <summary>
    /// Segments at or after the cursor's segment. A cursor naming a segment
    /// that retention has since removed restarts from the oldest retained
    /// segment: re-delivery is contractually fine, a silent gap is not.
    /// </summary>
    private static IEnumerable<FileInfo> SegmentsFrom(
        FileInfo[] segments,
        AuditExportCursor cursor)
    {
        if (cursor.SegmentFileName is null) return segments;
        var index = Array.FindIndex(
            segments,
            file => string.Equals(file.Name, cursor.SegmentFileName, StringComparison.Ordinal));
        return index < 0 ? segments : segments.Skip(index);
    }

    private static long PendingBytes(FileInfo[] segments, AuditExportCursor cursor)
    {
        long pending = 0;
        foreach (var segment in SegmentsFrom(segments, cursor))
        {
            var consumed = string.Equals(
                segment.Name,
                cursor.SegmentFileName,
                StringComparison.Ordinal)
                ? cursor.ByteOffset
                : 0;
            try
            {
                pending += Math.Max(0, segment.Length - consumed);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // A segment that vanished mid-scan contributes nothing.
            }
        }
        return pending;
    }

    /// <summary>
    /// Reads whole JSONL records only. A partially written trailing line is
    /// left for the next pass, so a record is never delivered torn.
    /// </summary>
    private static List<string> ReadBatch(
        string path,
        long startOffset,
        out long nextOffset,
        out string? readFailure)
    {
        var records = new List<string>();
        nextOffset = startOffset;
        readFailure = null;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (startOffset > stream.Length) return records;
            stream.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

            var consumed = startOffset;
            var batchBytes = 0;
            while (records.Count < MaximumBatchRecords && batchBytes < MaximumBatchBytes)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line);
                // ReadLine cannot distinguish a complete final line from a
                // torn one, so a trailing line without its newline is left
                // behind: consumed only advances across newline-terminated
                // records.
                if (consumed + lineBytes >= stream.Length && !EndsWithNewline(path, stream.Length))
                    break;
                if (line.Length == 0)
                {
                    consumed += 1;
                    continue;
                }
                records.Add(line);
                consumed += lineBytes + 1;
                batchBytes += lineBytes + 1;
            }
            nextOffset = consumed;
        }
        catch (IOException)
        {
            // The journal writer holds the LIVE segment with FileShare.None,
            // and that exclusivity is load-bearing for the writer's own
            // live-vs-closed classification — so the live tail is currently
            // exportable only after rotation. Surfaced, never silent; the
            // coordinated-reader fix is its own slice (cr3-1 / R3d).
            records.Clear();
            nextOffset = startOffset;
            readFailure = "export.segment_unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            records.Clear();
            nextOffset = startOffset;
            readFailure = "export.segment_unreadable";
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            records.Clear();
            nextOffset = startOffset;
            readFailure = "export.segment_read_fault";
        }
        return records;
    }

    /// <summary>Whether delivery consumed the segment to its current end.</summary>
    private static bool ReachedEndOfSegment(string path, long consumedBytes)
    {
        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            return file.Exists && consumedBytes >= file.Length;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Unknown means "not proven delivered": prefer a false gap alarm
            // over silently dropping evidence of real loss.
            return false;
        }
    }

    private static bool EndsWithNewline(string path, long length)
    {
        if (length == 0) return false;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(-1, SeekOrigin.End);
            return stream.ReadByte() == '\n';
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
        left < right ? left : right;

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (Exception exception) when (!IsFatal(exception)) { }
        }
        _stopping.Dispose();
        _destination?.Dispose();
    }
}

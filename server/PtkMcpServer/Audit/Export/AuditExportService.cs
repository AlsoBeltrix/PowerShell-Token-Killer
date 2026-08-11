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
    private readonly Func<AuditJournal?>? _liveJournalSource;
    private readonly AuditExportLease _lease = new();
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
        TimeSpan? maximumRetryDelay = null,
        Func<AuditJournal?>? liveJournalSource = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cursorStore);
        ArgumentNullException.ThrowIfNull(health);
        _options = options;
        _destination = destination;
        _cursorStore = cursorStore;
        _gapStore = new AuditExportGapStore(options.RootDirectory);
        _health = health;
        _liveJournalSource = liveJournalSource;
        // Gaps recorded by earlier processes are evidence and stay visible,
        // including any that an earlier process could not write to the
        // ledger and parked on the cursor instead (cr3-2 round 9).
        var retained = _gapStore.ReadOrQuarantine(out var retainedWasCorrupt);
        if (retainedWasCorrupt)
        {
            // A corrupt ledger BEHIND a healthy cursor destroyed proved gaps
            // silently, because quarantine only fired when the cursor lacked
            // a position (Fable-5 review finding 2). Losing the evidence is
            // itself reportable, at startup as well as mid-drain.
            _health.RecordUnverifiedBootBoundary("ledger-unreadable", 0);
        }
        if (retained.RefusedRecords > 0)
            _health.SetRefusedRecords(retained.RefusedRecords);
        var parked = _cursorStore.Read();
        _unrecordedGaps = parked.UnrecordedGaps;
        _unrecordedMissingRecords = parked.UnrecordedMissingRecords;
        var totalGaps = retained.Count + _unrecordedGaps;
        if (totalGaps > 0)
        {
            _health.SetExportGaps(
                totalGaps,
                retained.MissingRecords + _unrecordedMissingRecords);
        }
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
        // Exactly one exporter per audit root (cr4-4): the durable cursor and
        // gap ledger are single-writer artifacts, and every supervisor on the
        // root runs this service. Standby is quiet, retried each pump tick,
        // and never a failure.
        if (!_lease.TryAcquire(_options.RootDirectory))
        {
            _health.SetStandby(true);
            return 0;
        }
        _health.SetStandby(false);

        var delivered = 0;
        var cursor = _cursorStore.Read();

        // Migrate gap counters an earlier process could not persist. The
        // round-9 fix parked them on the cursor but never folded them back,
        // so a later cursor loss erased the evidence (cr3-2 round 10).
        if (_unrecordedGaps > 0 || _unrecordedMissingRecords > 0)
        {
            if (_gapStore.TryAbsorbUnrecorded(_unrecordedGaps, _unrecordedMissingRecords))
            {
                _unrecordedGaps = 0;
                _unrecordedMissingRecords = 0;
                cursor = cursor with
                {
                    UnrecordedGaps = 0,
                    UnrecordedMissingRecords = 0,
                };
                _cursorStore.TryWrite(cursor);
            }
        }

        // If export metadata cannot be persisted at all, delivering would
        // advance nothing durably and any loss proved in this pass would die
        // at the next restart. Export pauses BEFORE delivering rather than
        // after (Fable-5 review finding 3, which falsified the round-9
        // "execution stops first" argument: the spool lives in a
        // subdirectory and keeps working while both stores are blocked).
        if (!_cursorStore.TryWrite(cursor))
        {
            _health.RecordFailure("export.metadata_unwritable");
            return 0;
        }

        // Delivery is PER BOOT with per-boot durable positions (cr4-4
        // reopen): the earlier single linear cursor rode a cross-boot
        // ordering keyed on the remaining files, which mutated whenever
        // retention deleted delivered segments — letting the cursor both
        // skip undelivered segments and expose them to retention. Per-boot
        // positions are order-independent: a blocked or still-growing boot
        // halts only ITSELF, and no other boot's progress can move past it.
        var groups = EnumerateBootGroups();
        _health.RecordPendingBytes(PendingBytes(groups, cursor));
        var ledger = _gapStore.ReadOrQuarantine(out var ledgerWasCorrupt);
        if (ledgerWasCorrupt)
            _health.RecordUnverifiedBootBoundary("ledger-unreadable", 0);

        // Lineage attestations observed this drain, judged at the end
        // against everything then known (a claimed predecessor may simply
        // deliver later in this same pass).
        var lineageClaims = new Dictionary<string, string>(StringComparer.Ordinal);
        var incompleteBoots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var bootKey = group.BootId.ToString("D");
            var position = cursor.For(group.BootId);
            var prior = PriorChain(group.BootId, position, ledger);
            var bootHalted = false;
            var stillGrowing = false;

            foreach (var segment in group.Segments)
            {
                if (!AuditSpoolSegmentIdentity.TryParse(segment.Name, out var identity))
                    continue;
                if (position?.SegmentFileName is not null &&
                    AuditSpoolSegmentIdentity.TryParse(
                        position.SegmentFileName,
                        out var positionIdentity) &&
                    identity.Index < positionIdentity.Index)
                {
                    // Fully consumed on an earlier drain.
                    continue;
                }
                var startOffset = position is not null &&
                    string.Equals(segment.Name, position.SegmentFileName, StringComparison.Ordinal)
                    ? position.ByteOffset
                    : 0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var batch = ReadBatch(
                        segment.FullName,
                        startOffset,
                        out var nextOffset,
                        out var readFailure);
                    if (readFailure is not null)
                    {
                        // The journal writer holds the live segment
                        // FileShare.None, and that exclusivity is
                        // load-bearing for its own live-vs-closed
                        // classification — so the live tail is read through
                        // the writer's OWN handle instead, bounded by the
                        // durable flush watermark (cr3-1/R3d). Offsets are
                        // file offsets either way, so after rotation the
                        // ordinary file read continues from the same
                        // position seamlessly.
                        if (TryReadLiveCommitted(
                                segment.Name,
                                startOffset,
                                out batch,
                                out nextOffset))
                        {
                            // Quiet at the committed tail; still growing, so
                            // this BOOT stops here for this pass.
                            stillGrowing = true;
                        }
                        else
                        {
                            // Visible (cr3-1), and it halts only THIS BOOT
                            // (cr4-4): a foreign supervisor's live segment
                            // stays unreadable until it rotates, while every
                            // other boot's delivery proceeds on its own
                            // position.
                            _health.RecordFailure(readFailure);
                            bootHalted = true;
                            break;
                        }
                    }
                    if (batch.Count == 0) break;

                    // Loss is proved by the chain itself, not by which files
                    // still exist: a jump in this boot's contiguous sequence
                    // means records between the last delivered one and this
                    // one were removed before delivery, whatever retention
                    // or rotation did to the segments (cr3-2).
                    prior = WalkChain(bootKey, prior, batch, lineageClaims);

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
                        // A refusal is isolated to the offending record,
                        // never applied to the whole batch (cr3-5): every
                        // record is retried individually, so one poison
                        // record costs one record. A record that is
                        // individually refused is reported and stepped over
                        // — the local journal remains complete.
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
                    position = new AuditExportBootPosition(
                        segment.Name,
                        nextOffset,
                        prior.Has ? prior.Sequence : 0,
                        prior.Has && prior.Terminal,
                        DateTimeOffset.UtcNow);
                    cursor = cursor.WithBoot(group.BootId, position) with
                    {
                        UnrecordedGaps = _unrecordedGaps,
                        UnrecordedMissingRecords = _unrecordedMissingRecords,
                    };
                    if (!_cursorStore.TryWrite(cursor))
                    {
                        // Neither export metadata path can persist progress
                        // or evidence. Export PAUSES here and says so
                        // (cr3-2 round 10). Pausing export never gates
                        // execution; the local journal stays complete.
                        _health.RecordFailure("export.metadata_unwritable");
                        return delivered;
                    }
                    // Mirrored into the durable ledger so losing the cursor
                    // does not erase boot memory (cr3-2 round 5).
                    if (prior.Has)
                    {
                        _gapStore.RecordChainPosition(
                            bootKey,
                            prior.Sequence,
                            prior.Terminal);
                    }
                }

                if (bootHalted || stillGrowing) break;
            }

            if (bootHalted || stillGrowing) incompleteBoots.Add(bootKey);
        }

        JudgeLineageClaims(lineageClaims, cursor, ledger, groups, incompleteBoots);
        _health.RecordPendingBytes(PendingBytes(EnumerateBootGroups(), cursor));
        return delivered;
    }

    private readonly record struct ChainPrior(bool Has, long Sequence, bool Terminal);

    private ChainPrior PriorChain(
        Guid bootId,
        AuditExportBootPosition? position,
        AuditExportGapRecord ledger)
    {
        if (position is not null && position.LastSequence > 0)
            return new ChainPrior(true, position.LastSequence, position.LastWasLifecycleTerminal);
        // The durable ledger remembers boots whose cursor entry was lost
        // (cr3-2 round 5).
        if (ledger.Chains.TryGetValue(bootId.ToString("D"), out var memory))
            return new ChainPrior(true, memory.Sequence, memory.Terminal);
        return default;
    }

    /// <summary>
    /// Walks one delivered batch for chain continuity within its boot,
    /// recording proved gaps, collecting lineage attestations, and returning
    /// the advanced chain position. Records whose embedded boot id does not
    /// match the segment's boot contribute nothing: file placement is not
    /// trusted to overrule the walk's per-boot bookkeeping, and such records
    /// are still delivered.
    /// </summary>
    private ChainPrior WalkChain(
        string bootKey,
        ChainPrior prior,
        IReadOnlyList<string> batch,
        IDictionary<string, string> lineageClaims)
    {
        foreach (var record in batch)
        {
            var (bootId, sequence, isTerminal, previousBootId) = ChainPosition(record);
            if (bootId is null || sequence is null) continue;
            if (!string.Equals(bootId, bootKey, StringComparison.Ordinal)) continue;

            if (previousBootId is not null &&
                !string.Equals(previousBootId, bootKey, StringComparison.Ordinal) &&
                !lineageClaims.ContainsKey(previousBootId))
            {
                lineageClaims[previousBootId] = bootKey;
            }

            if (!prior.Has)
            {
                // Every chain starts at 1, so a first observed record above 1
                // proves its prefix is gone — whether this is a first run, a
                // lost cursor, or a lost ledger.
                if (sequence > 1)
                    RecordGap($"{bootKey}:1-{sequence.Value - 1}", sequence.Value - 1);
            }
            else if (sequence > prior.Sequence + 1)
            {
                RecordGap(
                    $"{bootKey}:{prior.Sequence + 1}-{sequence.Value - 1}",
                    sequence.Value - prior.Sequence - 1);
            }

            prior = new ChainPrior(true, sequence.Value, isTerminal);
        }
        return prior;
    }

    /// <summary>
    /// Judges the lineage attestations seen this drain (cr4-4 rework of the
    /// boundary heuristics — per-boot traversal has no "crossing" moment, and
    /// lineage is the stronger signal anyway):
    /// - a claimed predecessor with NO chain memory and NO spool files is a
    ///   wholly vanished boot (the cr3-2 open finding's shape) — unverified
    ///   boundary;
    /// - a claimed predecessor whose delivered chain never reached its
    ///   lifecycle terminal, and which is no longer growing or blocked, ended
    ///   without its terminal — unverified boundary (the successor's
    ///   existence plus the dead spool proves it ended; a concurrent ALIVE
    ///   predecessor always presents a locked or growing newest segment and
    ///   is exempt);
    /// - a claimed predecessor that is merely undelivered-but-present, or
    ///   still alive, raises nothing.
    /// </summary>
    private void JudgeLineageClaims(
        IReadOnlyDictionary<string, string> lineageClaims,
        AuditExportCursor cursor,
        AuditExportGapRecord ledger,
        IReadOnlyList<BootGroup> groups,
        IReadOnlySet<string> incompleteBoots)
    {
        if (lineageClaims.Count == 0) return;
        var present = groups
            .Select(group => group.BootId.ToString("D"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (claimed, _) in lineageClaims)
        {
            ChainPrior chain = default;
            if (Guid.TryParseExact(claimed, "D", out var claimedBootId))
            {
                chain = PriorChain(claimedBootId, cursor.For(claimedBootId), ledger);
            }

            if (!chain.Has)
            {
                if (!present.Contains(claimed))
                    _health.RecordUnverifiedBootBoundary(claimed, 0);
                continue;
            }
            if (chain.Terminal) continue;
            // An alive predecessor always presents a locked or growing
            // newest segment this drain; anything else with a successor and
            // no terminal has ended unverified.
            if (incompleteBoots.Contains(claimed)) continue;
            _health.RecordUnverifiedBootBoundary(claimed, chain.Sequence);
        }
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
            _health.SetRefusedRecords(_gapStore.RecordRefusedRecord().RefusedRecords);
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
                    // Durable: a refused record is never delivered, so the
                    // signal must outlive the next success and a restart
                    // (Fable-5 review finding 4).
                    _health.SetRefusedRecords(_gapStore.RecordRefusedRecord().RefusedRecords);
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

    private sealed record BootGroup(Guid BootId, IReadOnlyList<FileInfo> Segments);

    /// <summary>
    /// Segments grouped per supervisor boot, indexes ascending within each
    /// boot; groups in a STABLE arbitrary order (boot id). Delivery keeps a
    /// position per boot, so no cross-boot order is load-bearing — which is
    /// the point: the cr4-4 reopen showed any order keyed on the remaining
    /// files mutates when retention deletes delivered segments.
    /// </summary>
    private IReadOnlyList<BootGroup> EnumerateBootGroups()
    {
        try
        {
            var directory = new DirectoryInfo(_options.SpoolDirectory);
            if (!directory.Exists) return [];
            return directory
                .GetFiles("*.jsonl")
                .Select(file => (File: file,
                    Parsed: AuditSpoolSegmentIdentity.TryParse(file.Name, out var identity),
                    Identity: identity))
                .Where(entry => entry.Parsed)
                .GroupBy(entry => entry.Identity.SupervisorBootId)
                .OrderBy(group => group.Key)
                .Select(group => new BootGroup(
                    group.Key,
                    group.OrderBy(entry => entry.Identity.Index)
                        .Select(entry => entry.File)
                        .ToArray()))
                .ToArray();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return [];
        }
    }

    private static long PendingBytes(
        IReadOnlyList<BootGroup> groups,
        AuditExportCursor cursor)
    {
        long pending = 0;
        foreach (var group in groups)
        {
            var position = cursor.For(group.BootId);
            var positionIndex = -1;
            if (position?.SegmentFileName is not null &&
                AuditSpoolSegmentIdentity.TryParse(position.SegmentFileName, out var identity))
            {
                positionIndex = identity.Index;
            }
            foreach (var segment in group.Segments)
            {
                if (!AuditSpoolSegmentIdentity.TryParse(segment.Name, out var segmentIdentity))
                    continue;
                try
                {
                    if (segmentIdentity.Index < positionIndex) continue;
                    var consumed = segmentIdentity.Index == positionIndex
                        ? position!.ByteOffset
                        : 0;
                    pending += Math.Max(0, segment.Length - consumed);
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    // A segment that vanished mid-scan contributes nothing.
                }
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

    /// <summary>
    /// Reads the live segment's durably committed prefix through the journal
    /// writer's own handle. True when the journal authoritatively answered
    /// for this segment (records, or "at the committed tail" with an empty
    /// batch); false when there is no live source, the segment is not the
    /// journal's current one, or the read failed — the caller then reports
    /// the original file-read failure exactly as before. Only complete
    /// LF-terminated records are consumed, and the committed watermark always
    /// sits on a record boundary, so a record is never delivered torn.
    /// </summary>
    private bool TryReadLiveCommitted(
        string segmentFileName,
        long startOffset,
        out List<string> records,
        out long nextOffset)
    {
        records = [];
        nextOffset = startOffset;
        if (_liveJournalSource is null) return false;
        if (!AuditSpoolSegmentIdentity.TryParse(segmentFileName, out var identity))
            return false;
        try
        {
            var journal = _liveJournalSource();
            if (journal is null) return false;
            var answered = false;
            var batchBytes = 0;
            while (records.Count < MaximumBatchRecords && batchBytes < MaximumBatchBytes)
            {
                var read = journal.ReadCommittedSpool(
                    identity,
                    nextOffset,
                    journal.Options.MaxRecordBytes);
                if (read.Status == AuditCommittedSpoolReadStatus.AtCommittedTail)
                {
                    answered = true;
                    break;
                }
                if (read.Status != AuditCommittedSpoolReadStatus.Data ||
                    read.Bytes.IsEmpty)
                {
                    // Rotated, writer closed, or not the current segment: the
                    // ordinary closed-file read owns it (now or next drain).
                    break;
                }

                var span = read.Bytes.Span;
                var consumed = 0;
                while (records.Count < MaximumBatchRecords && batchBytes < MaximumBatchBytes)
                {
                    var lineFeed = span[consumed..].IndexOf((byte)'\n');
                    if (lineFeed < 0) break;
                    if (lineFeed > 0)
                    {
                        records.Add(System.Text.Encoding.UTF8.GetString(
                            span.Slice(consumed, lineFeed)));
                    }
                    consumed += lineFeed + 1;
                    batchBytes += lineFeed + 1;
                }
                // A committed chunk with no complete line means a record
                // wider than the read bound — not this reader's call to
                // guess; fall back to the visible failure.
                if (consumed == 0) break;
                nextOffset += consumed;
                answered = true;
            }
            if (!answered)
            {
                records = [];
                nextOffset = startOffset;
            }
            return answered;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // The live read is additive: any fault degrades to the exact
            // pre-existing behaviour (a reported unreadable segment), never
            // to a torn or duplicated delivery.
            records = [];
            nextOffset = startOffset;
            return false;
        }
    }

    private void RecordGap(string gapKey, long missingRecords)
    {
        var record = _gapStore.Record(gapKey, missingRecords, out var persisted);
        _health.SetExportGaps(record.Count, record.MissingRecords);
        if (!persisted)
        {
            // The ledger could not be rewritten. The cursor still advances
            // (the batch WAS delivered), so without parking the evidence
            // here a restart would silently return to healthy (cr3-2 round
            // 9). The counters ride the cursor write that follows every
            // delivery and are flushed into the ledger when it recovers.
            _unrecordedGaps += 1;
            _unrecordedMissingRecords += Math.Max(0, missingRecords);
        }
    }

    private long _unrecordedGaps;
    private long _unrecordedMissingRecords;

    /// <summary>The record's per-boot chain position, whether it is the
    /// lifecycle terminal, and the predecessor boot its lineage attests — or
    /// nulls when the line is not parseable; an undecorated record is still
    /// delivered.</summary>
    private static (string? BootId, long? Sequence, bool IsTerminal, string? PreviousBootId)
        ChainPosition(string record)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(record);
            var root = document.RootElement;
            long? sequence = root.TryGetProperty("sequence", out var sequenceElement) &&
                sequenceElement.ValueKind == System.Text.Json.JsonValueKind.Number &&
                sequenceElement.TryGetInt64(out var parsedSequence)
                ? parsedSequence
                : null;
            var hasProducer = root.TryGetProperty("producer", out var producer) &&
                producer.ValueKind == System.Text.Json.JsonValueKind.Object;
            string? bootId = hasProducer &&
                producer.TryGetProperty("supervisor_boot_id", out var bootElement) &&
                bootElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? bootElement.GetString()
                : null;
            string? previousBootId = hasProducer &&
                producer.TryGetProperty("previous_supervisor_boot_id", out var previousElement) &&
                previousElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? previousElement.GetString()
                : null;
            var isTerminal = root.TryGetProperty("event_type", out var typeElement) &&
                typeElement.ValueKind == System.Text.Json.JsonValueKind.String &&
                string.Equals(typeElement.GetString(), "server.stopped", StringComparison.Ordinal);
            return (bootId, sequence, isTerminal, previousBootId);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null, false, null);
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
        _lease.Dispose();
    }
}

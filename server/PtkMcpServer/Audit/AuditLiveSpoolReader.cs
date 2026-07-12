namespace PtkMcpServer.Audit;

internal interface IAuditLiveSpoolRecordPosition
{
    AuditSpoolSegmentIdentity Spool { get; }

    long StartOffset { get; }

    long NextOffset { get; }

    long Sequence { get; }

    Guid EventId { get; }

    string? PreviousEventHash { get; }

    string EventHash { get; }

    ReadOnlyMemory<byte> ExactJsonlBytes { get; }
}

internal enum AuditLiveSpoolPollKind
{
    Record,
    AtCommittedTail,
    Rotated,
    WriterClosed,
}

internal sealed record AuditLiveSpoolPoll(
    AuditLiveSpoolPollKind Kind,
    AuditSpoolSegmentIdentity ObservedCurrentSegment,
    IAuditLiveSpoolRecordPosition? Record = null);

/// <summary>
/// Reads only the authoritative writer's published durable prefix. It never
/// interprets a live tail as completion. Rotation and writer closure are
/// explicit transitions for the higher-level source to reconcile through a
/// retained closed-prefix or full-chain snapshot.
/// </summary>
internal sealed class AuditLiveSpoolReader
{
    private readonly AuditJournal _journal;
    private readonly Guid _supervisorBootId;
    private readonly int _maximumRecordBytes;
    private readonly object _gate = new();
    private readonly object _generation = new();
    private AuditSpoolSegmentIdentity _currentSegment;
    private long _offset;
    private long _expectedSequence = 1;
    private string? _previousHash;
    private RecordPosition? _pending;

    internal AuditLiveSpoolReader(AuditJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var options = journal.Options;
        if (options.ProtectionMode != AuditProtectionMode.Anchored)
        {
            throw new ArgumentException(
                "A live export spool reader requires anchored audit protection.",
                nameof(journal));
        }

        _journal = journal;
        _supervisorBootId = journal.SupervisorBootId;
        _maximumRecordBytes = options.MaxRecordBytes;
        _currentSegment = AuditSpoolSegmentIdentity.Create(_supervisorBootId, 0);
    }

    internal AuditLiveSpoolPoll Poll()
    {
        lock (_gate)
        {
            if (_pending is { } pending)
            {
                return new AuditLiveSpoolPoll(
                    AuditLiveSpoolPollKind.Record,
                    _currentSegment,
                    pending);
            }

            var read = _journal.ReadCommittedSpool(
                _currentSegment,
                _offset,
                _maximumRecordBytes);
            var observed = read.CurrentSegment ?? throw new IOException(
                "The audit journal has no committed-spool source identity.");
            if (observed.SupervisorBootId != _supervisorBootId)
                throw new IOException("The live audit spool source changed supervisor identity.");

            return read.Status switch
            {
                AuditCommittedSpoolReadStatus.Data => ReadRecord(read, observed),
                AuditCommittedSpoolReadStatus.AtCommittedTail => AtTail(read, observed),
                AuditCommittedSpoolReadStatus.Rotated => Rotated(read, observed),
                AuditCommittedSpoolReadStatus.WriterClosed => WriterClosed(read, observed),
                AuditCommittedSpoolReadStatus.NotCurrent => throw new IOException(
                    "The live audit spool source no longer recognizes its exact position."),
                _ => throw new IOException("The live audit spool source returned an unknown status."),
            };
        }
    }

    /// <summary>
    /// Advances the in-memory live cursor only after its caller has durably
    /// persisted remote acknowledgment through the checkpoint capability.
    /// </summary>
    internal void AdvanceAfterDurableAcknowledgment(
        IAuditLiveSpoolRecordPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        lock (_gate)
        {
            if (position is not RecordPosition owned ||
                !ReferenceEquals(owned.Owner, this) ||
                !ReferenceEquals(owned.Generation, _generation) ||
                !ReferenceEquals(owned, _pending))
            {
                throw new ArgumentException(
                    "The live audit spool position is not the exact pending record.",
                    nameof(position));
            }

            _currentSegment = owned.Spool;
            _offset = owned.NextOffset;
            _expectedSequence = CheckedNext(owned.Sequence);
            _previousHash = owned.EventHash;
            _pending = null;
        }
    }

    private AuditLiveSpoolPoll ReadRecord(
        AuditCommittedSpoolRead read,
        AuditSpoolSegmentIdentity observed)
    {
        if (observed != _currentSegment ||
            read.Bytes.IsEmpty ||
            read.CommittedTail < checked(_offset + read.Bytes.Length))
        {
            throw new IOException("The live audit spool returned an invalid committed prefix.");
        }

        var lfIndex = read.Bytes.Span.IndexOf((byte)'\n');
        if (lfIndex < 0)
        {
            throw new IOException(
                read.Bytes.Length == _maximumRecordBytes
                    ? "A live audit record has no LF within its configured bound."
                    : "The live audit spool exposed a torn committed record.");
        }

        var length = lfIndex + 1;
        var exactLine = read.Bytes.Span[..length].ToArray();
        var parsed = AuditSpoolRecordCodec.Parse(
            exactLine.AsSpan(0, length - 1),
            _supervisorBootId);
        if (parsed.Sequence != _expectedSequence ||
            !string.Equals(
                parsed.PreviousEventHash,
                _previousHash,
                StringComparison.Ordinal))
        {
            throw new IOException("The live audit spool hash chain is discontinuous.");
        }

        var position = new RecordPosition(
            this,
            _generation,
            _currentSegment,
            _offset,
            checked(_offset + length),
            parsed,
            exactLine);
        _pending = position;
        return new AuditLiveSpoolPoll(
            AuditLiveSpoolPollKind.Record,
            observed,
            position);
    }

    private AuditLiveSpoolPoll AtTail(
        AuditCommittedSpoolRead read,
        AuditSpoolSegmentIdentity observed)
    {
        if (!read.Bytes.IsEmpty ||
            observed != _currentSegment ||
            read.CommittedTail != _offset)
        {
            throw new IOException("The live audit spool tail does not match its exact cursor.");
        }
        return new AuditLiveSpoolPoll(
            AuditLiveSpoolPollKind.AtCommittedTail,
            observed);
    }

    private AuditLiveSpoolPoll Rotated(
        AuditCommittedSpoolRead read,
        AuditSpoolSegmentIdentity observed)
    {
        if (!read.Bytes.IsEmpty || read.CommittedTail != 0 ||
            observed.Index <= _currentSegment.Index)
        {
            throw new IOException("The live audit spool returned an invalid rotation.");
        }
        return new AuditLiveSpoolPoll(AuditLiveSpoolPollKind.Rotated, observed);
    }

    private AuditLiveSpoolPoll WriterClosed(
        AuditCommittedSpoolRead read,
        AuditSpoolSegmentIdentity observed)
    {
        if (!read.Bytes.IsEmpty || read.CommittedTail != 0 ||
            observed.Index < _currentSegment.Index)
        {
            throw new IOException("The closed audit writer returned an invalid final identity.");
        }
        return new AuditLiveSpoolPoll(AuditLiveSpoolPollKind.WriterClosed, observed);
    }

    private static long CheckedNext(long sequence)
    {
        if (sequence == long.MaxValue)
            throw new IOException("The live audit spool sequence overflows.");
        return sequence + 1;
    }

    private sealed class RecordPosition(
        AuditLiveSpoolReader owner,
        object generation,
        AuditSpoolSegmentIdentity spool,
        long startOffset,
        long nextOffset,
        AuditSpoolRecord parsed,
        byte[] exactJsonlBytes) : IAuditLiveSpoolRecordPosition
    {
        internal AuditLiveSpoolReader Owner { get; } = owner;

        internal object Generation { get; } = generation;

        public AuditSpoolSegmentIdentity Spool { get; } = spool;

        public long StartOffset { get; } = startOffset;

        public long NextOffset { get; } = nextOffset;

        public long Sequence { get; } = parsed.Sequence;

        public Guid EventId { get; } = parsed.EventId;

        public string? PreviousEventHash { get; } = parsed.PreviousEventHash;

        public string EventHash { get; } = parsed.EventHash;

        public ReadOnlyMemory<byte> ExactJsonlBytes { get; } = exactJsonlBytes;
    }
}

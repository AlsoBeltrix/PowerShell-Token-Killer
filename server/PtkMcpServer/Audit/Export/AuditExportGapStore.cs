using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtkMcpServer.Audit.Export;

/// <summary>
/// Durable export ledger: audit records removed locally before delivery
/// (permanently lost custody, keyed by missing chain range so a gap is never
/// counted twice), PLUS the last delivered chain position.
///
/// The chain position lives here, not only on the cursor, because losing the
/// cursor must not erase the memory boundary detection depends on: with a
/// lost cursor, an erased old boot's undelivered tail was invisible (cr3-2
/// round 5). Total loss of this file too — deleting the audit root — is
/// undetectable by construction and is an accepted, documented limit.
///
/// This is on disk, not in memory, because a gap is evidence: process-local
/// state would erase the only trace of the loss at the next restart (cr3-2
/// verification). Bounded to <see cref="MaximumRetainedSegments"/> names; the
/// count keeps growing after the names stop being retained.
/// </summary>
internal sealed class AuditExportGapStore
{
    internal const string FileName = "export-gaps.json";
    internal const int MaximumRetainedSegments = 64;
    private const int MaximumFileBytes = 32 * 1024;

    private readonly string _path;
    private readonly string _directory;
    private readonly object _gate = new();

    internal AuditExportGapStore(string auditRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditRootDirectory);
        _directory = auditRootDirectory;
        _path = Path.Combine(auditRootDirectory, FileName);
    }

    internal AuditExportGapRecord Read()
    {
        lock (_gate)
            return ReadLocked();
    }

    /// <summary>
    /// Reads the ledger, distinguishing ABSENT from CORRUPT. Silently
    /// treating an unreadable ledger as absent disabled loss detection
    /// exactly like a fresh install (cr3-2 round 6) — and detectable
    /// corruption is not the accepted whole-root-deletion limit. A corrupt
    /// ledger is quarantined as evidence, replaced, and reported, the same
    /// contract rule 3 pattern the host identity uses.
    /// </summary>
    internal AuditExportGapRecord ReadOrQuarantine(out bool wasCorrupt)
    {
        lock (_gate)
        {
            wasCorrupt = false;
            if (!File.Exists(_path)) return AuditExportGapRecord.Empty;
            try
            {
                return ReadStrictLocked();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                wasCorrupt = true;
                QuarantineLocked(exception);
                return AuditExportGapRecord.Empty;
            }
        }
    }

    private void QuarantineLocked(Exception failure)
    {
        try
        {
            var quarantineDirectory = SecureAuditStorage.PrepareRoot(
                Path.Combine(_directory, "quarantine"));
            var target = Path.Combine(
                quarantineDirectory,
                $"{FileName}.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}");
            File.Move(_path, target);
            Console.Error.WriteLine(
                $"[ptk audit] quarantined an unreadable export ledger to '{target}' " +
                $"({failure.Message}); export loss detection restarts without its prior memory.");
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Even failing to quarantine must not stop delivery; the caller
            // still raises the unverified-boundary signal.
        }
    }

    /// <summary>Durably remembers the last delivered chain position, so a
    /// lost or corrupted cursor does not erase boot memory.</summary>
    internal void RecordChainPosition(
        string? supervisorBootId,
        long sequence,
        bool wasLifecycleTerminal)
    {
        if (string.IsNullOrWhiteSpace(supervisorBootId) || sequence <= 0) return;
        lock (_gate)
        {
            var current = ReadLocked();
            if (string.Equals(current.LastSupervisorBootId, supervisorBootId, StringComparison.Ordinal) &&
                current.LastSequence == sequence &&
                current.LastWasLifecycleTerminal == wasLifecycleTerminal)
            {
                return;
            }
            TryWriteLocked(current with
            {
                LastSupervisorBootId = supervisorBootId,
                LastSequence = sequence,
                LastWasLifecycleTerminal = wasLifecycleTerminal,
            });
        }
    }

    /// <summary>
    /// Records one lost segment. Returns the resulting durable record. A
    /// segment already recorded is not counted twice, so a repeating drain
    /// cannot inflate the number.
    /// </summary>
    internal AuditExportGapRecord Record(string gapKey, long missingRecords)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapKey);
        lock (_gate)
        {
            var current = ReadLocked();
            if (current.Segments.Contains(gapKey, StringComparer.Ordinal))
                return current;

            var keys = current.Segments
                .Append(gapKey)
                .TakeLast(MaximumRetainedSegments)
                .ToArray();
            var updated = current with
            {
                Count = current.Count + 1,
                Segments = keys,
                MissingRecords = current.MissingRecords + Math.Max(0, missingRecords),
            };
            TryWriteLocked(updated);
            return updated;
        }
    }

    private AuditExportGapRecord ReadLocked()
    {
        try
        {
            if (!File.Exists(_path)) return AuditExportGapRecord.Empty;
            return ReadStrictLocked();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return AuditExportGapRecord.Empty;
        }
    }

    /// <summary>Reads the ledger, throwing on anything unreadable so the
    /// caller can tell corruption from absence.</summary>
    private AuditExportGapRecord ReadStrictLocked()
    {
        {
            var bytes = SecureAuditStorage.ReadProtectedFile(
                _path,
                MaximumFileBytes,
                requireProtectedParent: false,
                verifyWithoutMutation: true);
            var file = JsonSerializer.Deserialize<GapFile>(bytes)
                ?? throw new InvalidDataException("The export ledger is empty.");
            if (file.Count < 0)
                throw new InvalidDataException("The export ledger count is negative.");
            return new AuditExportGapRecord(
                file.Count,
                file.Segments ?? [],
                file.MissingRecords,
                file.LastSupervisorBootId,
                file.LastSequence,
                file.LastWasLifecycleTerminal);
        }
    }

    private void TryWriteLocked(AuditExportGapRecord record)
    {
        var temporaryPath = Path.Combine(
            _directory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new GapFile
            {
                Count = record.Count,
                Segments = record.Segments.ToArray(),
                MissingRecords = record.MissingRecords,
                LastSupervisorBootId = record.LastSupervisorBootId,
                LastSequence = record.LastSequence,
                LastWasLifecycleTerminal = record.LastWasLifecycleTerminal,
            });
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Losing the durable note must not stop execution or delivery;
            // the in-memory health line still reports the gap for this
            // process.
            SecureAuditStorage.TryDelete(temporaryPath);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class GapFile
    {
        [JsonPropertyName("count")] public long Count { get; set; }
        [JsonPropertyName("segments")] public string[]? Segments { get; set; }
        [JsonPropertyName("missing_records")] public long MissingRecords { get; set; }
        [JsonPropertyName("last_boot")] public string? LastSupervisorBootId { get; set; }
        [JsonPropertyName("last_sequence")] public long LastSequence { get; set; }
        [JsonPropertyName("last_terminal")] public bool LastWasLifecycleTerminal { get; set; }
    }
}

/// <summary>Distinct gap events, their bounded keys, and the total number of
/// audit records proved missing across them.</summary>
internal sealed record AuditExportGapRecord(
    long Count,
    IReadOnlyList<string> Segments,
    long MissingRecords = 0,
    string? LastSupervisorBootId = null,
    long LastSequence = 0,
    bool LastWasLifecycleTerminal = false)
{
    internal static AuditExportGapRecord Empty { get; } = new(0, []);
}

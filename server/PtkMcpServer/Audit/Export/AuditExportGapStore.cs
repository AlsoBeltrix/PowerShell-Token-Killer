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

    /// <summary>
    /// Every read path quarantines a corrupt ledger. Quarantine used to fire
    /// only when the cursor lacked a position, so a corrupt ledger BEHIND a
    /// healthy cursor silently erased proved gaps and the next write replaced
    /// the evidence (Fable-5 review finding 2).
    /// </summary>
    internal AuditExportGapRecord Read() => ReadOrQuarantine(out _);

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
            return ReadOrQuarantineLocked(out wasCorrupt);
    }

    private AuditExportGapRecord ReadOrQuarantineLocked(out bool wasCorrupt)
    {
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
    /// <summary>A record the destination permanently refused is never
    /// delivered — lost custody, held to the same durable-evidence bar as a
    /// gap rather than a transient failure line (Fable-5 review finding 4).</summary>
    internal AuditExportGapRecord RecordRefusedRecord()
    {
        lock (_gate)
        {
            var current = ReadOrQuarantineLocked(out _);
            var updated = current with { RefusedRecords = current.RefusedRecords + 1 };
            _ = TryWriteLocked(updated);
            return updated;
        }
    }

    internal void RecordChainPosition(
        string? supervisorBootId,
        long sequence,
        bool wasLifecycleTerminal)
    {
        if (string.IsNullOrWhiteSpace(supervisorBootId) || sequence <= 0) return;
        lock (_gate)
        {
            var current = ReadOrQuarantineLocked(out _);
            var existing = current.Chains.TryGetValue(supervisorBootId!, out var chain)
                ? chain
                : null;
            if (existing is not null &&
                existing.Sequence == sequence &&
                existing.Terminal == wasLifecycleTerminal &&
                string.Equals(current.LastSupervisorBootId, supervisorBootId, StringComparison.Ordinal) &&
                current.LastSequence == sequence)
            {
                return;
            }

            // Per-boot chain memory (cr4-4): concurrent boots each carry
            // their own chain, so one last-position pair cannot remember them
            // all. The legacy single fields are still written for artifact
            // compatibility. Bounded like the cursor's boot map.
            var chains = new Dictionary<string, AuditExportChainMemory>(
                current.Chains,
                StringComparer.Ordinal)
            {
                [supervisorBootId!] =
                    new AuditExportChainMemory(sequence, wasLifecycleTerminal),
            };
            const int maximumChains = 64;
            while (chains.Count > maximumChains)
            {
                var victim = chains
                    .OrderByDescending(entry => entry.Value.Terminal)
                    .First();
                chains.Remove(victim.Key);
            }

            _ = TryWriteLocked(current with
            {
                LastSupervisorBootId = supervisorBootId,
                LastSequence = sequence,
                LastWasLifecycleTerminal = wasLifecycleTerminal,
                ChainsOrNull = chains,
            });
        }
    }

    /// <summary>
    /// Durably mirrors a lineage attestation (claimed predecessor → claiming
    /// boot). Claims are EVIDENCE and must not live only on the cursor: the
    /// cursor is loss-tolerant by contract (an unreadable cursor merely
    /// re-delivers) and its boot map is bounded, so cursor loss or eviction
    /// would erase the only witness that a vanished predecessor ever existed
    /// (cr4-4 round 3 — the same cr3-2 round-5 lesson that moved chain
    /// memory here). Bounded: resolved claims (the claimed boot's chain
    /// reached its terminal) are evicted first.
    /// </summary>
    internal bool TryRecordAttestation(string claimedBootId, string claimingBootId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBootId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimingBootId);
        lock (_gate)
        {
            var current = ReadOrQuarantineLocked(out _);
            if (current.Attestations.ContainsKey(claimedBootId)) return true;
            var attestations = new Dictionary<string, string>(
                current.Attestations,
                StringComparer.Ordinal)
            {
                [claimedBootId] = claimingBootId,
            };
            const int maximumAttestations = 64;
            while (attestations.Count > maximumAttestations)
            {
                var victim = attestations.Keys
                    .OrderByDescending(claimed =>
                        current.Chains.TryGetValue(claimed, out var chain) && chain.Terminal)
                    .First();
                attestations.Remove(victim);
            }
            return TryWriteLocked(current with { AttestationsOrNull = attestations });
        }
    }

    /// <summary>
    /// Records one lost segment. Returns the resulting durable record. A
    /// segment already recorded is not counted twice, so a repeating drain
    /// cannot inflate the number.
    /// </summary>
    internal AuditExportGapRecord Record(
        string gapKey,
        long missingRecords,
        out bool persisted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapKey);
        persisted = true;
        lock (_gate)
        {
            var current = ReadOrQuarantineLocked(out _);
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
            persisted = TryWriteLocked(updated);
            return updated;
        }
    }

    /// <summary>
    /// Folds gap counters an earlier process could not persist (parked on the
    /// cursor) into the ledger once it is writable again. Without this the
    /// parked counters never migrated, so a later cursor loss silently
    /// restored a healthy status (cr3-2 round 10).
    /// </summary>
    internal bool TryAbsorbUnrecorded(long gaps, long missingRecords)
    {
        if (gaps <= 0 && missingRecords <= 0) return true;
        lock (_gate)
        {
            var current = ReadOrQuarantineLocked(out _);

            return TryWriteLocked(current with
            {
                Count = current.Count + gaps,
                MissingRecords = current.MissingRecords + Math.Max(0, missingRecords),
            });
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
            // A structurally valid but schema-less object ({} or a truncated
            // write) deserializes to all-defaults and used to pass as a
            // legitimately empty ledger, silently discarding boot memory
            // (cr3-2 round 7). Only a file carrying our own version marker
            // is our ledger; anything else is corruption.
            if (file.Version != CurrentVersion)
            {
                throw new InvalidDataException(
                    "The export ledger does not carry its schema version.");
            }
            if (file.Count < 0)
                throw new InvalidDataException("The export ledger count is negative.");
            var chains = new Dictionary<string, AuditExportChainMemory>(StringComparer.Ordinal);
            if (file.Chains is not null)
            {
                foreach (var (boot, chain) in file.Chains)
                {
                    if (chain is null || chain.Sequence <= 0) continue;
                    chains[boot] = new AuditExportChainMemory(chain.Sequence, chain.Terminal);
                }
            }
            // A version-1 ledger's single position seeds the map.
            if (file.LastSupervisorBootId is not null &&
                file.LastSequence > 0 &&
                !chains.ContainsKey(file.LastSupervisorBootId))
            {
                chains[file.LastSupervisorBootId] = new AuditExportChainMemory(
                    file.LastSequence,
                    file.LastWasLifecycleTerminal);
            }
            var attestations = new Dictionary<string, string>(StringComparer.Ordinal);
            if (file.Attestations is not null)
            {
                foreach (var (claimed, claiming) in file.Attestations)
                {
                    if (string.IsNullOrWhiteSpace(claiming)) continue;
                    attestations[claimed] = claiming;
                }
            }
            return new AuditExportGapRecord(
                file.Count,
                file.Segments ?? [],
                file.MissingRecords,
                file.LastSupervisorBootId,
                file.LastSequence,
                file.LastWasLifecycleTerminal,
                file.RefusedRecords,
                chains,
                attestations);
        }
    }

    private bool TryWriteLocked(AuditExportGapRecord record)
    {
        var temporaryPath = Path.Combine(
            _directory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new GapFile
            {
                Version = CurrentVersion,
                Count = record.Count,
                Segments = record.Segments.ToArray(),
                MissingRecords = record.MissingRecords,
                LastSupervisorBootId = record.LastSupervisorBootId,
                LastSequence = record.LastSequence,
                LastWasLifecycleTerminal = record.LastWasLifecycleTerminal,
                RefusedRecords = record.RefusedRecords,
                Chains = record.Chains.ToDictionary(
                    entry => entry.Key,
                    entry => (ChainFile?)new ChainFile
                    {
                        Sequence = entry.Value.Sequence,
                        Terminal = entry.Value.Terminal,
                    },
                    StringComparer.Ordinal),
                Attestations = record.Attestations.ToDictionary(
                    entry => entry.Key,
                    entry => (string?)entry.Value,
                    StringComparer.Ordinal),
            });
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Losing the durable note must not stop execution or delivery,
            // but it must not silently lose the EVIDENCE either: the caller
            // parks the unrecorded gap on the cursor so a restart still
            // reports it (cr3-2 round 9).
            SecureAuditStorage.TryDelete(temporaryPath);
            return false;
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    internal const int CurrentVersion = 1;

    private sealed class GapFile
    {
        [JsonPropertyName("version")] public int? Version { get; set; }
        [JsonPropertyName("count")] public long Count { get; set; }
        [JsonPropertyName("segments")] public string[]? Segments { get; set; }
        [JsonPropertyName("missing_records")] public long MissingRecords { get; set; }
        [JsonPropertyName("last_boot")] public string? LastSupervisorBootId { get; set; }
        [JsonPropertyName("last_sequence")] public long LastSequence { get; set; }
        [JsonPropertyName("last_terminal")] public bool LastWasLifecycleTerminal { get; set; }
        [JsonPropertyName("refused_records")] public long RefusedRecords { get; set; }
        [JsonPropertyName("chains")] public Dictionary<string, ChainFile?>? Chains { get; set; }
        [JsonPropertyName("attested")] public Dictionary<string, string?>? Attestations { get; set; }
    }

    private sealed class ChainFile
    {
        [JsonPropertyName("sequence")] public long Sequence { get; set; }
        [JsonPropertyName("terminal")] public bool Terminal { get; set; }
    }
}

/// <summary>One boot's last delivered chain position, mirrored durably.</summary>
internal sealed record AuditExportChainMemory(long Sequence, bool Terminal);

/// <summary>Distinct gap events, their bounded keys, and the total number of
/// audit records proved missing across them.</summary>
internal sealed record AuditExportGapRecord(
    long Count,
    IReadOnlyList<string> Segments,
    long MissingRecords = 0,
    string? LastSupervisorBootId = null,
    long LastSequence = 0,
    bool LastWasLifecycleTerminal = false,
    long RefusedRecords = 0,
    IReadOnlyDictionary<string, AuditExportChainMemory>? ChainsOrNull = null,
    IReadOnlyDictionary<string, string>? AttestationsOrNull = null)
{
    internal static AuditExportGapRecord Empty { get; } = new(0, []);

    internal IReadOnlyDictionary<string, AuditExportChainMemory> Chains =>
        ChainsOrNull ?? EmptyChains;

    /// <summary>Durable lineage attestations: claimed predecessor → the boot
    /// that named it (cr4-4 round 3).</summary>
    internal IReadOnlyDictionary<string, string> Attestations =>
        AttestationsOrNull ?? EmptyAttestations;

    private static readonly IReadOnlyDictionary<string, AuditExportChainMemory> EmptyChains =
        new Dictionary<string, AuditExportChainMemory>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> EmptyAttestations =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

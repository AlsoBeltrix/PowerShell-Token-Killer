using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtkMcpServer.Audit;

/// <summary>
/// Durable boot lineage: the id of the last supervisor boot that journaled at
/// least one record under this audit root. Every audit record carries this as
/// <c>producer.previous_supervisor_boot_id</c>, so a boot whose records were
/// all destroyed before delivery is still attested by its successor's records
/// — without lineage, boot ids are random UUIDv4 and a wholly vanished boot
/// was structurally invisible to the exporter's chain walk (cr3-2 / Fable-5
/// review finding 1, R3d).
///
/// Published only after a boot's FIRST record is durably appended, never at
/// journal open: a lineage entry therefore always names a boot that journaled
/// something, so a process that opened a journal and crashed before writing
/// anything neither appears in lineage nor raises a false signal.
/// </summary>
internal static class AuditBootLineage
{
    internal const string FileName = "boot-lineage.json";
    internal const string QuarantineDetailCode = "quarantine.boot_lineage";
    private const int MaximumFileBytes = 4 * 1024;
    private const int CurrentVersion = 1;

    /// <summary>
    /// The previous boot id, or null when there is none (first boot, or the
    /// lineage artifact is unusable). A corrupt artifact is quarantined as
    /// evidence per contract rule 3 and reported through
    /// <paramref name="quarantineDetail"/> so the caller can journal the fact
    /// — a quarantine must never remain stderr-only (cr2-4).
    /// </summary>
    internal static Guid? ReadPrevious(
        string auditRootDirectory,
        out string? quarantineDetail)
    {
        quarantineDetail = null;
        var path = Path.Combine(auditRootDirectory, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = SecureAuditStorage.ReadProtectedFile(
                path,
                MaximumFileBytes,
                requireProtectedParent: false,
                verifyWithoutMutation: true);
            var file = JsonSerializer.Deserialize<LineageFile>(bytes)
                ?? throw new InvalidDataException("The boot lineage artifact is empty.");
            // A schema-less object ({} or a truncated write) deserializes to
            // all-defaults; only a file carrying our own version marker is
            // our artifact (the export ledger learned this in cr3-2 round 7).
            if (file.Version != CurrentVersion)
                throw new InvalidDataException("The boot lineage artifact does not carry its schema version.");
            if (!Guid.TryParseExact(file.LastBoot, "D", out var lastBoot) ||
                lastBoot.ToString("D") != file.LastBoot)
            {
                throw new InvalidDataException("The boot lineage artifact does not name a canonical boot id.");
            }
            return lastBoot;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            quarantineDetail = QuarantineDetailCode;
            Quarantine(auditRootDirectory, path, exception);
            return null;
        }
    }

    /// <summary>
    /// Durably names <paramref name="supervisorBootId"/> as the latest boot
    /// with journaled records. Failure is reported, never thrown: lineage is
    /// attestation for the NEXT boot, and losing it degrades detection of a
    /// vanished boot to the pre-lineage behaviour — it must not fail the
    /// append whose success it records.
    /// </summary>
    internal static bool TryPublish(string auditRootDirectory, Guid supervisorBootId)
    {
        var temporaryPath = Path.Combine(
            auditRootDirectory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new LineageFile
            {
                Version = CurrentVersion,
                LastBoot = supervisorBootId.ToString("D"),
            });
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, Path.Combine(auditRootDirectory, FileName), overwrite: true);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            SecureAuditStorage.TryDelete(temporaryPath);
            return false;
        }
    }

    private static void Quarantine(string root, string path, Exception failure)
    {
        try
        {
            var quarantineDirectory = SecureAuditStorage.PrepareRoot(
                Path.Combine(root, "quarantine"));
            var target = Path.Combine(
                quarantineDirectory,
                $"{FileName}.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}");
            File.Move(path, target);
            Console.Error.WriteLine(
                $"[ptk audit] quarantined an unreadable boot lineage artifact to '{target}' " +
                $"({failure.Message}); this boot's records will not attest a predecessor.");
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Failing to quarantine an advisory artifact must not block
            // startup; the parked detail still reaches the journal.
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class LineageFile
    {
        [JsonPropertyName("version")] public int? Version { get; set; }
        [JsonPropertyName("last_boot")] public string? LastBoot { get; set; }
    }
}

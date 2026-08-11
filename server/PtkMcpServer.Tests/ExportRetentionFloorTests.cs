using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

/// <summary>
/// audit-restoration R3d: journal retention is acknowledgment-aware, so
/// ordinary age-based cleanup cannot destroy records the exporter has not
/// delivered. Ten review rounds established that detecting such loss
/// afterwards is intrinsically leaky; this stops creating it.
/// </summary>
public sealed class ExportRetentionFloorTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    [Fact]
    public void The_floor_protects_the_cursor_segment_and_everything_after_it()
    {
        var boot = Guid.Parse("d99ba8e8-25c5-4bfb-9c39-364407e4d96d");
        var floor = AuditSpoolSegmentIdentity.Create(boot, 4).FileName;

        Assert.True(ExportRetentionFloor.IsRequired(floor, floor));
        Assert.True(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(boot, 5).FileName,
            floor));
        // Already delivered.
        Assert.False(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(boot, 3).FileName,
            floor));
        // Without a supplied group ordering, another supervisor boot has no
        // ordering against this floor and is not protected (the pre-cr4-4
        // single-supervisor behaviour).
        Assert.False(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(Guid.NewGuid(), 9).FileName,
            floor));
        // No cursor at all means no extra retention.
        Assert.False(ExportRetentionFloor.IsRequired(floor, null));
    }

    [Fact]
    public void With_group_ordering_other_boots_at_or_after_the_cursor_group_are_protected()
    {
        // cr4-4: delivery traverses boot groups in earliest-creation order,
        // so a DIFFERENT boot is deletable only when its whole group is
        // strictly before the cursor's group — "different boot" no longer
        // means "already delivered or already lost" on a shared root.
        var floorBoot = Guid.Parse("d99ba8e8-25c5-4bfb-9c39-364407e4d96d");
        var earlierBoot = Guid.Parse("5b0e5efc-2f63-46f5-93a3-2f4ea18d6a01");
        var laterBoot = Guid.Parse("2a6465d4-6652-4ff7-8630-2ab0c5f6d04c");
        var unknownBoot = Guid.Parse("7c1f2f66-9f6e-4a3b-8b21-6a2f9d1c0e55");
        var floor = AuditSpoolSegmentIdentity.Create(floorBoot, 2).FileName;
        var baseTime = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);
        DateTime? Earliest(Guid boot) =>
            boot == floorBoot ? baseTime :
            boot == earlierBoot ? baseTime.AddMinutes(-5) :
            boot == laterBoot ? baseTime.AddMinutes(5) :
            null;

        Assert.False(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(earlierBoot, 9).FileName, floor, Earliest));
        Assert.True(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(laterBoot, 0).FileName, floor, Earliest));
        // Unknown ordering is conservative: keep the bytes.
        Assert.True(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(unknownBoot, 0).FileName, floor, Earliest));
        // Same-boot behaviour is unchanged by the ordering argument.
        Assert.True(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(floorBoot, 3).FileName, floor, Earliest));
        Assert.False(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(floorBoot, 1).FileName, floor, Earliest));
    }

    [Fact]
    public void An_absent_or_unreadable_cursor_yields_no_floor()
    {
        var root = NewRoot("floor-missing");
        Assert.Null(ExportRetentionFloor.ReadOldestRequiredSegment(root));

        // The journal must never fail or change behaviour because the
        // exporter's bookkeeping is unusable.
        File.WriteAllText(Path.Combine(root, "export-cursor.json"), "{ not json");
        Assert.Null(ExportRetentionFloor.ReadOldestRequiredSegment(root));

        File.WriteAllText(
            Path.Combine(root, "export-cursor.json"),
            JsonSerializer.Serialize(new { segment = "not-a-segment-name", offset = 0 }));
        Assert.Null(ExportRetentionFloor.ReadOldestRequiredSegment(root));
    }

    [Fact]
    public void A_valid_cursor_reports_its_segment_as_the_floor()
    {
        var root = NewRoot("floor-present");
        var name = AuditSpoolSegmentIdentity
            .Create(Guid.Parse("d99ba8e8-25c5-4bfb-9c39-364407e4d96d"), 7)
            .FileName;
        var path = Path.Combine(root, "export-cursor.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { segment = name, offset = 128 }));
        // The floor read enforces the same owner-only protection as every
        // other audit artifact: a world-readable cursor is not trusted.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, SecureAuditStorage.OwnerFileMode);

        Assert.Equal(name, ExportRetentionFloor.ReadOldestRequiredSegment(root));
    }

    [Fact]
    public void Age_retention_deletes_delivered_segments_and_keeps_undelivered_ones()
    {
        // The behavioural half, through the real journal sink: with a cursor
        // standing at segment 1, ageing out the spool must remove segment 0
        // (delivered) and keep segment 1 (not yet delivered) even though both
        // are equally old.
        var root = NewRoot("floor-sweep");
        var options = AuditOptions.Create(
            root,
            AuditProtectionMode.LocalOnly,
            exportConfigurationIdentity: null,
            maxRecordBytes: 4096,
            segmentBytes: 16_384,
            aggregateBytes: 1024 * 1024,
            emergencyReserveBytes: 8192,
            retentionAge: TimeSpan.FromMinutes(1),
            maxEvidenceBytes: 4096,
            evidenceAggregateBytes: 4096,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var health = new AuditHealth(options);

        // Two closed segments plus a live one, written by the real sink.
        using var journal = AuditJournalFactory.Open(options, health, "test-version");
        var boot = ReadSpoolBootId(options.SpoolDirectory);
        var closedZero = AuditSpoolSegmentIdentity.Create(boot, 0).FileName;
        var closedOne = AuditSpoolSegmentIdentity.Create(boot, 1).FileName;
        WriteUntilSegmentExists(journal, options, closedOne);
        WriteUntilSegmentExists(journal, options, AuditSpoolSegmentIdentity.Create(boot, 2).FileName);

        // Age the closed segments past the retention bound so the sweep
        // considers them; both are equally old, so only the floor decides.
        foreach (var file in Directory.GetFiles(options.SpoolDirectory, "*.jsonl"))
        {
            if (Path.GetFileName(file) is var name &&
                (name == closedZero || name == closedOne))
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
            }
        }

        // Delivery stands at segment 1: segment 0 is delivered, 1 is not.
        WriteCursor(root, closedOne);

        // A further append triggers the retention sweep.
        WriteUntilSegmentExists(journal, options, AuditSpoolSegmentIdentity.Create(boot, 3).FileName);

        var present = Directory.GetFiles(options.SpoolDirectory, "*.jsonl")
            .Select(Path.GetFileName)
            .ToArray();
        Assert.DoesNotContain(closedZero, present);
        Assert.Contains(closedOne, present);
    }

    private static Guid ReadSpoolBootId(string spoolDirectory)
    {
        var name = Path.GetFileName(
            Directory.GetFiles(spoolDirectory, "*.jsonl").Single());
        Assert.True(AuditSpoolSegmentIdentity.TryParse(name, out var identity));
        return identity.SupervisorBootId;
    }

    private static void WriteUntilSegmentExists(
        AuditJournal journal,
        AuditOptions options,
        string segmentFileName)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (File.Exists(Path.Combine(options.SpoolDirectory, segmentFileName))) return;
            Assert.True(journal.TryReserve(1, out var reservation, out _));
            journal.Append(reservation!, TestEvent());
            reservation!.Release();
        }
        Assert.Fail($"segment {segmentFileName} never appeared");
    }

    private static AuditEventInput TestEvent() => new()
    {
        EventType = "call.completed",
        Session = new AuditSession { Name = "default", Generation = 0, BindingKind = "default" },
        Actor = new AuditActor { AttributionStrength = "unknown" },
        Correlation = new AuditCorrelation(),
        Request = new AuditRequest(),
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome { State = "completed", TerminationCertainty = "not_applicable" },
        Coverage = new AuditCoverage
        {
            PtkRequest = true,
            RootProcessObserved = "not_applicable",
            DescendantsObserved = "not_applicable",
            RemoteEffectObserved = "not_applicable",
        },
        Audit = new AuditEventHealth { ProtectionMode = "local-only", HealthState = "healthy" },
    };

    private static void WriteCursor(string root, string segmentFileName)
    {
        var path = Path.Combine(root, "export-cursor.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new { segment = segmentFileName, offset = 0 }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, SecureAuditStorage.OwnerFileMode);
    }

    private string NewRoot(string label)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            $"test-{label}-{Guid.NewGuid():N}");
        _roots.Add(root);
        return SecureAuditStorage.PrepareRoot(root);
    }
}

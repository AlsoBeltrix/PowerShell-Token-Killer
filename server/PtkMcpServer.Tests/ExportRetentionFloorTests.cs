using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

/// <summary>
/// audit-restoration R3d: journal retention is acknowledgment-aware, so
/// ordinary age-based cleanup cannot destroy records the exporter has not
/// delivered. The cr4-4 reopen retired the single-segment floor for PER-BOOT
/// floors: any cross-boot ordering keyed on the remaining files mutates when
/// delivered segments are deleted, so no such ordering may decide retention.
/// </summary>
public sealed class ExportRetentionFloorTests : IDisposable
{
    private static readonly Guid BootA = Guid.Parse("d99ba8e8-25c5-4bfb-9c39-364407e4d96d");
    private static readonly Guid BootB = Guid.Parse("5b0e5efc-2f63-46f5-93a3-2f4ea18d6a01");

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
    public void Per_boot_floors_protect_positions_undelivered_boots_and_release_finished_ones()
    {
        var floors = new Dictionary<Guid, ExportRetentionFloor.BootFloor>
        {
            [BootA] = new(SegmentIndex: 4, Terminal: false),
            [BootB] = new(SegmentIndex: 2, Terminal: true),
        };

        // The position segment and everything after it stay; earlier
        // segments of the same boot are delivered and deletable.
        Assert.True(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(BootA, 4).FileName, floors));
        Assert.True(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(BootA, 5).FileName, floors));
        Assert.False(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(BootA, 3).FileName, floors));

        // A boot whose lifecycle terminal was delivered needs nothing:
        // nothing appends after server.stopped.
        Assert.False(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(BootB, 9).FileName, floors));

        // A boot the exporter has never recorded is wholly undelivered:
        // everything is retained.
        Assert.True(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(Guid.NewGuid(), 0).FileName, floors));

        // No floors at all (export never ran) retains nothing extra.
        Assert.False(ExportRetentionFloor.IsRequired(
            AuditSpoolSegmentIdentity.Create(BootA, 4).FileName, null));
    }

    [Fact]
    public void An_absent_or_unreadable_cursor_yields_no_floors()
    {
        var root = NewRoot("floor-missing");
        Assert.Null(ExportRetentionFloor.ReadFloors(root));

        // The journal must never fail or change behaviour because the
        // exporter's bookkeeping is unusable.
        File.WriteAllText(Path.Combine(root, "export-cursor.json"), "{ not json");
        Assert.Null(ExportRetentionFloor.ReadFloors(root));
    }

    [Fact]
    public void A_version2_cursor_reports_per_boot_floors()
    {
        var root = NewRoot("floor-v2");
        var segmentA = AuditSpoolSegmentIdentity.Create(BootA, 7).FileName;
        WriteProtected(root, "export-cursor.json", JsonSerializer.Serialize(new
        {
            version = 2,
            boots = new Dictionary<string, object>
            {
                [BootA.ToString("D")] = new { segment = segmentA, offset = 128, terminal = false },
                [BootB.ToString("D")] = new { segment = (string?)null, offset = 0, terminal = true },
            },
        }));

        var floors = ExportRetentionFloor.ReadFloors(root);
        Assert.NotNull(floors);
        Assert.Equal(7, floors![BootA].SegmentIndex);
        Assert.False(floors[BootA].Terminal);
        Assert.True(floors[BootB].Terminal);
    }

    [Fact]
    public void A_version1_cursor_migrates_to_its_boots_floor()
    {
        var root = NewRoot("floor-v1");
        var name = AuditSpoolSegmentIdentity.Create(BootA, 7).FileName;
        WriteProtected(root, "export-cursor.json",
            JsonSerializer.Serialize(new { segment = name, offset = 128 }));

        var floors = ExportRetentionFloor.ReadFloors(root);
        Assert.NotNull(floors);
        var floor = Assert.Single(floors!);
        Assert.Equal(BootA, floor.Key);
        Assert.Equal(7, floor.Value.SegmentIndex);
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
        WriteProtected(root, "export-cursor.json", JsonSerializer.Serialize(new
        {
            version = 2,
            boots = new Dictionary<string, object>
            {
                [boot.ToString("D")] = new { segment = closedOne, offset = 0, terminal = false },
            },
        }));

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

    private static void WriteProtected(string root, string name, string json)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, json);
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

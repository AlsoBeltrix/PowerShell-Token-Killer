using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

/// <summary>
/// audit-restoration R3d, boot lineage: every audit record attests the last
/// predecessor boot that journaled at least one record, so a boot whose spool
/// segments are all destroyed before delivery is still named by its
/// successor's records. This closed the one finding the cr3-2 loop left open
/// (a wholly vanished supervisor boot was structurally invisible).
/// </summary>
public sealed class AuditBootLineageTests : IDisposable
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
    public void The_first_boot_attests_no_predecessor_and_says_so_in_the_record()
    {
        var root = NewRoot("lineage-first");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);

        using (var journal = AuditJournalFactory.Open(options, health, "test-version"))
        {
            Assert.Null(journal.PreviousSupervisorBootId);
            AppendOne(journal);
        }

        // Read after dispose: the live segment is held FileShare.None.
        var record = ReadSingleRecord(options);
        Assert.Contains("\"previous_supervisor_boot_id\":null", record, StringComparison.Ordinal);
    }

    [Fact]
    public void A_boot_that_journaled_is_attested_by_its_successor_records()
    {
        var root = NewRoot("lineage-successor");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);

        Guid firstBoot;
        using (var first = AuditJournalFactory.Open(options, health, "test-version"))
        {
            firstBoot = first.SupervisorBootId;
            AppendOne(first);
        }

        Guid secondBoot;
        using (var second = AuditJournalFactory.Open(options, new AuditHealth(options), "test-version"))
        {
            Assert.Equal(firstBoot, second.PreviousSupervisorBootId);
            secondBoot = second.SupervisorBootId;
            AppendOne(second);
        }

        var secondSegment = AuditSpoolSegmentIdentity
            .Create(secondBoot, 0)
            .FileName;
        var record = File.ReadAllText(Path.Combine(options.SpoolDirectory, secondSegment));
        Assert.Contains(
            $"\"previous_supervisor_boot_id\":\"{firstBoot:D}\"",
            record,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_boot_that_never_appended_is_invisible_to_lineage()
    {
        // Lineage is published on the first durable append, not at journal
        // open: a lineage entry always names a boot that journaled something,
        // so a process that opened and crashed before writing anything lost
        // no records and raises no signal.
        var root = NewRoot("lineage-silent-boot");
        var options = AuditOptions.Create(root);

        Guid firstBoot;
        using (var first = AuditJournalFactory.Open(options, new AuditHealth(options), "test-version"))
        {
            firstBoot = first.SupervisorBootId;
            AppendOne(first);
        }

        // Opens, journals nothing, goes away.
        using (var silent = AuditJournalFactory.Open(options, new AuditHealth(options), "test-version"))
        {
            Assert.Equal(firstBoot, silent.PreviousSupervisorBootId);
        }

        using var third = AuditJournalFactory.Open(options, new AuditHealth(options), "test-version");
        Assert.Equal(firstBoot, third.PreviousSupervisorBootId);
    }

    [Fact]
    public void A_corrupt_lineage_artifact_is_quarantined_and_reaches_the_journal()
    {
        var root = NewRoot("lineage-corrupt");
        var options = AuditOptions.Create(root);
        var lineagePath = Path.Combine(root, AuditBootLineage.FileName);
        File.WriteAllText(lineagePath, "{ this is not our artifact");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(lineagePath, SecureAuditStorage.OwnerFileMode);

        using var journal = AuditJournalFactory.Open(options, new AuditHealth(options), "test-version");

        // Contract rule 3: preserved as evidence, fresh state minted, service
        // continues — and the fact is parked for the journal, never
        // stderr-only (cr2-4).
        Assert.Null(journal.PreviousSupervisorBootId);
        Assert.False(File.Exists(lineagePath));
        Assert.NotEmpty(Directory.GetFiles(
            Path.Combine(root, AuditJournalFactory.QuarantineDirectoryName),
            AuditBootLineage.FileName + ".*"));
        var parked = new List<string?>();
        while (journal.TryTakePendingStartupQuarantine(out var detail))
            parked.Add(detail);
        Assert.Contains(AuditBootLineage.QuarantineDetailCode, parked);
    }

    [Fact]
    public void A_canonical_but_non_v4_lineage_id_is_quarantined_not_served()
    {
        // cr4-1: the record serializer requires a UUIDv4 predecessor. A
        // canonical UUIDv1 that passed the lineage read would fail EVERY
        // subsequent append's schema validation — one corrupt advisory
        // artifact refusing all execution, the opposite of rule 3.
        var root = NewRoot("lineage-non-v4");
        var options = AuditOptions.Create(root);
        var lineagePath = Path.Combine(root, AuditBootLineage.FileName);
        File.WriteAllText(
            lineagePath,
            "{\"version\":1,\"last_boot\":\"a6e0e5f0-1dd2-11b2-8080-808080808080\"}");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(lineagePath, SecureAuditStorage.OwnerFileMode);

        using var journal = AuditJournalFactory.Open(options, new AuditHealth(options), "test-version");

        Assert.Null(journal.PreviousSupervisorBootId);
        Assert.False(File.Exists(lineagePath));
        Assert.NotEmpty(Directory.GetFiles(
            Path.Combine(root, AuditJournalFactory.QuarantineDirectoryName),
            AuditBootLineage.FileName + ".*"));
        var parked = new List<string?>();
        while (journal.TryTakePendingStartupQuarantine(out var detail))
            parked.Add(detail);
        Assert.Contains(AuditBootLineage.QuarantineDetailCode, parked);
        // The append that would have failed schema validation succeeds.
        AppendOne(journal);
    }

    [Fact]
    public void Distinct_startup_quarantine_facts_are_all_retained()
    {
        // The pending-quarantine channel was a single slot; with two artifact
        // classes able to quarantine on one startup (host identity and boot
        // lineage), the second parked fact silently replaced the first.
        var root = NewRoot("lineage-two-quarantines");
        var options = AuditOptions.Create(root);
        File.WriteAllText(Path.Combine(root, "host.id"), "not a host identity");
        var lineagePath = Path.Combine(root, AuditBootLineage.FileName);
        File.WriteAllText(lineagePath, "{ not our artifact");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(lineagePath, SecureAuditStorage.OwnerFileMode);

        using var journal = AuditJournalFactory.Open(options, new AuditHealth(options), "test-version");

        var parked = new List<string?>();
        while (journal.TryTakePendingStartupQuarantine(out var detail))
            parked.Add(detail);
        Assert.Contains(AuditJournalFactory.HostIdentityQuarantineDetailCode, parked);
        Assert.Contains(AuditBootLineage.QuarantineDetailCode, parked);
    }

    private static void AppendOne(AuditJournal journal)
    {
        Assert.True(journal.TryReserve(1, out var reservation, out _));
        journal.Append(reservation!, TestEvent());
        reservation!.Release();
    }

    private static string ReadSingleRecord(AuditOptions options)
    {
        var file = Directory.GetFiles(options.SpoolDirectory, "*.jsonl").Single();
        return File.ReadAllText(file);
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

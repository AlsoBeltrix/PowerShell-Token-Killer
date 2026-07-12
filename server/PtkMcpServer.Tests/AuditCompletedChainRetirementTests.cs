using System.Runtime.InteropServices;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditCompletedChainRetirementTests : IDisposable
{
    private static readonly Guid HostId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid BootId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    private const string ConfigurationIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly List<string> _roots = [];

    [Fact]
    public void Aged_chain_complete_retirement_removes_segments_and_per_boot_controls()
    {
        var options = Options(NewRoot());
        CreateCompletedChain(options, aged: true);

        Assert.True(AuditCompletedChainRetirement.TryRetire(
            options,
            BootId,
            DateTimeOffset.UtcNow,
            requiredHeadroomBytes: 0));

        AssertRetired(options);
    }

    [Fact]
    public void Young_chain_waits_for_age_until_aggregate_headroom_requires_retirement()
    {
        var options = Options(NewRoot());
        CreateCompletedChain(options, aged: false);

        Assert.False(AuditCompletedChainRetirement.TryRetire(
            options,
            BootId,
            DateTimeOffset.UtcNow,
            requiredHeadroomBytes: 0));
        Assert.NotEmpty(Segments(options));

        Assert.True(AuditCompletedChainRetirement.TryRetire(
            options,
            BootId,
            DateTimeOffset.UtcNow,
            requiredHeadroomBytes: options.AggregateBytes));
        AssertRetired(options);
    }

    [Fact]
    public void Retirement_exactly_cleans_completed_disposition_controls()
    {
        var options = Options(NewRoot());
        CreateCompletedChain(options, aged: true);
        _ = CreateCompletedDispositionControls(options);
        Assert.Single(IntentPaths(options));
        Assert.Single(OutcomePaths(options));

        Assert.True(AuditCompletedChainRetirement.TryRetire(
            options,
            BootId,
            DateTimeOffset.UtcNow,
            requiredHeadroomBytes: 0));

        AssertRetired(options);
    }

    [Theory]
    [InlineData((int)AuditCompletedChainRetirementFaultPoint.DispositionIntentDeleted)]
    [InlineData((int)AuditCompletedChainRetirementFaultPoint.DispositionOutcomeDeleted)]
    public void Disposition_cleanup_crash_boundaries_recover_under_retirement_authority(
        int injectedPointValue)
    {
        var injectedPoint = (AuditCompletedChainRetirementFaultPoint)injectedPointValue;
        var options = Options(NewRoot());
        CreateCompletedChain(options, aged: true);
        _ = CreateCompletedDispositionControls(options);

        Assert.Throws<IOException>(() => AuditCompletedChainRetirement.TryRetire(
            options,
            BootId,
            DateTimeOffset.UtcNow,
            requiredHeadroomBytes: 0,
            (point, ordinal) =>
            {
                if (point == injectedPoint && ordinal == 1)
                    throw new IOException("injected disposition cleanup crash");
            }));
        Assert.Single(Directory.GetFiles(
            options.RootDirectory,
            "audit.retirement-*.json"));
        Assert.Empty(IntentPaths(options));
        if (injectedPoint == AuditCompletedChainRetirementFaultPoint.DispositionIntentDeleted)
            Assert.Single(OutcomePaths(options));
        else
            Assert.Empty(OutcomePaths(options));

        using (var quota = AuditSpoolQuotaLease.AcquireExisting(options.SpoolDirectory))
            AuditAnchoredWriterStartupPreflight.RunUnderQuota(options, quota);

        AssertRetired(options);
    }

    [Fact]
    public void Retirement_recovers_a_durable_unpublished_completion_receipt()
    {
        var options = Options(NewRoot());
        CreateCompletedChain(options, aged: true);
        var disposition = CreateCompletedDispositionControls(options);
        var published = Assert.Single(OutcomePaths(options));
        var temporary = Path.Combine(
            options.RootDirectory,
            AuditOperatorDispositionOutcome.TemporaryFileName(
                BootId,
                disposition.EventId));
        File.Move(published, temporary);

        Assert.True(AuditCompletedChainRetirement.TryRetire(
            options,
            BootId,
            DateTimeOffset.UtcNow,
            requiredHeadroomBytes: 0));

        AssertRetired(options);
        Assert.False(File.Exists(temporary));
    }

    [Theory]
    [InlineData((int)AuditCompletedChainRetirementFaultPoint.IntentPublished, 0)]
    [InlineData((int)AuditCompletedChainRetirementFaultPoint.SegmentDeleted, 1)]
    [InlineData((int)AuditCompletedChainRetirementFaultPoint.CheckpointDeleted, 0)]
    [InlineData((int)AuditCompletedChainRetirementFaultPoint.LockDeleted, 0)]
    public void Every_durable_crash_boundary_recovers_before_a_fresh_writer_preflight(
        int injectedPointValue,
        int injectedOrdinal)
    {
        var injectedPoint = (AuditCompletedChainRetirementFaultPoint)injectedPointValue;
        var options = Options(NewRoot());
        CreateCompletedChain(options, aged: true);

        Assert.Throws<IOException>(() =>
            AuditCompletedChainRetirement.TryRetire(
                options,
                BootId,
                DateTimeOffset.UtcNow,
                requiredHeadroomBytes: 0,
                (point, ordinal) =>
                {
                    if (point == injectedPoint && ordinal == injectedOrdinal)
                        throw new IOException("injected retirement crash");
                }));
        Assert.Single(Directory.GetFiles(
            options.RootDirectory,
            "audit.retirement-*.json"));

        using (var quota = AuditSpoolQuotaLease.AcquireExisting(options.SpoolDirectory))
            AuditAnchoredWriterStartupPreflight.RunUnderQuota(options, quota);

        AssertRetired(options);
    }

    [Fact]
    public void Live_or_unacknowledged_checkpoint_ownership_never_grants_retirement()
    {
        var liveOptions = Options(NewRoot());
        using (var liveStore = CreateCompletedChain(liveOptions, aged: true, keepStore: true))
        {
            Assert.False(AuditCompletedChainRetirement.TryRetire(
                liveOptions,
                BootId,
                DateTimeOffset.UtcNow,
                requiredHeadroomBytes: liveOptions.AggregateBytes));
            Assert.NotEmpty(Segments(liveOptions));
        }

        var openOptions = Options(NewRoot());
        CreateOpenChain(openOptions);
        Assert.Throws<IOException>(() =>
            AuditCompletedChainRetirement.TryRetire(
                openOptions,
                BootId,
                DateTimeOffset.UtcNow,
                requiredHeadroomBytes: openOptions.AggregateBytes));
        Assert.NotEmpty(Segments(openOptions));
        Assert.Empty(Directory.GetFiles(
            openOptions.RootDirectory,
            "audit.retirement-*.json"));
    }

    [Fact]
    public void Published_intent_never_authorizes_a_segment_outside_its_frozen_range()
    {
        var options = Options(NewRoot());
        CreateCompletedChain(options, aged: true);
        Assert.Throws<IOException>(() =>
            AuditCompletedChainRetirement.TryRetire(
                options,
                BootId,
                DateTimeOffset.UtcNow,
                requiredHeadroomBytes: 0,
                (point, _) =>
                {
                    if (point == AuditCompletedChainRetirementFaultPoint.IntentPublished)
                        throw new IOException("stop after intent");
                }));
        var outside = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(BootId, 2).FileName);
        using (var stream = SecureAuditStorage.CreateExclusiveFile(outside))
            stream.Flush(flushToDisk: true);

        using var quota = AuditSpoolQuotaLease.AcquireExisting(options.SpoolDirectory);
        Assert.Throws<IOException>(() =>
            AuditAnchoredWriterStartupPreflight.RunUnderQuota(options, quota));

        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.CheckpointFileName(BootId))));
        Assert.Single(Directory.GetFiles(
            options.RootDirectory,
            "audit.retirement-*.json"));
    }

    [Fact]
    public void Unix_publish_crash_hard_link_alias_recovers_before_intent_parsing()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot());
        CreateCompletedChain(options, aged: true);
        Assert.Throws<IOException>(() =>
            AuditCompletedChainRetirement.TryRetire(
                options,
                BootId,
                DateTimeOffset.UtcNow,
                requiredHeadroomBytes: 0,
                (point, _) =>
                {
                    if (point == AuditCompletedChainRetirementFaultPoint.IntentPublished)
                        throw new IOException("stop after intent");
                }));
        var intent = Assert.Single(Directory.GetFiles(
            options.RootDirectory,
            "audit.retirement-*.json"));
        var alias = Path.Combine(
            options.RootDirectory,
            $".audit.retirement-{BootId:N}.{Guid.NewGuid():N}.tmp");
        Assert.Equal(0, CreateHardLink(intent, alias));

        using (var quota = AuditSpoolQuotaLease.AcquireExisting(options.SpoolDirectory))
            AuditAnchoredWriterStartupPreflight.RunUnderQuota(options, quota);

        AssertRetired(options);
        Assert.False(File.Exists(alias));
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-completed-retirement-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static AuditOptions Options(string root) => AuditOptions.Create(
        root,
        AuditProtectionMode.Anchored,
        ConfigurationIdentity,
        maxRecordBytes: 4096,
        segmentBytes: 32_768,
        aggregateBytes: 131_072,
        emergencyReserveBytes: 8192,
        retentionAge: TimeSpan.FromMinutes(10),
        maxEvidenceBytes: 4096,
        evidenceAggregateBytes: 16_384,
        evidenceRetentionAge: TimeSpan.FromMinutes(10));

    private static AuditExportCheckpointStore CreateCompletedChain(
        AuditOptions options,
        bool aged,
        bool keepStore = false)
    {
        var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        try
        {
            SerializedAuditEvent first;
            SerializedAuditEvent second;
            using (var sink = new FileAuditJournalSink(
                       options,
                       BootId,
                       checkpointStore: store))
            {
                first = Record(1, previousEventHash: null);
                sink.Append(first.Utf8Line);
                sink.FlushToDisk();
                if (!sink.CanReserve(options.SegmentBytes))
                    throw new IOException("The retirement fixture could not rotate.");
                second = Record(2, first.EventHash);
                sink.Append(second.Utf8Line);
                sink.FlushToDisk();
            }

            var firstSegment = AuditSpoolSegmentIdentity.Create(BootId, 0);
            store.SaveForTests(new AuditExportCheckpoint(
                BootId,
                chainComplete: false,
                firstSegment,
                first.Utf8Line.Length,
                first.Sequence,
                first.EventId,
                blockedRecord: null));
            var final = AuditSpoolSegmentIdentity.Create(BootId, 1);
            var finalOpen = new AuditExportCheckpoint(
                BootId,
                chainComplete: false,
                final,
                second.Utf8Line.Length,
                second.Sequence,
                second.EventId,
                blockedRecord: null);
            store.SaveForTests(finalOpen);
            store.SaveForTests(new AuditExportCheckpoint(
                BootId,
                chainComplete: true,
                final,
                second.Utf8Line.Length,
                second.Sequence,
                second.EventId,
                blockedRecord: null));
            if (aged)
            {
                foreach (var path in Segments(options))
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(1));
            }

            if (keepStore)
                return store;
            store.Dispose();
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    private static CompletedDisposition CreateCompletedDispositionControls(
        AuditOptions options)
    {
        var eventId = Guid.CreateVersion7();
        var spool = AuditSpoolSegmentIdentity.Create(BootId, 0);
        var blocked = new AuditExportBlockedRecord(
            spool,
            byteOffset: 0,
            sequence: 1,
            eventId: eventId,
            failureClass: AuditExportFailureClass.Data,
            detailCode: "test.block",
            responseDigest: null,
            firstFailureUtc: DateTimeOffset.UtcNow,
            exportConfigurationIdentity: ConfigurationIdentity);
        var position = new DispositionPosition(spool, 0, 1, 1, eventId);
        var proof = AuditOperatorDispositionProof.AcknowledgedGap("operator.accepted");
        var intent = AuditOperatorDispositionIntent.CreateOrOpen(
            options,
            position,
            blocked,
            proof);

        using var admin = AdminJournal(options);
        var operations = new AuditAdminOperations(options, admin.Journal);
        Assert.Equal(
            intent.DispositionId,
            operations.ApplyPermanentBlockDisposition(BootId, eventId, proof));
        return new CompletedDisposition(eventId, intent.DispositionId);
    }

    private static AdminJournalFixture AdminJournal(AuditOptions options)
    {
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge);
        var journal = new AuditJournal(
            options,
            new AuditHealth(options),
            sink,
            "retirement-disposition-test",
            binaryDigest: null,
            Guid.NewGuid(),
            Guid.NewGuid());
        return new AdminJournalFixture(journal, sink);
    }

    private static void CreateOpenChain(AuditOptions options)
    {
        using var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        using var sink = new FileAuditJournalSink(
            options,
            BootId,
            checkpointStore: store);
        var record = Record(1, previousEventHash: null);
        sink.Append(record.Utf8Line);
        sink.FlushToDisk();
    }

    private static SerializedAuditEvent Record(long sequence, string? previousEventHash) =>
        AuditEventSerializer.Serialize(
            sequence,
            previousEventHash,
            new AuditProducerContext(
                HostId,
                BootId,
                null,
                4321,
                "retirement-test",
                ConfigurationIdentity),
            new AuditEventInput
            {
                EventType = sequence == 1 ? "server.started" : "server.stopped",
                Session = new AuditSession(),
                Actor = new AuditActor { AttributionStrength = "unknown" },
                Correlation = new AuditCorrelation(),
                Request = new AuditRequest(),
                Routing = new AuditRouting(),
                Outcome = new AuditOutcome
                {
                    State = sequence == 1 ? "started" : "stopped",
                    TerminationCertainty = "not_applicable",
                },
                Coverage = new AuditCoverage
                {
                    PtkRequest = false,
                    RootProcessObserved = "not_applicable",
                    DescendantsObserved = "not_applicable",
                    RemoteEffectObserved = "not_applicable",
                },
                Audit = new AuditEventHealth
                {
                    ProtectionMode = "anchored",
                    ExportConfigurationIdentity = ConfigurationIdentity,
                    HealthState = "healthy",
                },
            },
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static string[] Segments(AuditOptions options) =>
        Directory.GetFiles(options.SpoolDirectory)
            .Where(path => AuditSpoolSegmentIdentity.TryParse(
                Path.GetFileName(path),
                out _))
            .ToArray();

    private static string[] IntentPaths(AuditOptions options) =>
        Directory.GetFiles(options.RootDirectory)
            .Where(path => AuditOperatorDispositionIntent.TryParseFileName(
                Path.GetFileName(path),
                out var bootId,
                out _) && bootId == BootId)
            .ToArray();

    private static string[] OutcomePaths(AuditOptions options) =>
        Directory.GetFiles(options.RootDirectory)
            .Where(path => AuditOperatorDispositionOutcome.TryParseFileName(
                Path.GetFileName(path),
                out var bootId,
                out _) && bootId == BootId)
            .ToArray();

    private static void AssertRetired(AuditOptions options)
    {
        Assert.Empty(Segments(options));
        Assert.False(File.Exists(Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.CheckpointFileName(BootId))));
        Assert.False(File.Exists(Path.Combine(
            options.RootDirectory,
            AuditExportCheckpointStore.LockFileName(BootId))));
        Assert.Empty(Directory.GetFiles(
            options.RootDirectory,
            "audit.retirement-*"));
        Assert.Empty(IntentPaths(options));
        Assert.Empty(OutcomePaths(options));
        Assert.True(File.Exists(Path.Combine(
            options.SpoolDirectory,
            AuditSpoolQuotaLease.ControlFileName)));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLink(string existingPath, string newPath);

    private sealed record CompletedDisposition(Guid EventId, Guid DispositionId);

    private sealed record DispositionPosition(
        AuditSpoolSegmentIdentity Spool,
        long StartOffset,
        long NextOffset,
        long Sequence,
        Guid EventId) : IAuditClosedSpoolRecordPosition
    {
        public string? PreviousEventHash => null;
        public string EventHash => new('0', 64);
        public ReadOnlyMemory<byte> ExactJsonlBytes => ReadOnlyMemory<byte>.Empty;
    }

    private sealed record AdminJournalFixture(
        AuditJournal Journal,
        InMemoryAuditJournalSink Sink) : IDisposable
    {
        public void Dispose() => Journal.Dispose();
    }
}

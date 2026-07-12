using System.Text;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditAnchoredSpoolPrefixRetentionTests : IDisposable
{
    private static readonly Guid BootId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid HostId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
    private static readonly DateTimeOffset BaseTime =
        DateTimeOffset.Parse("2026-07-12T12:34:56.1234567Z");
    private const string ConfigurationIdentity =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Preserve the test failure that prevented ordinary cleanup.
            }
        }
    }

    [Fact]
    public void Checkpoint_pins_quota_eligible_segment_until_acknowledgment_advances()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(3);
            var firstPath = WriteSegment(options, 0, records[0]);
            var checkpointPath = WriteSegment(options, 1, records[1]);
            var suffixPath = WriteSegment(options, 2, records[2]);
            store.SaveForTests(CheckpointAfter(records[0], segment: 0));

            var pinned = AuditAnchoredSpoolPrefixRetention.Sweep(
                options,
                store,
                BaseTime,
                requiredHeadroomBytes: options.AggregateBytes);

            Assert.Equal(0, pinned.DeletedSegmentCount);
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(checkpointPath));
            Assert.True(File.Exists(suffixPath));

            store.SaveForTests(CheckpointAfter(records[1], segment: 1));
            var released = AuditAnchoredSpoolPrefixRetention.Sweep(
                options,
                store,
                BaseTime,
                requiredHeadroomBytes: options.AggregateBytes);

            Assert.Equal(1, released.DeletedSegmentCount);
            Assert.False(released.HeadroomSatisfied);
            Assert.False(File.Exists(firstPath));
            Assert.True(File.Exists(checkpointPath));
            Assert.True(File.Exists(suffixPath));
        }
    }

    [Fact]
    public void Age_sweep_deletes_only_the_old_acknowledged_prefix()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(3);
            var oldPrefix = WriteSegment(options, 0, records[0]);
            var checkpointSegment = WriteSegment(options, 1, records[1]);
            var blockedSegment = WriteSegment(options, 2, records[2]);
            File.SetLastWriteTimeUtc(
                oldPrefix,
                BaseTime.Subtract(options.RetentionAge).AddSeconds(-1).UtcDateTime);
            File.SetLastWriteTimeUtc(
                checkpointSegment,
                BaseTime.Subtract(options.RetentionAge).AddSeconds(-1).UtcDateTime);
            store.SaveForTests(CheckpointAfter(records[0], segment: 0));
            store.SaveForTests(new AuditExportCheckpoint(
                BootId,
                chainComplete: false,
                AuditSpoolSegmentIdentity.Create(BootId, 1),
                records[1].Utf8Line.Length,
                records[1].Sequence,
                records[1].EventId,
                new AuditExportBlockedRecord(
                    AuditSpoolSegmentIdentity.Create(BootId, 2),
                    byteOffset: 0,
                    records[2].Sequence,
                    records[2].EventId,
                    AuditExportFailureClass.Configuration,
                    "http.401",
                    responseDigest: null,
                    BaseTime,
                    ConfigurationIdentity)));

            var outcome = AuditAnchoredSpoolPrefixRetention.Sweep(
                options,
                store,
                BaseTime,
                requiredHeadroomBytes: 0);

            Assert.Equal(1, outcome.DeletedSegmentCount);
            Assert.True(outcome.DeletedBytes > 0);
            Assert.True(outcome.HeadroomSatisfied);
            Assert.False(File.Exists(oldPrefix));
            Assert.True(File.Exists(checkpointSegment));
            Assert.True(File.Exists(blockedSegment));
        }
    }

    [Fact]
    public void Exclusive_reader_handle_pins_the_prefix_until_release()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            var prefixPath = WriteSegment(options, 0, records[0]);
            WriteSegment(options, 1, records[1]);
            store.SaveForTests(CheckpointAfter(records[0], segment: 0));
            store.SaveForTests(CheckpointAfter(records[1], segment: 1));

            using (var retainedReader = new FileStream(
                       prefixPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                var pinned = AuditAnchoredSpoolPrefixRetention.Sweep(
                    options,
                    store,
                    BaseTime,
                    requiredHeadroomBytes: options.AggregateBytes);
                Assert.Equal(0, pinned.DeletedSegmentCount);
                Assert.True(File.Exists(prefixPath));
            }

            var released = AuditAnchoredSpoolPrefixRetention.Sweep(
                options,
                store,
                BaseTime,
                requiredHeadroomBytes: options.AggregateBytes);
            Assert.Equal(1, released.DeletedSegmentCount);
            Assert.False(File.Exists(prefixPath));
        }
    }

    [Fact]
    public void Path_swap_never_authorizes_deleting_the_replacement()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            var prefixPath = WriteSegment(options, 0, records[0]);
            WriteSegment(options, 1, records[1]);
            store.SaveForTests(CheckpointAfter(records[0], segment: 0));
            store.SaveForTests(CheckpointAfter(records[1], segment: 1));
            var movedPath = Path.Combine(options.SpoolDirectory, ".retained-original");
            var replacementBytes = Encoding.UTF8.GetBytes("replacement");

            Assert.Throws<IOException>(() =>
                AuditAnchoredSpoolPrefixRetention.Sweep(
                    options,
                    store,
                    BaseTime,
                    requiredHeadroomBytes: options.AggregateBytes,
                    candidateRetainedForTests: path =>
                    {
                        Assert.Equal(prefixPath, path);
                        File.Move(path, movedPath);
                        using var replacement = SecureAuditStorage.CreateExclusiveFile(path);
                        replacement.Write(replacementBytes);
                        replacement.Flush(flushToDisk: true);
                    }));

            Assert.Equal(replacementBytes, File.ReadAllBytes(prefixPath));
            Assert.Equal(records[0].Utf8Line.ToArray(), File.ReadAllBytes(movedPath));
        }
    }

    [Fact]
    public void Closed_reader_recovers_from_a_checkpoint_proved_floor_and_rejects_boundary_tamper()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(4);
            var deletedPath = WriteSegment(options, 0, records[0]);
            var retainedFloor = WriteSegment(options, 1, records[1], records[2]);
            WriteSegment(options, 2, records[3]);
            store.SaveForTests(CheckpointAfter(records[0], segment: 0));
            store.SaveForTests(CheckpointAfter(records[1], segment: 1));
            store.SaveForTests(new AuditExportCheckpoint(
                BootId,
                chainComplete: false,
                AuditSpoolSegmentIdentity.Create(BootId, 1),
                records[1].Utf8Line.Length + records[2].Utf8Line.Length,
                records[2].Sequence,
                records[2].EventId,
                blockedRecord: null));
            _ = AuditAnchoredSpoolPrefixRetention.Sweep(
                options,
                store,
                BaseTime,
                requiredHeadroomBytes: options.AggregateBytes);
            Assert.False(File.Exists(deletedPath));

            using (var reader = new AuditClosedSpoolChainReader(options, store))
            {
                var next = Assert.IsType<AuditClosedSpoolRecovery.Record>(
                    reader.ResolveCheckpoint()).Position;
                Assert.Equal(4, next.Sequence);
                Assert.Equal(AuditSpoolSegmentIdentity.Create(BootId, 2), next.Spool);
                Assert.Equal(records[3].EventId, next.EventId);
            }

            var tampered = File.ReadAllBytes(retainedFloor);
            var marker = Encoding.UTF8.GetBytes("\"event_type\":\"call.accepted\"");
            var markerOffset = tampered.AsSpan().IndexOf(marker);
            Assert.True(markerOffset >= 0);
            tampered[markerOffset + marker.Length - 2] = (byte)'x';
            File.WriteAllBytes(retainedFloor, tampered);

            using var tamperReader = new AuditClosedSpoolChainReader(options, store);
            Assert.Throws<IOException>(() => tamperReader.ResolveCheckpoint());
        }
    }

    [Fact]
    public void Anchored_sink_startup_accepts_a_checkpoint_proved_retained_floor()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(3);
            WriteSegment(options, 0, records[0]);
            var retainedFloor = WriteSegment(options, 1, records[1]);
            WriteSegment(options, 2, records[2]);
            store.SaveForTests(CheckpointAfter(records[0], segment: 0));
            store.SaveForTests(CheckpointAfter(records[1], segment: 1));
            _ = AuditAnchoredSpoolPrefixRetention.Sweep(
                options,
                store,
                BaseTime,
                requiredHeadroomBytes: options.AggregateBytes);
            Assert.True(File.Exists(retainedFloor));

            var nextBoot = Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
            using var nextStore = AuditExportCheckpointStore.CreateForWriter(options, nextBoot);
            using var nextSink = new FileAuditJournalSink(
                options,
                nextBoot,
                () => BaseTime,
                checkpointStore: nextStore);

            Assert.Equal(nextBoot, nextSink.CurrentSegmentIdentity.SupervisorBootId);
            Assert.True(File.Exists(retainedFloor));
        }
    }

    [Fact]
    public void Anchored_sink_and_reader_reject_a_floor_beyond_the_checkpoint()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(2);
            var checkpointSegment = WriteSegment(options, 0, records[0]);
            WriteSegment(options, 1, records[1]);
            store.SaveForTests(CheckpointAfter(records[0], segment: 0));
            File.Delete(checkpointSegment);

            using (var reader = new AuditClosedSpoolChainReader(options, store))
                Assert.Throws<IOException>(() => reader.ResolveCheckpoint());

            var nextBoot = Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
            using var nextStore = AuditExportCheckpointStore.CreateForWriter(options, nextBoot);
            Assert.Throws<IOException>(() => new FileAuditJournalSink(
                options,
                nextBoot,
                () => BaseTime,
                checkpointStore: nextStore));
        }
    }

    [Fact]
    public void Retention_rejects_an_internal_gap_before_granting_any_deletion()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(3);
            var prefixPath = WriteSegment(options, 0, records[0]);
            WriteSegment(options, 2, records[2]);
            store.SaveForTests(CheckpointAfter(records[0], segment: 0));
            store.SaveForTests(CheckpointAfter(records[1], segment: 1));
            store.SaveForTests(CheckpointAfter(records[2], segment: 2));

            Assert.Throws<IOException>(() =>
                AuditAnchoredSpoolPrefixRetention.Sweep(
                    options,
                    store,
                    BaseTime,
                    requiredHeadroomBytes: options.AggregateBytes));
            Assert.True(File.Exists(prefixPath));
        }
    }

    [Fact]
    public void Retained_floor_requires_an_external_previous_hash_link()
    {
        var (options, store) = OwnedFixture();
        using (store)
        {
            var records = Records(1);
            var invalidBoundary = SerializeRecord(sequence: 1, previousHash: null);
            var suffix = SerializeRecord(
                sequence: 2,
                previousHash: invalidBoundary.EventHash);
            WriteSegment(options, 0, records[0]);
            WriteSegment(options, 1, invalidBoundary);
            WriteSegment(options, 2, suffix);
            store.SaveForTests(CheckpointAfter(records[0], segment: 0));
            store.SaveForTests(new AuditExportCheckpoint(
                BootId,
                chainComplete: false,
                AuditSpoolSegmentIdentity.Create(BootId, 1),
                invalidBoundary.Utf8Line.Length,
                sequence: 2,
                invalidBoundary.EventId,
                blockedRecord: null));
            _ = AuditAnchoredSpoolPrefixRetention.Sweep(
                options,
                store,
                BaseTime,
                requiredHeadroomBytes: options.AggregateBytes);

            using var reader = new AuditClosedSpoolChainReader(options, store);
            Assert.Throws<IOException>(() => reader.ResolveCheckpoint());

            var nextBoot = Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
            using var nextStore = AuditExportCheckpointStore.CreateForWriter(options, nextBoot);
            Assert.Throws<IOException>(() => new FileAuditJournalSink(
                options,
                nextBoot,
                () => BaseTime,
                checkpointStore: nextStore));
        }
    }

    private (AuditOptions Options, AuditExportCheckpointStore Store) OwnedFixture()
    {
        var options = AuditOptions.Create(
            NewRoot(),
            AuditProtectionMode.Anchored,
            ConfigurationIdentity,
            maxRecordBytes: 4096,
            segmentBytes: 16_384,
            aggregateBytes: 65_536,
            emergencyReserveBytes: 8192,
            retentionAge: TimeSpan.FromMinutes(10),
            maxEvidenceBytes: 4096,
            evidenceAggregateBytes: 4096,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
        var store = AuditExportCheckpointStore.CreateForWriter(options, BootId);
        _ = SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        using (AuditSpoolQuotaLease.CreateControlAndAcquire(options.SpoolDirectory))
        {
        }
        return (options, store);
    }

    private string NewRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-anchored-retention-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static SerializedAuditEvent[] Records(int count)
    {
        var records = new SerializedAuditEvent[count];
        string? previousHash = null;
        for (var index = 0; index < count; index++)
        {
            records[index] = SerializeRecord(index + 1, previousHash);
            previousHash = records[index].EventHash;
        }
        return records;
    }

    private static SerializedAuditEvent SerializeRecord(
        long sequence,
        string? previousHash) =>
        AuditEventSerializer.Serialize(
            sequence,
            previousHash,
            new AuditProducerContext(
                HostId,
                BootId,
                null,
                4321,
                "1.2.3-test",
                ConfigurationIdentity),
            Input(),
            Guid.CreateVersion7(),
            BaseTime,
            BaseTime);

    private static AuditEventInput Input() => new()
    {
        EventType = "call.accepted",
        Session = new AuditSession(),
        Actor = new AuditActor
        {
            AttributionStrength = "transport_only",
            Transport = "mcp_stdio",
        },
        Correlation = new AuditCorrelation(),
        Request = new AuditRequest(),
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome { TerminationCertainty = "not_applicable" },
        Coverage = new AuditCoverage
        {
            PtkRequest = true,
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
    };

    private static string WriteSegment(
        AuditOptions options,
        int index,
        params SerializedAuditEvent[] records)
    {
        var path = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(BootId, index).FileName);
        using var stream = SecureAuditStorage.CreateExclusiveFile(path);
        foreach (var record in records)
            stream.Write(record.Utf8Line.Span);
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static AuditExportCheckpoint CheckpointAfter(
        SerializedAuditEvent record,
        int segment) => new(
            BootId,
            chainComplete: false,
            AuditSpoolSegmentIdentity.Create(BootId, segment),
            record.Utf8Line.Length,
            record.Sequence,
            record.EventId,
            blockedRecord: null);
}

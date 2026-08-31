using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Tests;

public sealed class AuditDestinationS3Tests : IDisposable
{
    private readonly List<string> _roots = [];

    [Fact]
    public void Destination_registry_requires_explicit_multiple_opt_in_and_is_transactional()
    {
        var root = NewRoot("destination-registry");
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var openFailure);
        Assert.Null(openFailure);

        var firstDraft = Draft("primary", "https://siem.example/v1/logs", "first-secret");
        Assert.True(registry.TryAdd(
            firstDraft,
            confirmedSensitiveDuplication: false,
            DateTimeOffset.Parse("2026-08-14T10:00:00Z"),
            out var first,
            out var firstFailure), firstFailure);
        Assert.NotNull(first);

        var secondDraft = Draft("secondary", "https://backup.example/v1/logs", "second-secret");
        Assert.False(registry.TryAdd(
            secondDraft,
            confirmedSensitiveDuplication: false,
            DateTimeOffset.UtcNow,
            out _,
            out var confirmationFailure));
        Assert.Equal("sensitive_duplication_confirmation_required", confirmationFailure);
        Assert.Single(registry.Snapshot().Destinations);

        Assert.True(registry.TryAdd(
            secondDraft,
            confirmedSensitiveDuplication: true,
            DateTimeOffset.UtcNow,
            out var second,
            out var secondFailure), secondFailure);
        Assert.NotNull(second);
        Assert.Equal(2, registry.Snapshot().Destinations.Count);

        var beforeInvalid = registry.Snapshot();
        Assert.False(registry.TryAdd(
            Draft("bad", "http://remote.example/v1/logs", "secret"),
            confirmedSensitiveDuplication: true,
            DateTimeOffset.UtcNow,
            out _,
            out var invalidFailure));
        Assert.Equal("invalid_endpoint", invalidFailure);
        Assert.Equal(beforeInvalid, registry.Snapshot());

        var reopened = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var reopenFailure);
        Assert.Null(reopenFailure);
        Assert.Equal(
            registry.Snapshot().Destinations.Select(item => item.DestinationId),
            reopened.Snapshot().Destinations.Select(item => item.DestinationId));
    }

    [Fact]
    public void Destination_certificate_pin_is_normalized_persisted_and_https_only()
    {
        var root = NewRoot("destination-certificate-pin");
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var openFailure);
        Assert.Null(openFailure);

        var colonPin = string.Join(':', Enumerable.Repeat("ab", 32));
        var pinned = Draft("pinned", "https://mini.example/v1/logs", "secret") with
        {
            ServerCertificateSha256 = colonPin,
        };
        Assert.True(registry.TryAdd(
            pinned,
            confirmedSensitiveDuplication: false,
            DateTimeOffset.UtcNow,
            out var created,
            out var addFailure), addFailure);
        Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)),
            created!.ServerCertificateSha256);

        var reopened = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var reopenFailure);
        Assert.Null(reopenFailure);
        Assert.Equal(created.ServerCertificateSha256,
            Assert.Single(reopened.Snapshot().Destinations).ServerCertificateSha256);

        var beforeInvalid = reopened.Snapshot();
        Assert.False(reopened.TryAdd(
            Draft("bad pin", "https://other.example/v1/logs", "secret") with
            {
                ServerCertificateSha256 = "not-a-pin",
            },
            confirmedSensitiveDuplication: true,
            DateTimeOffset.UtcNow,
            out _,
            out var invalidFailure));
        Assert.Equal("invalid_server_certificate_sha256", invalidFailure);
        Assert.Equal(beforeInvalid, reopened.Snapshot());

        Assert.False(reopened.TryAdd(
            Draft("plaintext pin", "http://127.0.0.1:19418/v1/logs", "secret") with
            {
                ServerCertificateSha256 = string.Concat(Enumerable.Repeat("AB", 32)),
            },
            confirmedSensitiveDuplication: true,
            DateTimeOffset.UtcNow,
            out _,
            out var plaintextFailure));
        Assert.Equal("certificate_pin_requires_https", plaintextFailure);

        var registryPath = Path.Combine(root, AuditDestinationRegistry.FileName);
        var legacyBytes = File.ReadAllText(registryPath)
            .Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal);
        legacyBytes = System.Text.RegularExpressions.Regex.Replace(
            legacyBytes,
            ",\"server_certificate_sha256\":\"[0-9A-F]+\"",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        File.WriteAllText(registryPath, legacyBytes, new UTF8Encoding(false));

        var legacyReopened = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var legacyFailure);
        Assert.Null(legacyFailure);
        Assert.Null(Assert.Single(
            legacyReopened.Snapshot().Destinations).ServerCertificateSha256);
    }

    [Fact]
    public async Task Registry_serializes_concurrent_changes_and_failed_publish_preserves_prior_set()
    {
        var root = NewRoot("destination-registry-concurrency");
        var failPublish = false;
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var openFailure,
            beforePublishForTests: () =>
            {
                if (failPublish) throw new IOException("injected destination publish failure");
            });
        Assert.Null(openFailure);

        var additions = Enumerable.Range(0, 8)
            .Select(index => Task.Run(() =>
            {
                var succeeded = registry.TryAdd(
                    Draft(
                        $"destination-{index}",
                        $"https://siem-{index}.example/v1/logs",
                        $"secret-{index}"),
                    confirmedSensitiveDuplication: true,
                    DateTimeOffset.UtcNow,
                    out var created,
                    out var failure);
                return (succeeded, created, failure);
            }))
            .ToArray();
        var results = await Task.WhenAll(additions);
        Assert.All(results, result => Assert.True(result.succeeded, result.failure));
        Assert.Equal(8, registry.Snapshot().Destinations.Count);
        Assert.Equal(
            8,
            registry.Snapshot().Destinations.Select(item => item.DestinationId).Distinct().Count());

        var before = registry.Snapshot();
        var beforeBytes = File.ReadAllBytes(Path.Combine(root, AuditDestinationRegistry.FileName));
        failPublish = true;
        Assert.False(registry.TryUpdate(
            before.Destinations[0].DestinationId,
            Draft("replacement", "https://replacement.example/v1/logs", "replacement-secret"),
            DateTimeOffset.UtcNow,
            out _,
            out var updateFailure));
        Assert.Equal("configuration_unwritable", updateFailure);
        Assert.Equal(before.Revision, registry.Snapshot().Revision);
        Assert.Equal(
            before.Destinations.ToArray(),
            registry.Snapshot().Destinations.ToArray());
        Assert.Equal(
            beforeBytes,
            File.ReadAllBytes(Path.Combine(root, AuditDestinationRegistry.FileName)));

        var reopened = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var reopenFailure);
        Assert.Null(reopenFailure);
        Assert.Equal(before.Revision, reopened.Snapshot().Revision);
        Assert.Equal(
            before.Destinations.ToArray(),
            reopened.Snapshot().Destinations.ToArray());
    }

    [Fact]
    public void Stale_registry_instance_refreshes_before_applying_a_destination_change()
    {
        var root = NewRoot("destination-registry-stale-instance");
        var current = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var currentFailure);
        var stale = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var staleFailure);
        Assert.Null(currentFailure);
        Assert.Null(staleFailure);
        Assert.True(current.TryAdd(
            Draft("current", "https://current.example/v1/logs", "current-secret"),
            confirmedSensitiveDuplication: false,
            DateTimeOffset.UtcNow,
            out var currentDestination,
            out var addFailure), addFailure);
        Assert.Contains(
            currentDestination!.DestinationId,
            stale.EnabledDestinationIds());

        Assert.True(stale.TryAdd(
            Draft("stale", "https://stale.example/v1/logs", "stale-secret"),
            confirmedSensitiveDuplication: true,
            DateTimeOffset.UtcNow,
            out var staleDestination,
            out var staleAddFailure), staleAddFailure);

        var reopened = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var reopenFailure);
        Assert.Null(reopenFailure);
        Assert.Equal(2, reopened.Snapshot().Destinations.Count);
        Assert.Contains(
            reopened.Snapshot().Destinations,
            item => item.DestinationId == currentDestination!.DestinationId);
        Assert.Contains(
            reopened.Snapshot().Destinations,
            item => item.DestinationId == staleDestination!.DestinationId);
    }

    [Fact]
    public void Legacy_export_migration_is_stable_and_only_it_receives_pre_v6_records()
    {
        var root = NewRoot("destination-migration");
        var legacy = new AuditExportSettings(
            AuditDestinationKind.OtlpHttp,
            new Uri("https://siem.example/v1/logs"),
            "legacy-secret");
        var firstOpen = AuditDestinationRegistry.Open(root, legacy, out var failure);
        Assert.Null(failure);
        var migrated = Assert.Single(firstOpen.Snapshot().Destinations);
        Assert.True(migrated.IncludeLegacyRecords);
        Assert.True(AuditExportCoordinator.IsRequiredBy(
            """{"schema_version":"ptk.audit/5"}""",
            migrated));

        var secondOpen = AuditDestinationRegistry.Open(root, legacy, out failure);
        Assert.Null(failure);
        Assert.Equal(
            migrated.DestinationId,
            Assert.Single(secondOpen.Snapshot().Destinations).DestinationId);

        var prospective = migrated with
        {
            DestinationId = Guid.NewGuid(),
            OperatorLabel = "prospective",
            IncludeLegacyRecords = false,
        };
        Assert.False(AuditExportCoordinator.IsRequiredBy(
            """{"schema_version":"ptk.audit/5"}""",
            prospective));
    }

    [Fact]
    public void Journal_admission_reads_the_current_sorted_destination_obligations()
    {
        var root = NewRoot("destination-journal-admission");
        var options = AuditOptions.Create(root);
        var health = new AuditHealth(options);
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var openFailure);
        Assert.Null(openFailure);
        Assert.True(registry.TryAdd(
            Draft("zulu", "https://zulu.example/v1/logs", "zulu-secret"),
            confirmedSensitiveDuplication: false,
            DateTimeOffset.UtcNow,
            out var zulu,
            out var firstFailure), firstFailure);
        Assert.True(registry.TryAdd(
            Draft("alpha", "https://alpha.example/v1/logs", "alpha-secret"),
            confirmedSensitiveDuplication: true,
            DateTimeOffset.UtcNow,
            out var alpha,
            out var secondFailure), secondFailure);
        var sink = new InMemoryAuditJournalSink(
            options.SegmentBytes,
            options.AggregateBytes,
            options.ProtectionMode,
            options.RetentionAge);
        using var journal = new AuditJournal(
            options,
            health,
            sink,
            "test-version",
            binaryDigest: null,
            hostId: Guid.NewGuid(),
            supervisorBootId: Guid.NewGuid(),
            previousSupervisorBootId: Guid.NewGuid(),
            requiredDestinationIds: registry.EnabledDestinationIds);
        Assert.True(journal.TryReserve(1, out var reservation, out var reserveFailure), reserveFailure);
        var serialized = journal.Append(reservation!, Input("call.completed"));

        using var document = JsonDocument.Parse(serialized.Utf8Line[..^1]);
        Assert.Equal(
            AuditEventSerializer.DestinationObligationSchemaVersion,
            document.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(
            new[] { alpha!.DestinationId, zulu!.DestinationId }.Order(),
            document.RootElement.GetProperty("required_destination_ids")
                .EnumerateArray()
                .Select(item => Guid.Parse(item.GetString()!)));
    }

    [Fact]
    public void V6_obligations_route_only_to_named_destinations_and_status_redacts_secrets()
    {
        var root = NewRoot("destination-routing");
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out _);
        Assert.True(registry.TryAdd(
            Draft("primary", "https://siem.example/private/path", "NEVER-EXPOSE-ME"),
            false,
            DateTimeOffset.UtcNow,
            out var primary,
            out _));
        Assert.True(registry.TryAdd(
            Draft("secondary", "https://other.example/collector", "SECOND-SECRET"),
            true,
            DateTimeOffset.UtcNow,
            out var secondary,
            out _));
        var record = JsonSerializer.Serialize(new
        {
            schema_version = AuditEventSerializer.DestinationObligationSchemaVersion,
            required_destination_ids = new[] { primary!.DestinationId.ToString("D") },
        });
        Assert.True(AuditExportCoordinator.IsRequiredBy(record, primary));
        Assert.False(AuditExportCoordinator.IsRequiredBy(record, secondary!));

        var options = AuditOptions.Create(root);
        var evidence = new ScriptEvidenceStoreProvider(options);
        var backfills = new AuditBackfillRegistry(root);
        var coordinator = new AuditExportCoordinator(
            options,
            registry,
            backfills,
            evidence,
            () => null,
            new AuditExportHealth());
        var statuses = coordinator.Statuses();
        var serialized = JsonSerializer.Serialize(statuses);
        Assert.DoesNotContain("NEVER-EXPOSE-ME", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("SECOND-SECRET", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/path", serialized, StringComparison.Ordinal);
        Assert.Contains("credential:", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Independent_exporters_skip_unowned_history_and_advance_only_their_cursor()
    {
        var root = NewRoot("destination-independent");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        Directory.CreateDirectory(options.EvidenceDirectory);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var absentId = Guid.NewGuid();
        var bootId = Guid.NewGuid();
        var records = new[]
        {
            Record(bootId, 1, firstId, "2026-08-14T10:00:00Z"),
            Record(bootId, 2, secondId, "2026-08-14T10:01:00Z"),
        };
        WriteSegment(options, bootId, records);
        var evidence = new ScriptEvidenceStoreProvider(options);

        var firstDestination = new RecordingDestination();
        var firstCursor = new AuditExportCursorStore(
            root,
            AuditExportCursorStore.DestinationFileName(firstId));
        await using (var first = Service(
                         options,
                         firstDestination,
                         firstCursor,
                         firstId,
                         evidence))
        {
            Assert.Equal(1, await first.DrainOnceAsync(CancellationToken.None));
        }
        Assert.Single(firstDestination.Records);
        Assert.Contains(firstId.ToString("D"), firstDestination.Records[0]);

        var secondDestination = new RecordingDestination();
        var secondCursor = new AuditExportCursorStore(
            root,
            AuditExportCursorStore.DestinationFileName(secondId));
        await using (var second = Service(
                         options,
                         secondDestination,
                         secondCursor,
                         secondId,
                         evidence))
        {
            Assert.Equal(1, await second.DrainOnceAsync(CancellationToken.None));
        }
        Assert.Single(secondDestination.Records);
        Assert.Contains(secondId.ToString("D"), secondDestination.Records[0]);

        var prospectiveDestination = new RecordingDestination();
        var prospectiveCursor = new AuditExportCursorStore(
            root,
            AuditExportCursorStore.DestinationFileName(absentId));
        await using (var prospective = Service(
                         options,
                         prospectiveDestination,
                         prospectiveCursor,
                         absentId,
                         evidence))
        {
            Assert.Equal(0, await prospective.DrainOnceAsync(CancellationToken.None));
        }
        Assert.Empty(prospectiveDestination.Records);
        var position = prospectiveCursor.Read().For(bootId);
        Assert.NotNull(position);
        Assert.Equal(new FileInfo(Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(bootId, 0).FileName)).Length,
            position!.ByteOffset);
    }

    [Fact]
    public async Task Coordinator_applies_live_changes_and_keeps_partial_failure_independent()
    {
        var root = NewRoot("destination-coordinator-live");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        Directory.CreateDirectory(options.EvidenceDirectory);
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var openFailure);
        Assert.Null(openFailure);
        Assert.True(registry.TryAdd(
            Draft("primary", "https://primary.example/v1/logs", "first-secret"),
            confirmedSensitiveDuplication: false,
            DateTimeOffset.UtcNow,
            out var primary,
            out var addFailure), addFailure);

        var bootId = Guid.NewGuid();
        WriteSegment(
            options,
            bootId,
            [Record(
                bootId,
                1,
                primary!.DestinationId,
                "2026-08-14T10:00:00Z")]);

        var primaryController = new DestinationController(AuditDeliveryResult.Delivered);
        var secondaryController = new DestinationController(
            AuditDeliveryResult.Retryable("secondary_unavailable"));
        var controllers = new Dictionary<string, DestinationController>(StringComparer.Ordinal)
        {
            ["primary.example"] = primaryController,
            ["secondary.example"] = secondaryController,
        };
        var evidence = new ScriptEvidenceStoreProvider(options);
        var backfills = new AuditBackfillRegistry(root);
        await using var coordinator = new AuditExportCoordinator(
            options,
            registry,
            backfills,
            evidence,
            () => null,
            new AuditExportHealth(),
            destinationFactory: settings =>
                controllers[settings.Endpoint!.Host].CreateDestination());
        await coordinator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => primaryController.AcceptedRecords.Count == 1);

        var operations = new AuditDestinationOperations(
            options,
            registry,
            backfills,
            coordinator,
            new AcceptingCredentialValidator());
        var added = await operations.AddAsync(
            Draft("secondary", "https://secondary.example/v1/logs", "second-secret"),
            confirmedSensitiveDuplication: true,
            CancellationToken.None);
        Assert.True(added.Succeeded, added.Failure);
        var secondary = added.Destination!;
        var secondaryCursor = new AuditExportCursorStore(
            root,
            AuditExportCursorStore.DestinationFileName(secondary.DestinationId));
        var segmentPath = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(bootId, 0).FileName);
        await WaitUntilAsync(() =>
            secondaryCursor.Read().For(bootId)?.ByteOffset == new FileInfo(segmentPath).Length);
        Assert.Empty(secondaryController.AcceptedRecords);

        File.AppendAllText(
            segmentPath,
            Record(
                bootId,
                2,
                [primary.DestinationId, secondary.DestinationId],
                "2026-08-14T10:01:00Z") + "\n",
            new UTF8Encoding(false));
        await WaitUntilAsync(() =>
            primaryController.AcceptedRecords.Count == 2 &&
            secondaryController.Attempts > 0);
        await WaitUntilAsync(() => coordinator.Statuses()
            .Single(item => item.DestinationId == secondary.DestinationId)
            .Delivery.PendingEventRecords == 1);

        var primaryCursor = new AuditExportCursorStore(
            root,
            AuditExportCursorStore.DestinationFileName(primary.DestinationId));
        // The fake destination records acceptance before the coordinator
        // persists the cursor advance, so the durable offset is the only
        // condition that proves delivery completed (windows-latest caught
        // the gap: run 33431130367 read 305 of 649).
        await WaitUntilAsync(() =>
            primaryCursor.Read().For(bootId)?.ByteOffset ==
            new FileInfo(segmentPath).Length);
        Assert.Equal(
            new FileInfo(segmentPath).Length,
            primaryCursor.Read().For(bootId)!.ByteOffset);
        Assert.True(
            secondaryCursor.Read().For(bootId)!.ByteOffset < new FileInfo(segmentPath).Length);
        Assert.Equal(2, primaryController.AcceptedRecords.Count);

        secondaryController.Result = AuditDeliveryResult.Delivered;
        await WaitUntilAsync(() => secondaryController.AcceptedRecords.Count == 1);
        await WaitUntilAsync(() => coordinator.Statuses()
            .Single(item => item.DestinationId == secondary.DestinationId)
            .Delivery.PendingEventRecords == 0);
        await WaitUntilAsync(() =>
            secondaryCursor.Read().For(bootId)?.ByteOffset ==
            new FileInfo(segmentPath).Length);
        Assert.Equal(
            new FileInfo(segmentPath).Length,
            secondaryCursor.Read().For(bootId)!.ByteOffset);
        Assert.Equal(2, primaryController.AcceptedRecords.Count);

        var primaryCreations = primaryController.Creations;
        var secondaryCreations = secondaryController.Creations;
        var updated = await operations.UpdateAsync(
            primary.DestinationId,
            Draft("primary updated", "https://primary.example/v1/logs", "new-secret"),
            CancellationToken.None);
        Assert.True(updated.Succeeded, updated.Failure);
        Assert.Equal(primaryCreations + 1, primaryController.Creations);
        Assert.Equal(secondaryCreations, secondaryController.Creations);

        var disabled = await operations.SetEnabledAsync(
            primary.DestinationId,
            enabled: false,
            CancellationToken.None);
        Assert.True(disabled.Succeeded, disabled.Failure);
        Assert.False(coordinator.Statuses()
            .Single(item => item.DestinationId == primary.DestinationId)
            .Enabled);
        var enabled = await operations.SetEnabledAsync(
            primary.DestinationId,
            enabled: true,
            CancellationToken.None);
        Assert.True(enabled.Succeeded, enabled.Failure);
        Assert.True(coordinator.Statuses()
            .Single(item => item.DestinationId == primary.DestinationId)
            .Enabled);
        var removed = await operations.RemoveAsync(
            primary.DestinationId,
            CancellationToken.None);
        Assert.True(removed.Succeeded, removed.Failure);
        Assert.DoesNotContain(
            coordinator.Statuses(),
            item => item.DestinationId == primary.DestinationId);
    }

    [Fact]
    public async Task New_destination_exporter_holds_every_permanent_refusal_for_abandonment()
    {
        var root = NewRoot("destination-refusal");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        Directory.CreateDirectory(options.EvidenceDirectory);
        var destinationId = Guid.NewGuid();
        var bootId = Guid.NewGuid();
        WriteSegment(options, bootId,
            [Record(bootId, 1, destinationId, "2026-08-14T10:00:00Z")]);
        var evidence = new ScriptEvidenceStoreProvider(options);
        var cursor = new AuditExportCursorStore(
            root,
            AuditExportCursorStore.DestinationFileName(destinationId));
        var refusing = new RecordingDestination(AuditDeliveryResult.Permanent("refused"));
        await using var service = Service(
            options,
            refusing,
            cursor,
            destinationId,
            evidence);

        Assert.Equal(0, await service.DrainOnceAsync(CancellationToken.None));
        Assert.Null(cursor.Read().For(bootId));
    }

    [Fact]
    public void Disable_remove_backfill_and_retention_are_explicit_and_conservative()
    {
        var root = NewRoot("destination-retention");
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out _);
        Assert.True(registry.TryAdd(
            Draft("first", "https://first.example/v1/logs", "a"),
            false,
            DateTimeOffset.UtcNow,
            out var first,
            out _));
        Assert.False(registry.TrySetEnabled(
            first!.DestinationId,
            false,
            hasPendingObligations: true,
            out var disableFailure));
        Assert.Equal("pending_obligations_require_abandonment", disableFailure);
        Assert.False(registry.TryRemove(
            first.DestinationId,
            hasPendingObligations: true,
            out var removeFailure));
        Assert.Equal("pending_obligations_require_abandonment", removeFailure);

        var bootId = Guid.NewGuid();
        var segment = AuditSpoolSegmentIdentity.Create(bootId, 0).FileName;
        var missingCursorFloors = ExportRetentionFloor.ReadFloors(root);
        Assert.True(ExportRetentionFloor.IsRequired(segment, missingCursorFloors));

        var cursor = new AuditExportCursorStore(
            root,
            AuditExportCursorStore.DestinationFileName(first.DestinationId));
        Assert.True(cursor.TryWrite(AuditExportCursor.Start.WithBoot(
            bootId,
            new AuditExportBootPosition(
                segment,
                1,
                1,
                LastWasLifecycleTerminal: true,
                DateTimeOffset.UtcNow))));
        Assert.False(ExportRetentionFloor.IsRequired(
            segment,
            ExportRetentionFloor.ReadFloors(root)));

        var backfills = new AuditBackfillRegistry(root);
        Assert.False(backfills.TryStart(
            first.DestinationId,
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            "operator",
            confirmed: false,
            out _,
            out var confirmationFailure));
        Assert.Equal("backfill_confirmation_required", confirmationFailure);
        Assert.True(backfills.TryStart(
            first.DestinationId,
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
            "operator",
            confirmed: true,
            out var backfill,
            out var backfillFailure), backfillFailure);
        Assert.True(ExportRetentionFloor.IsRequired(
            segment,
            ExportRetentionFloor.ReadFloors(root)));
        Assert.Equal(AuditBackfillState.Active, backfill!.State);
    }

    [Fact]
    public async Task Disable_and_remove_scan_durable_backlog_before_first_health_pass()
    {
        var root = NewRoot("destination-cold-backlog");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        Directory.CreateDirectory(options.EvidenceDirectory);
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var openFailure);
        Assert.Null(openFailure);
        Assert.True(registry.TryAdd(
            Draft("primary", "https://siem.example/v1/logs", "secret"),
            confirmedSensitiveDuplication: false,
            DateTimeOffset.UtcNow,
            out var destination,
            out var addFailure), addFailure);

        var bootId = Guid.NewGuid();
        WriteSegment(
            options,
            bootId,
            [Record(
                bootId,
                1,
                destination!.DestinationId,
                "2026-08-14T10:00:00Z")]);

        var evidence = new ScriptEvidenceStoreProvider(options);
        var backfills = new AuditBackfillRegistry(root);
        await using var coordinator = new AuditExportCoordinator(
            options,
            registry,
            backfills,
            evidence,
            () => null,
            new AuditExportHealth());
        var operations = new AuditDestinationOperations(
            options,
            registry,
            backfills,
            coordinator,
            new AcceptingCredentialValidator());

        Assert.Equal(0, Assert.Single(coordinator.Statuses()).Delivery.PendingEventRecords);

        var disable = await operations.SetEnabledAsync(
            destination.DestinationId,
            enabled: false,
            CancellationToken.None);
        Assert.False(disable.Succeeded);
        Assert.Equal("pending_obligations_require_abandonment", disable.Failure);

        var remove = await operations.RemoveAsync(
            destination.DestinationId,
            CancellationToken.None);
        Assert.False(remove.Succeeded);
        Assert.Equal("pending_obligations_require_abandonment", remove.Failure);
        Assert.Equal(1, Assert.Single(coordinator.Statuses()).Delivery.PendingEventRecords);

        var abandonment = await operations.AbandonAsync(
            destination.DestinationId,
            "operator@example",
            "destination contract ended",
            remove: false,
            CancellationToken.None);
        Assert.True(abandonment.Succeeded, abandonment.Failure);
        Assert.False(Assert.Single(registry.Snapshot().Destinations).Enabled);

        var abandonmentPath = Assert.Single(Directory.GetFiles(
            root,
            "export-abandonment-*.json",
            SearchOption.TopDirectoryOnly));
        using var document = JsonDocument.Parse(File.ReadAllBytes(abandonmentPath));
        var undelivered = document.RootElement.GetProperty("undelivered");
        Assert.Equal("complete", undelivered.GetProperty("measurement_state").GetString());
        Assert.Equal(1, undelivered.GetProperty("event_records").GetInt64());
        var range = Assert.Single(
            undelivered.GetProperty("event_and_evidence_source_ranges").EnumerateArray());
        Assert.Equal(bootId.ToString("D"), range.GetProperty("supervisor_boot_id").GetString());
        Assert.Equal(0, range.GetProperty("first_undelivered_offset").GetInt64());
        Assert.True(range.GetProperty("observed_through_offset").GetInt64() > 0);
    }

    [Fact]
    public async Task Bounded_backfill_survives_restart_and_completes_without_becoming_live_delivery()
    {
        var root = NewRoot("destination-backfill-restart");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.SpoolDirectory);
        Directory.CreateDirectory(options.EvidenceDirectory);
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var openFailure);
        Assert.Null(openFailure);
        Assert.True(registry.TryAdd(
            Draft("primary", "https://backfill.example/v1/logs", "secret"),
            confirmedSensitiveDuplication: false,
            DateTimeOffset.UtcNow,
            out var destination,
            out var addFailure), addFailure);
        var bootId = Guid.NewGuid();
        WriteSegment(
            options,
            bootId,
            [
                Record(bootId, 1, [], "2026-08-14T09:00:00Z"),
                Record(bootId, 2, [], "2026-08-14T10:30:00Z"),
                Record(bootId, 3, [], "2026-08-14T12:00:00Z"),
            ]);

        var controller = new DestinationController(
            AuditDeliveryResult.Retryable("backfill_destination_unavailable"));
        var evidence = new ScriptEvidenceStoreProvider(options);
        var backfills = new AuditBackfillRegistry(root);
        Guid backfillId;
        await using (var firstCoordinator = new AuditExportCoordinator(
            options,
            registry,
            backfills,
            evidence,
            () => null,
            new AuditExportHealth(),
            destinationFactory: _ => controller.CreateDestination()))
        {
            await firstCoordinator.StartAsync(CancellationToken.None);
            var operations = new AuditDestinationOperations(
                options,
                registry,
                backfills,
                firstCoordinator,
                new AcceptingCredentialValidator());
            var started = await operations.StartBackfillAsync(
                destination!.DestinationId,
                DateTimeOffset.Parse("2026-08-14T10:00:00Z"),
                DateTimeOffset.Parse("2026-08-14T11:00:00Z"),
                "operator@example",
                confirmed: true,
                CancellationToken.None);
            Assert.True(started.Succeeded, started.Failure);
            backfillId = started.Backfill!.BackfillId;
            await WaitUntilAsync(() => firstCoordinator.Statuses()
                .Single()
                .Backfill?.Delivery.ConsecutiveFailures > 0);
            Assert.Equal(AuditBackfillState.Active, backfills.ForDestination(
                destination.DestinationId)!.State);
            Assert.Empty(controller.AcceptedRecords);
        }

        controller.Result = AuditDeliveryResult.Delivered;
        var reopenedRegistry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var reopenFailure);
        Assert.Null(reopenFailure);
        var reopenedBackfills = new AuditBackfillRegistry(root);
        await using (var secondCoordinator = new AuditExportCoordinator(
            options,
            reopenedRegistry,
            reopenedBackfills,
            evidence,
            () => null,
            new AuditExportHealth(),
            destinationFactory: _ => controller.CreateDestination()))
        {
            await secondCoordinator.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() =>
                reopenedBackfills.ForDestination(destination!.DestinationId)?.State ==
                    AuditBackfillState.Completed);
            Assert.Equal(backfillId, reopenedBackfills.ForDestination(
                destination!.DestinationId)!.BackfillId);
            var accepted = Assert.Single(controller.AcceptedRecords);
            Assert.Contains("2026-08-14T10:30:00Z", accepted, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-08-14T09:00:00Z", accepted, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-08-14T12:00:00Z", accepted, StringComparison.Ordinal);
        }

        var acceptedCount = controller.AcceptedRecords.Count;
        await using (var thirdCoordinator = new AuditExportCoordinator(
            options,
            reopenedRegistry,
            new AuditBackfillRegistry(root),
            evidence,
            () => null,
            new AuditExportHealth(),
            destinationFactory: _ => controller.CreateDestination()))
        {
            await thirdCoordinator.StartAsync(CancellationToken.None);
            await Task.Delay(250);
            Assert.Equal(acceptedCount, controller.AcceptedRecords.Count);
            Assert.Equal(
                AuditBackfillState.Completed,
                thirdCoordinator.Statuses().Single().Backfill!.State);
        }
    }

    [Fact]
    public async Task Unconfirmed_second_destination_is_rejected_before_credential_probe()
    {
        var root = NewRoot("destination-confirm-before-probe");
        var options = AuditOptions.Create(root);
        Directory.CreateDirectory(options.EvidenceDirectory);
        var registry = AuditDestinationRegistry.Open(
            root,
            AuditExportSettings.Disabled,
            out var openFailure);
        Assert.Null(openFailure);
        Assert.True(registry.TryAdd(
            Draft("primary", "https://primary.example/v1/logs", "first-secret"),
            confirmedSensitiveDuplication: false,
            DateTimeOffset.UtcNow,
            out _,
            out var addFailure), addFailure);
        var backfills = new AuditBackfillRegistry(root);
        await using var coordinator = new AuditExportCoordinator(
            options,
            registry,
            backfills,
            new ScriptEvidenceStoreProvider(options),
            () => null,
            new AuditExportHealth());
        var validator = new AcceptingCredentialValidator();
        var operations = new AuditDestinationOperations(
            options,
            registry,
            backfills,
            coordinator,
            validator);

        var result = await operations.AddAsync(
            Draft("secondary", "https://secondary.example/v1/logs", "second-secret"),
            confirmedSensitiveDuplication: false,
            CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("sensitive_duplication_confirmation_required", result.Failure);
        Assert.Equal(0, validator.Calls);
        Assert.Single(registry.Snapshot().Destinations);
    }

    [Fact]
    public async Task Credential_probe_is_harmless_and_distinguishes_auth_refusal()
    {
        var handler = new ProbeHandler(HttpStatusCode.Unauthorized);
        using var client = new HttpClient(handler);
        using var validator = new AuditDestinationCredentialValidator(client);
        var failure = await validator.ValidateAsync(
            Draft("primary", "https://siem.example/v1/logs", "secret-token"),
            CancellationToken.None);
        Assert.Equal("credential_refused", failure);
        Assert.Equal(HttpMethod.Options, handler.Method);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "secret-token"), handler.Authorization);
        Assert.Equal(0, handler.BodyBytes);

        handler.StatusCode = HttpStatusCode.MethodNotAllowed;
        Assert.Null(await validator.ValidateAsync(
            Draft("primary", "https://siem.example/v1/logs", "secret-token"),
            CancellationToken.None));
    }

    private AuditExportService Service(
        AuditOptions options,
        IAuditDestination destination,
        AuditExportCursorStore cursor,
        Guid destinationId,
        ScriptEvidenceStoreProvider evidence) =>
        new(
            options,
            destination,
            cursor,
            new AuditExportHealth(),
            evidence: evidence,
            gapStore: new AuditExportGapStore(
                options.RootDirectory,
                AuditExportGapStore.DestinationFileName(destinationId)),
            lease: new AuditExportLease(
                AuditExportLease.DestinationFileName(destinationId)),
            recordFilter: record => RequiredBy(record, destinationId),
            holdAllPermanentRefusals: true);

    private static bool RequiredBy(string record, Guid destinationId)
    {
        using var document = JsonDocument.Parse(record);
        return document.RootElement.GetProperty("required_destination_ids")
            .EnumerateArray()
            .Any(item => Guid.Parse(item.GetString()!) == destinationId);
    }

    private static AuditEventInput Input(string eventType) => new()
    {
        EventType = eventType,
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
            ProtectionMode = "local-only",
            HealthState = "healthy",
        },
    };

    private static AuditDestinationDraft Draft(
        string label,
        string endpoint,
        string credential) =>
        new(
            AuditDestinationKind.OtlpHttp,
            label,
            new Uri(endpoint),
            credential);

    private static string Record(
        Guid bootId,
        long sequence,
        Guid destinationId,
        string occurredUtc) =>
        JsonSerializer.Serialize(new
        {
            schema_version = AuditEventSerializer.DestinationObligationSchemaVersion,
            event_id = Guid.NewGuid(),
            event_type = "call.completed",
            occurred_utc = occurredUtc,
            sequence,
            producer = new { supervisor_boot_id = bootId },
            required_destination_ids = new[] { destinationId.ToString("D") },
        });

    private static string Record(
        Guid bootId,
        long sequence,
        IReadOnlyList<Guid> destinationIds,
        string occurredUtc) =>
        JsonSerializer.Serialize(new
        {
            schema_version = AuditEventSerializer.DestinationObligationSchemaVersion,
            event_id = Guid.NewGuid(),
            event_type = "call.completed",
            occurred_utc = occurredUtc,
            sequence,
            producer = new { supervisor_boot_id = bootId },
            required_destination_ids = destinationIds
                .Order()
                .Select(destinationId => destinationId.ToString("D"))
                .ToArray(),
        });

    private static void WriteSegment(
        AuditOptions options,
        Guid bootId,
        IReadOnlyList<string> records)
    {
        var path = Path.Combine(
            options.SpoolDirectory,
            AuditSpoolSegmentIdentity.Create(bootId, 0).FileName);
        File.WriteAllText(path, string.Concat(records.Select(record => record + "\n")));
    }

    private string NewRoot(string label)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            $"test-s3-{label}-{Guid.NewGuid():N}");
        _roots.Add(root);
        return SecureAuditStorage.PrepareRoot(root);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "The expected destination state did not appear.");
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private sealed class RecordingDestination : IAuditDestination
    {
        private readonly AuditDeliveryResult _result;

        internal RecordingDestination(AuditDeliveryResult? result = null)
        {
            _result = result ?? AuditDeliveryResult.Delivered;
        }

        internal List<string> Records { get; } = [];

        public string Describe() => "recording";

        public Task<AuditDeliveryResult> DeliverAsync(
            IReadOnlyList<string> records,
            CancellationToken cancellationToken)
        {
            Records.AddRange(records);
            return Task.FromResult(_result);
        }

        public void Dispose() { }
    }

    private sealed class AcceptingCredentialValidator : IAuditDestinationCredentialValidator
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<string?> ValidateAsync(
            AuditDestinationDraft draft,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class DestinationController
    {
        private readonly object _gate = new();
        private AuditDeliveryResult _result;
        private int _attempts;
        private int _creations;

        internal DestinationController(AuditDeliveryResult result) => _result = result;

        internal ConcurrentQueue<string> AcceptedRecords { get; } = new();

        internal int Attempts => Volatile.Read(ref _attempts);

        internal int Creations => Volatile.Read(ref _creations);

        internal AuditDeliveryResult Result
        {
            get
            {
                lock (_gate) return _result;
            }
            set
            {
                lock (_gate) _result = value;
            }
        }

        internal IAuditDestination CreateDestination()
        {
            Interlocked.Increment(ref _creations);
            return new ControlledDestination(this);
        }

        private sealed class ControlledDestination : IAuditDestination
        {
            private readonly DestinationController _owner;

            internal ControlledDestination(DestinationController owner) => _owner = owner;

            public string Describe() => "controlled";

            public Task<AuditDeliveryResult> DeliverAsync(
                IReadOnlyList<string> records,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _owner._attempts);
                var result = _owner.Result;
                if (result.Disposition == AuditDeliveryDisposition.Delivered)
                {
                    foreach (var record in records)
                        _owner.AcceptedRecords.Enqueue(record);
                }

                return Task.FromResult(result);
            }

            public void Dispose() { }
        }
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        internal ProbeHandler(HttpStatusCode statusCode)
        {
            StatusCode = statusCode;
        }

        internal HttpStatusCode StatusCode { get; set; }
        internal HttpMethod? Method { get; private set; }
        internal AuthenticationHeaderValue? Authorization { get; private set; }
        internal int BodyBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Authorization = request.Headers.Authorization;
            BodyBytes = request.Content is null
                ? 0
                : (await request.Content.ReadAsByteArrayAsync(cancellationToken)).Length;
            return new HttpResponseMessage(StatusCode);
        }
    }
}

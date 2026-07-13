using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class OutputStoreTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void Sealed_snapshot_has_repeatable_byte_chunks_and_bounded_literal_search()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using var store = CreateStore(() => now);
        var source = "AéB\r\nneedle\r\nAéB";
        var sealedArtifact = Seal(store, source);

        Assert.True(sealedArtifact.Success);
        Assert.NotNull(sealedArtifact.Handle);
        Assert.StartsWith("ptko_", sealedArtifact.Handle, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, sealedArtifact.Handle!);
        SecureAuditStorage.VerifyExternalProtectedDirectory(store.RootPathForTests);
        SecureAuditStorage.VerifyExternalProtectedFile(Assert.Single(Directory.GetFiles(store.RootPathForTests)));

        var first = store.Read(sealedArtifact.Handle!, offset: 3, maximumBytes: 3);
        var repeated = store.Read(sealedArtifact.Handle!, offset: 3, maximumBytes: 3);
        Assert.Equal("B\r\n", first.Text);
        Assert.Equal(6, first.NextOffset);
        Assert.Equal(first, repeated);

        var clippedBeforeMultibyteScalar = store.Read(sealedArtifact.Handle!, offset: 0, maximumBytes: 2);
        Assert.Equal(OutputArtifactState.Available, clippedBeforeMultibyteScalar.State);
        Assert.Equal("A", clippedBeforeMultibyteScalar.Text);
        Assert.Equal(1, clippedBeforeMultibyteScalar.BytesRead);
        Assert.Equal(1, clippedBeforeMultibyteScalar.NextOffset);

        var scalarDoesNotFit = store.Read(sealedArtifact.Handle!, offset: 1, maximumBytes: 1);
        Assert.Equal(OutputArtifactState.InsufficientBound, scalarDoesNotFit.State);
        Assert.Empty(scalarDoesNotFit.Text);
        Assert.Equal(0, scalarDoesNotFit.BytesRead);
        Assert.Equal(1, scalarDoesNotFit.NextOffset);

        var scalar = store.Read(sealedArtifact.Handle!, offset: 1, maximumBytes: 2);
        Assert.Equal("é", scalar.Text);
        Assert.Equal(2, scalar.BytesRead);
        Assert.Equal(3, scalar.NextOffset);

        var search = store.Search(sealedArtifact.Handle!, "needle", offset: 0, maximumBytes: 64);
        var searchRepeated = store.Search(sealedArtifact.Handle!, "needle", offset: 0, maximumBytes: 64);
        var match = Assert.Single(search.Matches);
        Assert.Equal(6, match.Offset);
        Assert.Contains("needle", match.Preview, StringComparison.Ordinal);
        Assert.Equal(search.State, searchRepeated.State);
        Assert.Equal(search.Offset, searchRepeated.Offset);
        Assert.Equal(search.NextOffset, searchRepeated.NextOffset);
        Assert.Equal(search.TotalBytes, searchRepeated.TotalBytes);
        Assert.Equal(search.BytesScanned, searchRepeated.BytesScanned);
        Assert.Equal(search.Matches, searchRepeated.Matches);

        Assert.Equal("B\r\n", store.Read(sealedArtifact.Handle!, 3, 3).Text);
        Assert.Equal(OutputArtifactState.InvalidOffset, store.Read(sealedArtifact.Handle!, 2, 4).State);
    }

    [Fact]
    public void Search_finds_a_utf8_literal_split_across_bounded_windows()
    {
        using var store = CreateStore(() => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        var sealedArtifact = Seal(store, "éxxneedle-tail");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Search(sealedArtifact.Handle!, "needle", offset: 0, maximumBytes: 5));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Search(sealedArtifact.Handle!, "need", offset: 0, maximumBytes: 3));

        var offset = 0L;
        var matches = new List<OutputSearchMatch>();
        var iterations = 0;
        while (offset < sealedArtifact.Bytes)
        {
            var result = store.Search(sealedArtifact.Handle!, "need", offset, maximumBytes: 5);
            var repeated = store.Search(sealedArtifact.Handle!, "need", offset, maximumBytes: 5);

            Assert.Equal(result.State, repeated.State);
            Assert.Equal(result.Offset, repeated.Offset);
            Assert.Equal(result.NextOffset, repeated.NextOffset);
            Assert.Equal(result.TotalBytes, repeated.TotalBytes);
            Assert.Equal(result.BytesScanned, repeated.BytesScanned);
            Assert.Equal(result.Matches, repeated.Matches);
            Assert.InRange(result.BytesScanned, 0, 5);
            Assert.True(result.NextOffset > offset, "Bounded search must make forward progress.");

            matches.AddRange(result.Matches);
            offset = result.NextOffset;
            Assert.True(++iterations < 10, "Bounded search did not reach the end of the artifact.");
        }

        var match = Assert.Single(matches);
        Assert.Equal(4, match.Offset);
        Assert.Contains("need", match.Preview, StringComparison.Ordinal);

        var emojiArtifact = Seal(store, "A😀tail", sessionAlias: "emoji");
        var beforeEmoji = store.Search(emojiArtifact.Handle!, "😀", offset: 0, maximumBytes: 4);
        Assert.Empty(beforeEmoji.Matches);
        Assert.Equal(1, beforeEmoji.NextOffset);
        var atEmoji = store.Search(
            emojiArtifact.Handle!,
            "😀",
            beforeEmoji.NextOffset,
            maximumBytes: 4);
        Assert.Equal(1, Assert.Single(atEmoji.Matches).Offset);
        Assert.Equal(5, atEmoji.NextOffset);
    }

    [Fact]
    public void Artifact_cap_never_publishes_a_partial_utf8_scalar()
    {
        using var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            maximumArtifactBytes: 2,
            maximumSessionBytes: 2,
            maximumAggregateBytes: 2);

        var artifact = Seal(store, "A😀");

        Assert.Equal(OutputArtifactState.Incomplete, artifact.State);
        Assert.Equal("artifact_cap_exceeded", artifact.DetailCode);
        Assert.Equal(1, artifact.Bytes);
        Assert.Equal("A", store.Read(artifact.Handle!, 0, 2).Text);
    }

    [Fact]
    public void Retention_enforces_caps_with_distinct_incomplete_expired_and_evicted_states()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using (var capped = CreateStore(
                   () => now,
                   maximumArtifactBytes: 8,
                   maximumSessionBytes: 8,
                   maximumAggregateBytes: 8,
                   ttl: TimeSpan.FromMinutes(1)))
        {
            var truncated = Seal(capped, "abcdefghijkl");
            Assert.True(truncated.Success);
            Assert.Equal(OutputArtifactState.Incomplete, truncated.State);
            Assert.Equal(8, truncated.Bytes);
            Assert.Equal("artifact_cap_exceeded", truncated.DetailCode);

            var read = capped.Read(truncated.Handle!, 0, 8);
            Assert.Equal("abcdefgh", read.Text);
            Assert.False(read.Complete);

            var expiredPath = Assert.Single(Directory.GetFiles(capped.RootPathForTests));

            now = now.AddMinutes(1).AddTicks(1);
            capped.RunRetentionForTests();
            var expired = capped.Status(truncated.Handle!);
            Assert.Equal(OutputArtifactState.Expired, expired.State);
            Assert.Equal("ttl_expired", expired.DetailCode);
            Assert.NotEqual(OutputArtifactState.NotFound, expired.State);
            Assert.False(File.Exists(expiredPath));

            var expiredRead = capped.Read(truncated.Handle!, 0, 8);
            Assert.Equal(OutputArtifactState.Expired, expiredRead.State);
            Assert.Empty(expiredRead.Text);
            Assert.Equal(0, expiredRead.TotalBytes);
            Assert.Equal(0, expiredRead.BytesRead);

            var expiredSearch = capped.Search(truncated.Handle!, "a", 0, 8);
            Assert.Equal(OutputArtifactState.Expired, expiredSearch.State);
            Assert.Empty(expiredSearch.Matches);
            Assert.Equal(0, expiredSearch.TotalBytes);
            Assert.Equal(0, expiredSearch.BytesScanned);
        }

        now = new DateTimeOffset(2026, 7, 13, 13, 0, 0, TimeSpan.Zero);
        using var evicting = CreateStore(
            () => now,
            maximumArtifactBytes: 8,
            maximumSessionBytes: 8,
            maximumAggregateBytes: 8);
        var first = Seal(evicting, "12345678", sessionAlias: "alpha");
        var firstPath = Assert.Single(Directory.GetFiles(evicting.RootPathForTests));
        var second = Seal(evicting, "abcdefgh", sessionAlias: "beta");

        var evicted = evicting.Status(first.Handle!);
        Assert.Equal(OutputArtifactState.Evicted, evicted.State);
        Assert.Equal("aggregate_capacity", evicted.DetailCode);
        Assert.Equal(OutputArtifactState.Available, evicting.Status(second.Handle!).State);
        Assert.False(File.Exists(firstPath));
        Assert.Single(Directory.GetFiles(evicting.RootPathForTests));

        var evictedRead = evicting.Read(first.Handle!, 0, 8);
        Assert.Equal(OutputArtifactState.Evicted, evictedRead.State);
        Assert.Empty(evictedRead.Text);
        Assert.Equal(0, evictedRead.TotalBytes);
        Assert.Equal(0, evictedRead.BytesRead);

        var evictedSearch = evicting.Search(first.Handle!, "1", 0, 8);
        Assert.Equal(OutputArtifactState.Evicted, evictedSearch.State);
        Assert.Empty(evictedSearch.Matches);
        Assert.Equal(0, evictedSearch.TotalBytes);
        Assert.Equal(0, evictedSearch.BytesScanned);
        Assert.Equal(OutputArtifactState.NotFound, evicting.Status("ptko_unknown").State);
    }

    [Fact]
    public void Concurrent_reservations_count_toward_session_and_aggregate_capacity()
    {
        using var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            maximumArtifactBytes: 8,
            maximumSessionBytes: 8,
            maximumAggregateBytes: 16);

        Assert.True(store.TryReserve("alpha", out var alpha, out var alphaFailure), alphaFailure);
        OutputCaptureReservation? beta = null;
        OutputCaptureReservation? gamma = null;
        try
        {
            Assert.False(store.TryReserve("alpha", out var sameSession, out var sessionFailure));
            Assert.Null(sameSession);
            Assert.Equal("capacity", sessionFailure);

            Assert.True(store.TryReserve("beta", out beta, out var betaFailure), betaFailure);
            Assert.False(store.TryReserve("gamma", out var overAggregate, out var aggregateFailure));
            Assert.Null(overAggregate);
            Assert.Equal("capacity", aggregateFailure);

            alpha!.Dispose();
            alpha = null;
            Assert.True(store.TryReserve("gamma", out gamma, out var gammaFailure), gammaFailure);
        }
        finally
        {
            alpha?.Dispose();
            beta?.Dispose();
            gamma?.Dispose();
        }

        Assert.Empty(Directory.GetFiles(store.RootPathForTests));
    }

    [Fact]
    public void Retained_artifact_count_bounds_zero_byte_snapshots_and_open_handles()
    {
        using var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            maximumRetainedArtifacts: 2);
        var first = Seal(store, string.Empty, sessionAlias: "alpha");
        var second = Seal(store, string.Empty, sessionAlias: "beta");

        Assert.Equal(2, Directory.GetFiles(store.RootPathForTests).Length);
        Assert.True(store.TryReserve("gamma", out var third, out var failure), failure);

        Assert.Equal(OutputArtifactState.Evicted, store.Status(first.Handle!).State);
        Assert.Equal("artifact_count_capacity", store.Status(first.Handle!).DetailCode);
        Assert.Equal(OutputArtifactState.Available, store.Status(second.Handle!).State);
        Assert.Single(Directory.GetFiles(store.RootPathForTests));
        third!.Dispose();
    }

    [Fact]
    public void Periodic_retention_expires_and_physically_deletes_without_an_api_trigger()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var nowTicks = now.Ticks;
        using var store = CreateStore(
            () => new DateTimeOffset(Interlocked.Read(ref nowTicks), TimeSpan.Zero),
            ttl: TimeSpan.FromMinutes(1),
            retentionInterval: TimeSpan.FromMilliseconds(10));
        var artifact = Seal(store, "periodic");
        var path = Assert.Single(Directory.GetFiles(store.RootPathForTests));

        Interlocked.Exchange(ref nowTicks, now.AddMinutes(1).AddTicks(1).Ticks);
        Assert.True(
            SpinWait.SpinUntil(() => !File.Exists(path), TimeSpan.FromSeconds(5)),
            "The periodic retention callback did not delete the expired artifact.");

        var expired = store.Status(artifact.Handle!);
        Assert.Equal(OutputArtifactState.Expired, expired.State);
        Assert.Equal("ttl_expired", expired.DetailCode);
    }

    [Fact]
    public void Failed_retention_deletion_stays_nondisclosing_and_charged_until_retry_succeeds()
    {
        var refuseDeletion = true;
        using var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            maximumArtifactBytes: 8,
            maximumSessionBytes: 8,
            maximumAggregateBytes: 8,
            artifactDeleteStartingForTests: _ =>
            {
                if (refuseDeletion) throw new IOException("injected deletion failure");
            });
        var artifact = Seal(store, "12345678", sessionAlias: "alpha");
        var path = Assert.Single(Directory.GetFiles(store.RootPathForTests));

        Assert.False(store.TryReserve("beta", out var blocked, out var failure));
        Assert.Null(blocked);
        Assert.Equal("capacity", failure);
        Assert.Equal(OutputArtifactState.Evicted, store.Status(artifact.Handle!).State);
        Assert.Empty(store.Read(artifact.Handle!, 0, 8).Text);
        Assert.True(File.Exists(path));

        refuseDeletion = false;
        store.RunRetentionForTests();
        Assert.False(File.Exists(path));
        Assert.True(store.TryReserve("beta", out var admitted, out var admittedFailure), admittedFailure);
        admitted!.Dispose();
    }

    [Fact]
    public void Retained_identity_preserves_snapshot_and_never_deletes_a_path_substitute()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using var store = CreateStore(
            () => now,
            ttl: TimeSpan.FromMinutes(1));
        var artifact = Seal(store, "original snapshot");
        var path = Assert.Single(Directory.GetFiles(store.RootPathForTests));
        var displaced = path + ".displaced";

        File.Move(path, displaced);
        using (var substitute = SecureAuditStorage.CreateExclusiveFile(path))
        {
            substitute.Write("substitute"u8);
            substitute.Flush(flushToDisk: true);
        }

        Assert.Equal(
            "original snapshot",
            store.Read(artifact.Handle!, 0, OutputStore.MaximumReadBytes).Text);

        now = now.AddMinutes(1).AddTicks(1);
        store.RunRetentionForTests();
        Assert.Equal(OutputArtifactState.Expired, store.Status(artifact.Handle!).State);
        Assert.Empty(store.Read(artifact.Handle!, 0, OutputStore.MaximumReadBytes).Text);
        Assert.Equal("substitute", File.ReadAllText(path));
        Assert.True(File.Exists(displaced));

        File.Delete(path);
        File.Move(displaced, path);
        store.RunRetentionForTests();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SealIncomplete_keeps_terminal_streams_labeled_and_forces_incomplete_state()
    {
        using var store = CreateStore(() => DateTimeOffset.UtcNow);
        Assert.True(store.TryReserve("default", out var reservation, out var failure), failure);
        using (reservation)
        {
            var result = reservation!.SealIncomplete(new OutputArtifactContent(
                "stdout",
                ["native diagnostic"],
                ["powershell error"],
                ["warning"],
                7,
                OutputProvenance.PowerShellObjects),
                "worker_died");
            Assert.True(result.Success);
            Assert.Equal(OutputArtifactState.Incomplete, result.State);

            var status = store.Status(result.Handle!);
            Assert.False(status.Complete);
            Assert.Equal("worker_died", status.DetailCode);
            var read = store.Read(result.Handle!, 0, OutputStore.MaximumReadBytes);
            Assert.Contains("stdout", read.Text, StringComparison.Ordinal);
            Assert.Contains("[exit] 7", read.Text, StringComparison.Ordinal);
            Assert.Contains("[stderr]", read.Text, StringComparison.Ordinal);
            Assert.Contains("native diagnostic", read.Text, StringComparison.Ordinal);
            Assert.Contains("[errors]", read.Text, StringComparison.Ordinal);
            Assert.Contains("[warnings]", read.Text, StringComparison.Ordinal);
        }
    }

    private OutputStore CreateStore(
        Func<DateTimeOffset> clock,
        long maximumArtifactBytes = 1024,
        long maximumSessionBytes = 2048,
        long maximumAggregateBytes = 4096,
        TimeSpan? ttl = null,
        TimeSpan? retentionInterval = null,
        Action<string>? artifactDeleteStartingForTests = null,
        int maximumRetainedArtifacts = 4096)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "output-tests",
            Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return new OutputStore(new OutputStoreOptions(
            root,
            ttl ?? TimeSpan.FromMinutes(15),
            retentionInterval ?? TimeSpan.FromHours(1),
            maximumArtifactBytes,
            maximumSessionBytes,
            maximumAggregateBytes,
            clock,
            artifactDeleteStartingForTests,
            maximumRetainedArtifacts));
    }

    private static OutputSealResult Seal(OutputStore store, string text, string sessionAlias = "default")
    {
        Assert.True(store.TryReserve(sessionAlias, out var reservation, out var failure), failure);
        using (reservation)
        {
            return reservation!.Seal(new OutputArtifactContent(
                text,
                [],
                [],
                [],
                null,
                OutputProvenance.PowerShellObjects));
        }
    }
}

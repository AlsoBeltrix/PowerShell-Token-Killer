using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditStartupConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ptk",
        $"ptk-audit-startup-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* Preserve the assertion failure that prevented cleanup. */ }
    }

    [Fact]
    public void Explicit_root_selects_local_only_legacy_administration()
    {
        using var startup = AuditStartupConfiguration.Load(_root);

        Assert.Equal(Path.GetFullPath(_root), startup.AuditOptions.RootDirectory);
        Assert.Equal(AuditProtectionMode.LocalOnly, startup.AuditOptions.ProtectionMode);
        Assert.Null(startup.AuditOptions.ExportConfigurationIdentity);
    }

    [Fact]
    public void Missing_root_selects_the_default_legacy_administration_root()
    {
        using var startup = AuditStartupConfiguration.Load(configuredAuditRoot: null);
        var expected = AuditOptions.CreateDefault();

        Assert.Equal(expected.RootDirectory, startup.AuditOptions.RootDirectory);
        Assert.Equal(AuditProtectionMode.LocalOnly, startup.AuditOptions.ProtectionMode);
    }

    [Fact]
    public void Permanent_block_options_are_derived_from_the_exact_legacy_checkpoint()
    {
        var local = AuditOptions.Create(_root);
        var writerOptions = AuditOptions.Create(
            _root,
            AuditProtectionMode.Anchored,
            new string('a', 64));
        var bootId = Guid.Parse("22345678-1234-4abc-8def-0123456789ab");
        var eventId = Guid.CreateVersion7();
        var blockedIdentity = new string('b', 64);
        using var store = AuditExportCheckpointStore.CreateForWriter(
            writerOptions,
            bootId);
        var blocked = new AuditExportBlockedRecord(
            AuditSpoolSegmentIdentity.Create(bootId, 0),
            byteOffset: 0,
            sequence: 1,
            eventId,
            AuditExportFailureClass.Data,
            "http.400",
            responseDigest: null,
            new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero),
            blockedIdentity);
        store.SaveForTests(new AuditExportCheckpoint(
            bootId,
            chainComplete: false,
            spool: null,
            byteOffset: 0,
            sequence: 0,
            acknowledgedEventId: null,
            blocked));

        var resolved = AuditStartupConfiguration.ResolvePermanentBlockOptions(
            local,
            bootId,
            eventId);

        Assert.Equal(AuditProtectionMode.Anchored, resolved.ProtectionMode);
        Assert.Equal(blockedIdentity, resolved.ExportConfigurationIdentity);
        Assert.Equal(local.RootDirectory, resolved.RootDirectory);
        Assert.Equal(local.AggregateBytes, resolved.AggregateBytes);
    }
}

using System.Diagnostics;
using PtkMcpServer.Audit;
using PtkContainmentTestFixture;

namespace PtkMcpServer.Tests;

public sealed class OutputRootLeaseTests : IDisposable
{
    private readonly List<string> _parents = [];

    [Fact]
    public void Owned_store_holds_an_exclusive_marker_and_normal_dispose_removes_root()
    {
        var parent = CreateParent();
        var ownership = OutputRootOwnership.CreateCurrent();
        var root = Path.Combine(parent, ownership.DirectoryName);
        var store = CreateStore(root, ownership);
        _ = Assert.Single(Directory.GetFiles(root));
        var siblingOwnership = OutputRootOwnership.CreateCurrent();
        using var sibling = CreateStore(
            Path.Combine(parent, siblingOwnership.DirectoryName),
            siblingOwnership);
        Assert.True(Directory.Exists(root));

        store.Dispose();

        Assert.False(Directory.Exists(root));
        Assert.True(Directory.Exists(sibling.RootPathForTests));
    }

    [Fact]
    public void Dispose_reclaims_recognized_residue_before_removing_ownership_marker()
    {
        var parent = CreateParent();
        var ownership = OutputRootOwnership.CreateCurrent();
        var root = Path.Combine(parent, ownership.DirectoryName);
        using var lease = OutputRootLease.Acquire(root, ownership);
        var artifactPath = Path.Combine(
            root,
            $"artifact-{Guid.NewGuid():N}.out");
        using (var artifact = SecureAuditStorage.CreateExclusiveFile(
                   artifactPath,
                   access: FileAccess.ReadWrite))
        {
            artifact.Write("retained residue"u8);
            artifact.Flush(flushToDisk: true);
        }

        lease.Dispose();

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Startup_reclaims_only_an_abandoned_valid_root()
    {
        var parent = CreateParent();
        var liveOwnership = OutputRootOwnership.CreateCurrent();
        var staleOwnership = OutputRootOwnership.CreateCurrent();
        var nextOwnership = OutputRootOwnership.CreateCurrent();
        var invalidOwnership = OutputRootOwnership.CreateCurrent();
        var liveRoot = Path.Combine(parent, liveOwnership.DirectoryName);
        var staleRoot = Path.Combine(parent, staleOwnership.DirectoryName);
        var nextRoot = Path.Combine(parent, nextOwnership.DirectoryName);
        var invalidRoot = Path.Combine(parent, invalidOwnership.DirectoryName);

        using var live = OutputRootLease.Acquire(
            liveRoot,
            liveOwnership);
        var stale = OutputRootLease.Acquire(
            staleRoot,
            staleOwnership);
        stale.AbandonForTests();
        SecureAuditStorage.PrepareRoot(invalidRoot);
        using (var invalidMarker = SecureAuditStorage.CreateExclusiveFile(
                   Path.Combine(invalidRoot, "owner.v1.json"),
                   access: FileAccess.ReadWrite))
        {
            invalidMarker.Write("{}"u8);
            invalidMarker.Flush(flushToDisk: true);
        }

        using var next = OutputRootLease.Acquire(
            nextRoot,
            nextOwnership);

        Assert.True(Directory.Exists(liveRoot));
        Assert.False(Directory.Exists(staleRoot));
        Assert.True(Directory.Exists(invalidRoot));
        Assert.True(Directory.Exists(nextRoot));
    }

    [Fact]
    public async Task Hard_owner_death_is_reclaimed_without_touching_a_live_sibling()
    {
        var parent = CreateParent();
        using var owner = StartOwnerProcess(parent);
        try
        {
            var deadRoot = await owner.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(string.IsNullOrWhiteSpace(deadRoot));
            Assert.True(Directory.Exists(deadRoot));

            var liveOwnership = OutputRootOwnership.CreateCurrent();
            var liveRoot = Path.Combine(parent, liveOwnership.DirectoryName);
            using var live = OutputRootLease.Acquire(liveRoot, liveOwnership);
            Assert.True(Directory.Exists(deadRoot));

            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var reclaimerOwnership = OutputRootOwnership.CreateCurrent();
            var reclaimerRoot = Path.Combine(
                parent,
                reclaimerOwnership.DirectoryName);
            using var reclaimer = OutputRootLease.Acquire(
                reclaimerRoot,
                reclaimerOwnership);

            Assert.False(Directory.Exists(deadRoot));
            Assert.True(Directory.Exists(liveRoot));
        }
        finally
        {
            if (!owner.HasExited)
            {
                owner.Kill(entireProcessTree: true);
                await owner.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    public void Dispose()
    {
        foreach (var parent in _parents)
        {
            try { Directory.Delete(parent, recursive: true); }
            catch { }
        }
    }

    private string CreateParent()
    {
        var parent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "output-root-lease-tests",
            Guid.NewGuid().ToString("N"));
        _parents.Add(parent);
        return parent;
    }

    private static OutputStore CreateStore(
        string root,
        OutputRootOwnership ownership) =>
        new(new OutputStoreOptions(
            root,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: 1024,
            MaximumSessionBytes: 2048,
            MaximumAggregateBytes: 4096,
            RootOwnership: ownership));

    private static Process StartOwnerProcess(string parent)
    {
        var fixtureAssembly = typeof(FixtureAssemblyMarker).Assembly.Location;
        var fixtureDirectory = Path.GetDirectoryName(fixtureAssembly) ??
            throw new InvalidOperationException(
                "The containment fixture directory is unavailable.");
        var appHost = Path.Combine(
            fixtureDirectory,
            OperatingSystem.IsWindows()
                ? "PtkContainmentTestFixture.exe"
                : "PtkContainmentTestFixture");
        var start = new ProcessStartInfo
        {
            FileName = File.Exists(appHost) ? appHost : "dotnet",
            WorkingDirectory = fixtureDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!File.Exists(appHost))
            start.ArgumentList.Add(fixtureAssembly);
        start.ArgumentList.Add("output-root-owner");
        start.ArgumentList.Add(parent);
        return Process.Start(start) ??
            throw new InvalidOperationException(
                "The output-root owner fixture did not start.");
    }
}

using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditSpoolQuotaLeaseTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Preserve the test failure that prevented ordinary cleanup.
            }
        }
    }

    [Fact]
    public void Writer_creation_publishes_the_exact_protected_one_byte_control()
    {
        var root = NewProtectedRoot();
        var path = ControlPath(root);

        using (AuditSpoolQuotaLease.CreateControlAndAcquire(root))
        {
            Assert.True(File.Exists(path));
        }

        Assert.Equal(new byte[] { 0x50 }, File.ReadAllBytes(path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(SecureAuditStorage.OwnerFileMode, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Concurrent_writer_creations_converge_on_one_control_and_nothing_else()
    {
        var root = NewProtectedRoot();
        var path = ControlPath(root);
        using var start = new ManualResetEventSlim(initialState: false);
        var creators = Enumerable.Range(0, 8)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    using var lease = AuditSpoolQuotaLease.CreateControlAndAcquire(root);
                    lease.VerifyOwnership();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        start.Set();
        await Task.WhenAll(creators).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(new byte[] { 0x50 }, File.ReadAllBytes(path));
        // The spool root tolerates no entry it does not know, so creation
        // may never leave anything beside the control, even transiently
        // (CI run 40386199045 failed a concurrent factory on a pending
        // temporary name).
        Assert.Equal([path], Directory.EnumerateFileSystemEntries(root).ToArray());
    }

    [Fact]
    public async Task Empty_control_counts_as_creation_in_progress_until_a_writer_completes_it()
    {
        var root = NewProtectedRoot();
        var path = WriteControl(root, []);

        // A non-creating probe sees an owner, not a malformed control, and
        // a blocking acquirer waits instead of failing closed.
        Assert.False(AuditSpoolQuotaLease.TryAcquireExisting(root, out var probe));
        Assert.Null(probe);
        Assert.Empty(File.ReadAllBytes(path));
        var waiter = Task.Run(() => AuditSpoolQuotaLease.AcquireExisting(root));
        try
        {
            await Task.Delay(150);
            Assert.False(waiter.IsCompleted);

            // The next creator completes the control under its own lock;
            // the bytes are read once no lease holds that lock.
            using (var creator = AuditSpoolQuotaLease.CreateControlAndAcquire(root))
            {
                creator.VerifyOwnership();
            }

            using (var acquired = await waiter.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                acquired.VerifyOwnership();
            }
        }
        finally
        {
            try
            {
                using var cleanup = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Preserve the primary assertion or acquisition failure.
            }
        }

        Assert.Equal(new byte[] { 0x50 }, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.EnumerateFileSystemEntries(root).ToArray());
    }

    [Fact]
    public void Empty_control_with_a_second_name_is_rejected_before_it_is_written()
    {
        var root = NewProtectedRoot();
        var path = WriteControl(root, []);
        var alias = path + ".alias";
        CreateHardLink(path, alias);

        // An empty control is completed only once the retained handle is
        // proved to name the one protected control: an aliased one is
        // rejected without ever being written, through either name.
        Assert.Throws<IOException>(() => AuditSpoolQuotaLease.CreateControlAndAcquire(root));
        Assert.Empty(File.ReadAllBytes(path));
        Assert.Empty(File.ReadAllBytes(alias));
    }

    [Fact]
    public void Existing_probe_does_not_create_a_missing_control()
    {
        var root = NewProtectedRoot();
        var path = ControlPath(root);

        Assert.Throws<IOException>(() =>
            AuditSpoolQuotaLease.TryAcquireExisting(root, out _));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void Existing_probe_does_not_create_a_missing_root()
    {
        var root = NewAbsentRoot();

        Assert.Throws<IOException>(() =>
            AuditSpoolQuotaLease.TryAcquireExisting(root, out _));

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Existing_probe_returns_false_only_while_the_control_is_owned()
    {
        var root = NewProtectedRoot();
        var held = AuditSpoolQuotaLease.CreateControlAndAcquire(root);
        try
        {
            Assert.False(AuditSpoolQuotaLease.TryAcquireExisting(root, out var contender));
            Assert.Null(contender);
        }
        finally
        {
            held.Dispose();
        }

        Assert.True(AuditSpoolQuotaLease.TryAcquireExisting(root, out var acquired));
        using (acquired)
            acquired.VerifyOwnership();
    }

    [Fact]
    public async Task Existing_blocking_acquisition_waits_for_the_current_owner()
    {
        var root = NewProtectedRoot();
        var held = AuditSpoolQuotaLease.CreateControlAndAcquire(root);
        var contender = Task.Run(() => AuditSpoolQuotaLease.AcquireExisting(root));
        try
        {
            await Task.Delay(150);
            Assert.False(contender.IsCompleted);
            held.Dispose();
            using var acquired = await contender.WaitAsync(TimeSpan.FromSeconds(5));
            acquired.VerifyOwnership();
        }
        finally
        {
            held.Dispose();
            try
            {
                using var cleanup = await contender.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Preserve the primary assertion or acquisition failure.
            }
        }
    }

    [Theory]
    [MemberData(nameof(MalformedControls))]
    public void Existing_probe_rejects_a_malformed_control(byte[] bytes)
    {
        var root = NewProtectedRoot();
        WriteControl(root, bytes);

        Assert.Throws<IOException>(() =>
            AuditSpoolQuotaLease.TryAcquireExisting(root, out _));
    }

    [Fact]
    public void Existing_probe_rejects_broadened_control_permissions_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = NewProtectedRoot();
        var path = WriteControl(root, [0x50]);
        File.SetUnixFileMode(
            path,
            SecureAuditStorage.OwnerFileMode | UnixFileMode.GroupRead);

        Assert.Throws<IOException>(() =>
            AuditSpoolQuotaLease.TryAcquireExisting(root, out _));
    }

    [Fact]
    public void Retained_lease_detects_path_replacement_or_windows_denies_it()
    {
        var root = NewProtectedRoot();
        var path = ControlPath(root);
        var displaced = path + ".displaced";
        var lease = AuditSpoolQuotaLease.CreateControlAndAcquire(root);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                Assert.ThrowsAny<IOException>(() => File.Move(path, displaced));
            }
            finally
            {
                lease.Dispose();
            }
            return;
        }

        File.Move(path, displaced);
        WriteControl(root, [0x50]);
        Assert.Throws<IOException>(lease.Dispose);
    }

    // An empty control is not malformed: it is still being created (see
    // Empty_control_counts_as_creation_in_progress_until_a_writer_completes_it).
    public static TheoryData<byte[]> MalformedControls => new()
    {
        new byte[] { 0x51 },
        new byte[] { 0x50, 0x00 },
    };

    private string NewProtectedRoot()
    {
        var root = NewAbsentRoot();
        return SecureAuditStorage.PrepareRoot(root);
    }

    private string NewAbsentRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-audit-quota-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static string WriteControl(string root, ReadOnlySpan<byte> bytes)
    {
        var path = ControlPath(root);
        using var stream = SecureAuditStorage.CreateExclusiveFile(path);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static string ControlPath(string root) =>
        Path.Combine(root, AuditSpoolQuotaLease.ControlFileName);

    private static void CreateHardLink(string existing, string link)
    {
        var start = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/H", link, existing } }
            : new System.Diagnostics.ProcessStartInfo("ln") { ArgumentList = { existing, link } };
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("The hard link helper did not start.");
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(link))
            throw new InvalidOperationException("The hard link helper failed: " + process.StandardError.ReadToEnd());
    }
}

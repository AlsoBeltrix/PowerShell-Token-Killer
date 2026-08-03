namespace PtkMcpServer.Tests;

/// <summary>
/// RTK is a required dependency: the server refuses to start without it. The
/// startup gate must therefore apply the same usability criteria the runtime
/// uses to pin and hash the binary. A weaker check — mere file existence — let
/// the server start on a path the runtime then failed to capture, and native
/// commands ran unfiltered: exactly the silent degradation the gate exists to
/// prevent.
/// </summary>
public sealed class RtkDependencyTests : IDisposable
{
    private readonly string? _savedPath =
        Environment.GetEnvironmentVariable(RtkDependency.EnvironmentVariable);
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            RtkDependency.EnvironmentVariable,
            _savedPath);
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* Preserve the assertion failure that prevented cleanup. */ }
        }
    }

    private string NewDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("ptk-rtk-dependency-");
        _roots.Add(directory.FullName);
        return directory.FullName;
    }

    [Fact]
    public void A_configured_path_that_does_not_exist_is_unresolvable()
    {
        Environment.SetEnvironmentVariable(
            RtkDependency.EnvironmentVariable,
            Path.Combine(NewDirectory(), "absent-rtk"));

        Assert.Null(RtkDependency.ResolveExecutablePath());
    }

    /// <summary>
    /// The regression: a path that passes File.Exists but cannot be captured
    /// as an executable identity is not a usable RTK. An oversized file is the
    /// clearest case — TryCapture bounds the hash at 128 MiB and refuses
    /// beyond it, while File.Exists happily returns true. Reverting the gate to
    /// File.Exists makes this FAIL: the server would start and then degrade,
    /// running native commands unfiltered.
    /// </summary>
    [Fact]
    public void A_configured_file_the_runtime_cannot_capture_is_unresolvable()
    {
        var path = Path.Combine(NewDirectory(), "oversized-rtk");
        // One byte past the capture bound. Sparse so the test stays fast.
        using (var stream = new FileStream(path, FileMode.CreateNew))
            stream.SetLength(128L * 1024 * 1024 + 1);

        Environment.SetEnvironmentVariable(RtkDependency.EnvironmentVariable, path);

        Assert.True(File.Exists(path));
        Assert.Null(RtkExecutableIdentity.TryCapture(path));
        Assert.Null(RtkDependency.ResolveExecutablePath());
    }

    [Fact]
    public void A_configured_directory_is_not_a_usable_executable()
    {
        var directory = NewDirectory();
        Environment.SetEnvironmentVariable(
            RtkDependency.EnvironmentVariable,
            directory);

        Assert.True(Directory.Exists(directory));
        Assert.Null(RtkDependency.ResolveExecutablePath());
    }

    [Fact]
    public void A_capturable_configured_file_resolves_to_its_full_path()
    {
        var path = Path.Combine(NewDirectory(), "rtk-fixture");
        File.WriteAllText(path, "fixture");
        Environment.SetEnvironmentVariable(RtkDependency.EnvironmentVariable, path);

        var resolved = RtkDependency.ResolveExecutablePath();

        Assert.NotNull(resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public void The_unavailable_message_names_both_resolution_routes()
    {
        var message = RtkDependency.UnavailableMessage();

        Assert.Contains(RtkDependency.EnvironmentVariable, message, StringComparison.Ordinal);
        Assert.Contains("PATH", message, StringComparison.Ordinal);
        Assert.Contains("rtk", message, StringComparison.OrdinalIgnoreCase);
    }
}

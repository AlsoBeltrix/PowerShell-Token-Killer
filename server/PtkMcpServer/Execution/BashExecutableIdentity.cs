namespace PtkMcpServer;

/// <summary>An operator-startup-pinned Bash executable. The absolute final
/// path is used for process creation; the digest makes later replacement a
/// typed not-started outcome instead of silently validating with a new file.</summary>
internal sealed record BashExecutableIdentity
{
    private readonly ExecutableFileIdentity _fileIdentity;

    private BashExecutableIdentity(ExecutableFileIdentity fileIdentity)
    {
        _fileIdentity = fileIdentity;
    }

    internal string ExecutablePath => _fileIdentity.ExecutablePath;
    internal string BinaryDigest => _fileIdentity.BinaryDigest;
    internal string AuditIdentityCode => $"bash_sha256_{BinaryDigest}";

    internal static BashExecutableIdentity? TryCapture(string? path)
    {
        var identity = ExecutableFileIdentity.TryCapture(path);
        return identity is null ? null : new BashExecutableIdentity(identity);
    }

    internal bool MatchesCurrentFile() => _fileIdentity.MatchesCurrentFile();
}

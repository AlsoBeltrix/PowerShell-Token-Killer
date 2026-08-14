using System.Text;

namespace PtkMcpServer.Audit;

internal sealed record AuditCallAttribution
{
    internal static AuditCallAttribution NotSupplied { get; } = new()
    {
        AgentUnavailableReason = "not_supplied_by_client",
        ModelUnavailableReason = "not_supplied_by_client",
        Strength = "transport_only",
    };

    public string? AgentName { get; init; }
    public string? AgentUnavailableReason { get; init; }
    public string? ModelProvider { get; init; }
    public string? ModelName { get; init; }
    public string? ModelUnavailableReason { get; init; }
    public string? Source { get; init; }
    public required string Strength { get; init; }
}

internal sealed record AuditClientCallContext
{
    internal static AuditClientCallContext NotSupplied { get; } = new()
    {
        TaskUnavailableReason = "not_supplied_by_client",
        RunUnavailableReason = "not_supplied_by_client",
        Strength = "transport_only",
    };

    public string? TaskId { get; init; }
    public string? TaskName { get; init; }
    public long? McpTaskTtlMs { get; init; }
    public string? TaskUnavailableReason { get; init; }
    public string? RunId { get; init; }
    public string? RunUnavailableReason { get; init; }
    public string? Source { get; init; }
    public required string Strength { get; init; }
}

internal sealed record AuditExecutionContext
{
    internal static AuditExecutionContext NotSupplied { get; } = new()
    {
        RequestedCwdUnavailableReason = "not_supplied_by_client",
        EffectiveCwdUnavailableReason = "not_dispatched",
        RepositoryUnavailableReason = "not_dispatched",
    };

    public string? RequestedCwd { get; init; }
    public string? RequestedCwdUnavailableReason { get; init; }
    public string? EffectiveCwd { get; init; }
    public string? EffectiveCwdUnavailableReason { get; init; }
    public string? RepositoryRoot { get; init; }
    public string? RepositoryRelativePath { get; init; }
    public string? RepositoryUnavailableReason { get; init; }
}

internal static class AuditExecutionContextCapture
{
    private const int MaximumPathUtf8Bytes = 4_096;
    private const int MaximumAncestors = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static AuditExecutionContext Capture(
        string? requestedCwd,
        string? effectiveCwd)
    {
        var requested = NormalizeSubmittedPath(requestedCwd);
        var effective = NormalizePath(effectiveCwd);
        var requestedReason = requested is null
            ? "not_supplied_by_client"
            : null;
        if (effective is null)
        {
            return new AuditExecutionContext
            {
                RequestedCwd = requested,
                RequestedCwdUnavailableReason = requestedReason,
                EffectiveCwdUnavailableReason = "not_available_at_dispatch",
                RepositoryUnavailableReason = "effective_cwd_unavailable",
            };
        }

        var repositoryRoot = FindRepositoryRoot(effective);
        var relativePath = repositoryRoot is null
            ? null
            : Path.GetRelativePath(repositoryRoot, effective);
        if (relativePath is not null && !IsBounded(relativePath))
        {
            repositoryRoot = null;
            relativePath = null;
        }

        return new AuditExecutionContext
        {
            RequestedCwd = requested,
            RequestedCwdUnavailableReason = requestedReason,
            EffectiveCwd = effective,
            RepositoryRoot = repositoryRoot,
            RepositoryRelativePath = relativePath,
            RepositoryUnavailableReason = repositoryRoot is null
                ? "repository_not_detected"
                : null,
        };
    }

    private static string? FindRepositoryRoot(string effectiveCwd)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(effectiveCwd);
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            return null;
        }

        for (var depth = 0; current is not null && depth < MaximumAncestors; depth++)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return IsBounded(current.FullName) ? current.FullName : null;
            current = current.Parent;
        }

        return null;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsBounded(value))
            return null;
        try
        {
            var fullPath = Path.GetFullPath(value);
            return IsBounded(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (IsPathException(exception))
        {
            return null;
        }
    }

    private static string? NormalizeSubmittedPath(string? value) =>
        string.IsNullOrWhiteSpace(value) || !IsBounded(value)
            ? null
            : value;

    private static bool IsBounded(string value)
    {
        try
        {
            return StrictUtf8.GetByteCount(value) <= MaximumPathUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsPathException(Exception exception) =>
        exception is ArgumentException or NotSupportedException or PathTooLongException;
}

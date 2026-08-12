using PtkSiemReceiver.Configuration;
using PtkSiemReceiver.Ingest;
using PtkSiemReceiver.Storage;

const string barrierDirectoryEnvironment = "PTK_SIEM_TEST_COMMIT_BARRIER";
const string ackBeforeCommitEnvironment = "PTK_SIEM_TEST_ACK_BEFORE_COMMIT";

var configurationPath = Environment.GetEnvironmentVariable("PTK_SIEM_CONFIG");
if (string.IsNullOrWhiteSpace(configurationPath))
{
    Console.Error.WriteLine("siem_receiver_test_host_invalid: config_env");
    return 1;
}

try
{
    var options = SiemReceiverConfigurationLoader.Load(configurationPath);
    var barrierDirectory = Environment.GetEnvironmentVariable(barrierDirectoryEnvironment);
    using var barrier = string.IsNullOrWhiteSpace(barrierDirectory)
        ? null
        : new FileCommitBarrier(barrierDirectory);
    var acknowledgeBeforeCommit = string.Equals(
        Environment.GetEnvironmentVariable(ackBeforeCommitEnvironment),
        "1",
        StringComparison.Ordinal);
    if (acknowledgeBeforeCommit && barrier is null)
        throw new InvalidOperationException("The ack-before-commit double requires a commit barrier.");

    await using var application = ReceiverApplication.Build(
        options,
        args,
        storageFaultInjector: barrier,
        committerDecoratorForTests: acknowledgeBeforeCommit
            ? committer => new AckBeforeCommitCommitter(committer, barrier!)
            : null);
    await application.RunAsync();
    return 0;
}
catch (SiemReceiverConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (SiemReceiverStartupException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"siem_receiver_test_host_invalid: {exception.GetType().Name}");
    return 1;
}

internal sealed class FileCommitBarrier : ISqliteIngestFaultInjector, IDisposable
{
    private readonly string _enteredPath;
    private readonly string _releasePath;

    internal FileCommitBarrier(string directoryPath)
    {
        if (!Path.IsPathFullyQualified(directoryPath) || !Directory.Exists(directoryPath))
            throw new InvalidOperationException("The commit-barrier directory is invalid.");
        _enteredPath = Path.Combine(directoryPath, "entered");
        _releasePath = Path.Combine(directoryPath, "release");
    }

    internal ManualResetEventSlim Entered { get; } = new(false);

    public void BeforeCommit(SqliteIngestWriteKind writeKind)
    {
        if (writeKind != SqliteIngestWriteKind.Event) return;

        using (var marker = new FileStream(
                   _enteredPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.ReadWrite,
                   bufferSize: 1,
                   FileOptions.WriteThrough))
        {
            marker.WriteByte(1);
            marker.Flush(flushToDisk: true);
        }
        Entered.Set();
        while (!File.Exists(_releasePath)) Thread.Sleep(10);
    }

    public void Dispose() => Entered.Dispose();
}

internal sealed class AckBeforeCommitCommitter(
    IIngestCommitter inner,
    FileCommitBarrier barrier) : IIngestCommitter
{
    public Task<IngestCommitResult> CommitAsync(
        ValidatedOtlpRecord record,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken)
    {
        var actualCommit = Task.Run(
            () => inner.CommitAsync(record, receipt, CancellationToken.None),
            CancellationToken.None);
        _ = actualCommit.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        if (!barrier.Entered.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The ack-before-commit double did not reach its barrier.");
        return Task.FromResult(IngestCommitResult.Accepted());
    }

    public Task<IngestCommitResult> QuarantineAsync(
        RejectedOtlpAttempt attempt,
        IngestReceiptContext receipt,
        CancellationToken cancellationToken) =>
        inner.QuarantineAsync(attempt, receipt, cancellationToken);
}

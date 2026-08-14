using System.Runtime.ExceptionServices;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Audit;

/// <summary>
/// One supervisor generation's audit-owned runtime resources. The journal is
/// available before the optional exporter starts; the gate starts export only
/// after server.started is durable and stops it before server.stopped so the
/// lifecycle terminal remains the final record written by this process.
/// R2 restoration carries the local-only path; the anchored/export path
/// (AuditExportLoop, checkpoint store, OTLP transport) returns in R3 —
/// see .agents/plans/audit-restoration.md. StartExporter/StopExporterAsync
/// stay as the gate-facing seam and are no-ops until R3 supplies a loop.
/// </summary>
internal sealed class AuditRuntimeResources : IDisposable
{
    private readonly bool _ownsJournal;
    private int _disposed;

    internal AuditRuntimeResources(
        AuditJournal journal,
        bool ownsJournal = true)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Journal = journal;
        _ownsJournal = ownsJournal;
    }

    internal AuditJournal Journal { get; }

    internal static AuditRuntimeResources OpenLocal(
        AuditOptions options,
        AuditHealth health,
        string producerVersion,
        ScriptEvidenceStoreProvider evidence,
        AuditDestinationRegistry? destinations = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        if (options.ProtectionMode != AuditProtectionMode.LocalOnly)
        {
            throw new ArgumentException(
                "Local runtime resources require local-only audit options.",
                nameof(options));
        }

        AuditJournal? journal = null;
        try
        {
            // The complete retained topology was reconciled before this writer
            // can run its constructor retention. A failed proof never reaches
            // this point, so capacity recovery cannot turn uncertainty into
            // deletion.
            journal = AuditJournalFactory.OpenReconciledLocal(
            options,
            health,
            producerVersion,
            evidence,
            destinations is null ? null : destinations.EnabledDestinationIds);
            var resources = new AuditRuntimeResources(journal);
            journal = null;
            return resources;
        }
        finally
        {
            journal?.Dispose();
        }
    }

    internal void StartExporter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    internal Task StopExporterAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_ownsJournal) return;
        Journal.Dispose();
    }
}

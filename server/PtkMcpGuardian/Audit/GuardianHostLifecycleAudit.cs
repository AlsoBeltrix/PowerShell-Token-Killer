using PtkMcpGuardian.Standalone;

namespace PtkMcpServer.Audit;

/// <summary>
/// Translates supervisor-owned host lifecycle facts into bounded automatic
/// audit records. The journal captures the immutable v3 host snapshot at the
/// append edge; an unavailable journal closes admission without undoing a host
/// safety transition that already happened.
/// </summary>
internal sealed class GuardianHostLifecycleAudit(AuditRuntimeGate runtime) :
    IGuardianHostLifecycleAudit
{
    private readonly AuditRuntimeGate _runtime = runtime ??
        throw new ArgumentNullException(nameof(runtime));

    public void RecordStarting() =>
        Record(
            "host.starting",
            outcomeState: "starting",
            detailCode: "host_starting",
            warmStateLost: null);

    public void RecordReady(bool recovered) =>
        Record(
            recovered ? "host.recovered" : "host.ready",
            outcomeState: "completed",
            detailCode: recovered ? "host_recovered" : "host_ready",
            warmStateLost: recovered);

    private void Record(
        string eventType,
        string outcomeState,
        string detailCode,
        bool? warmStateLost)
    {
        _ = _runtime.TryAppendAutomaticTransition(CreateEvent(
            eventType,
            outcomeState,
            detailCode,
            warmStateLost));
    }

    private AuditEventInput CreateEvent(
        string eventType,
        string outcomeState,
        string detailCode,
        bool? warmStateLost)
    {
        var health = _runtime.Health.Snapshot();
        var unhealthy = health.State is
            AuditHealthState.Degraded or AuditHealthState.Unavailable;
        return new AuditEventInput
        {
            EventType = eventType,
            Session = new AuditSession(),
            Actor = new AuditActor { AttributionStrength = "unknown" },
            Correlation = new AuditCorrelation(),
            Request = new AuditRequest(),
            Routing = new AuditRouting(),
            Outcome = new AuditOutcome
            {
                State = outcomeState,
                DetailCode = detailCode,
                WarmStateLost = warmStateLost,
                TerminationCertainty = "not_applicable",
            },
            Coverage = new AuditCoverage
            {
                PtkRequest = false,
                RootProcessObserved = "not_applicable",
                DescendantsObserved = "not_applicable",
                RemoteEffectObserved = "not_applicable",
            },
            Audit = new AuditEventHealth
            {
                ProtectionMode = health.ProtectionMode == AuditProtectionMode.LocalOnly
                    ? "local-only"
                    : "anchored",
                ExportConfigurationIdentity = health.ExportConfigurationIdentity,
                HealthState = unhealthy ? "degraded" : "healthy",
                FailureClass = unhealthy ? health.FailureClass : null,
                DegradedSinceUtc = unhealthy ? health.DegradedSinceUtc : null,
            },
        };
    }
}

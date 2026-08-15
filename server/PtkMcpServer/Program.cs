using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PtkMcpServer;
using PtkMcpServer.Audit;
using PtkMcpServer.Audit.Export;
using PtkMcpServer.Sessions;
using PtkMcpServer.Worker;

// Worker classification is the first executable action. An internal worker
// attempt must never enter supervisor host, audit, output, or MCP startup.
if (WorkerProcessEntry.IsWorkerInvocation(args))
{
    Environment.ExitCode = await WorkerProcessEntry.RunAsync(args).ConfigureAwait(false);
    return;
}

// RTK is a required dependency: PTK compresses PowerShell objects itself and
// routes everything else to RTK. Refuse to start rather than come up as a
// half-working server whose native output is silently unfiltered. stderr,
// never stdout — stdout is the JSON-RPC transport.
if (RtkDependency.ResolveExecutablePath() is null)
{
    await Console.Error.WriteLineAsync(RtkDependency.UnavailableMessage())
        .ConfigureAwait(false);
    Environment.ExitCode = 78; // EX_CONFIG
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the JSON-RPC transport; every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var callTimeout = DefaultSessionRuntimeFactory.ReadCallTimeout();
var maxCallTimeout = DefaultSessionRuntimeFactory.ReadMaxCallTimeout();

// Audit is mandatory and startup-frozen: base-level, non-bypassable local
// logging (audit-restoration R2). Local write inability is the only
// execution gate; export (below, R3) is additive and never gates.
using var auditStartup = AuditStartupConfiguration.LoadFromEnvironment();
using var outputRequestProtector = new AuditOutputRequestProtector();
var producerVersion =
    typeof(RunspaceHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";
var exportSettings = AuditExportSettings.Load(
    auditStartup.AuditOptions.RootDirectory,
    out var exportConfigurationFailure);
var destinationRegistry = AuditDestinationRegistry.Open(
    auditStartup.AuditOptions.RootDirectory,
    exportSettings,
    out var destinationConfigurationFailure);
if (exportConfigurationFailure is not null || destinationConfigurationFailure is not null)
{
    await Console.Error.WriteLineAsync(
        $"[ptk audit] export configuration ignored " +
        $"({destinationConfigurationFailure ?? exportConfigurationFailure}); " +
        "PTK continues journaling locally.").ConfigureAwait(false);
}
builder.Services.AddSingleton(auditStartup.AuditOptions);
builder.Services.AddSingleton(destinationRegistry);
builder.Services.AddSingleton(
    sp => new AuditHealth(sp.GetRequiredService<AuditOptions>()));
builder.Services.AddSingleton(_ => new OutputStore(OutputStoreOptions.Production()));
builder.Services.AddSingleton(sp => new ScriptEvidenceStoreProvider(
    sp.GetRequiredService<AuditOptions>()));
builder.Services.AddScoped<AuditCallContextAccessor>();
builder.Services.AddSingleton(sp => new AuditRuntimeGate(
    sp.GetRequiredService<AuditOptions>(),
        sp.GetRequiredService<AuditHealth>(),
        sp.GetRequiredService<ScriptEvidenceStoreProvider>(),
        producerVersion,
        outputStore: sp.GetRequiredService<OutputStore>(),
        destinations: sp.GetRequiredService<AuditDestinationRegistry>()));
// The gate's hosted service is registered before the supervisor lifecycle so
// audit startup is durable before any session infrastructure starts.
builder.Services.AddSingleton<IHostedService>(
    sp => sp.GetRequiredService<AuditRuntimeGate>());

// Export is additive: it delivers the local journal onward and never gates
// execution (audit-restoration R3, contract rule 2). A missing, invalid, or
// unreachable destination costs delivery lag, never a refused call.
builder.Services.AddSingleton(new AuditExportHealth());
builder.Services.AddSingleton(sp => new AuditBackfillRegistry(
    sp.GetRequiredService<AuditOptions>().RootDirectory));
// The loopback audit web UI (R4): journal-backed log view, quarantine
// evidence, export health, and the settings page. One UI per audit root —
// supervisors race for the port, losers stand by. Never gates execution.
builder.Services.AddSingleton<IHostedService>(sp => new PtkMcpServer.Audit.Web.AuditWebUiService(
    sp.GetRequiredService<AuditOptions>(),
    sp.GetRequiredService<AuditHealth>(),
    sp.GetRequiredService<AuditExportHealth>(),
    () => sp.GetRequiredService<AuditRuntimeGate>().JournalForLiveExport,
    coordinator: sp.GetRequiredService<AuditExportCoordinator>(),
    destinationOperations: sp.GetRequiredService<AuditDestinationOperations>()));
// Operator alert webhook (R4, reporting surface (c)): edge-triggered POSTs
// for conditions that demand a human. Absent configuration means no service.
builder.Services.AddSingleton<IHostedService>(sp => new PtkMcpServer.Audit.Web.AuditAlertWebhookService(
    sp.GetRequiredService<AuditOptions>(),
    sp.GetRequiredService<AuditHealth>(),
    sp.GetRequiredService<AuditExportHealth>(),
    exportSettings.AlertWebhook));
builder.Services.AddSingleton(sp => new AuditExportCoordinator(
    sp.GetRequiredService<AuditOptions>(),
    sp.GetRequiredService<AuditDestinationRegistry>(),
    sp.GetRequiredService<AuditBackfillRegistry>(),
    sp.GetRequiredService<ScriptEvidenceStoreProvider>(),
    // The live segment is held FileShare.None by the journal writer; each
    // destination exporter reads the committed tail through this owner.
    () => sp.GetRequiredService<AuditRuntimeGate>().JournalForLiveExport,
    sp.GetRequiredService<AuditExportHealth>(),
    exportSettings.AlertWebhook));
builder.Services.AddSingleton<IHostedService>(sp =>
    sp.GetRequiredService<AuditExportCoordinator>());
builder.Services.AddSingleton<IAuditDestinationCredentialValidator>(
    _ => new AuditDestinationCredentialValidator());
builder.Services.AddSingleton<AuditDestinationOperations>();

// One supervisor owns this MCP connection's bounded worker/session registry.
// Submitted scripts execute only in contained worker processes. Constructing
// the supervisor spawns nothing; ordering does the audit gating — the gate's
// hosted service starts first, so a failed audit startup aborts the host
// before the supervisor lifecycle or the MCP transport ever start, and the
// audit call filter refuses any call that is not admitted to the journal.
builder.Services.AddSingleton(sp =>
{
    var health = sp.GetRequiredService<AuditHealth>();
    var exportHealth = sp.GetRequiredService<AuditExportHealth>();
    return WorkerSupervisor.CreateDefault(callTimeout, maxCallTimeout, () =>
    {
        var snapshot = health.Snapshot();
        var mode = snapshot.ProtectionMode == AuditProtectionMode.LocalOnly
            ? "local-only"
            : "anchored";
        var state = snapshot.State.ToString().ToLowerInvariant();
        var auditLine = snapshot.FailureClass is null
            ? $"audit: {state} mode={mode}"
            : $"audit: {state} mode={mode} failure_class={snapshot.FailureClass}";
        if (snapshot.UndeliveredEvictions > 0)
        {
            // Capacity pressure discarded records the exporter never
            // delivered. This must reach a surface an operator reads, not
            // only the server's stderr.
            auditLine +=
                $" SPOOL_EVICTED_UNDELIVERED={snapshot.UndeliveredEvictions}" +
                " (records were dropped before export; see audit export gaps)";
        }
        if (snapshot.LineagePublishFailures > 0)
        {
            // This boot's lineage attestation cannot be written (cr4-2):
            // journaling continues, but if this boot's records were wholly
            // destroyed before delivery, its successor could not name it.
            auditLine +=
                $" LINEAGE_UNPUBLISHED={snapshot.LineagePublishFailures}" +
                " (boot-lineage.json cannot be written; this boot is unattested)";
        }
        return auditLine + Environment.NewLine + exportHealth.Snapshot().StatusLine();
    });
});
builder.Services.AddScoped<ISessionOperations>(sp =>
    new AuditScopedSessionOperations(
        sp.GetRequiredService<WorkerSupervisor>(),
        sp.GetRequiredService<AuditCallContextAccessor>()));
builder.Services.AddSingleton(sp => new SupervisorLifecycle(
    sp.GetRequiredService<WorkerSupervisor>()));
builder.Services.AddSingleton<IHostedService>(
    sp => sp.GetRequiredService<SupervisorLifecycle>());
// Capture the transport streams BEFORE detaching stdin: the streams wrap the
// original handles, so the JSON-RPC channel keeps working while every child
// process spawned from the warm runspace inherits NUL/EOF instead of the live
// pipe (see ChildStdinGuard).
var mcpIn = Console.OpenStandardInput();
var mcpOut = Console.OpenStandardOutput();
PtkMcpServer.ChildStdinGuard.DetachChildStdin();
builder.Services
    .AddMcpServer(options => options.ScopeRequests = true)
    .WithStreamServerTransport(mcpIn, mcpOut)
    .WithRequestFilters(filters =>
    {
        // Audit admission is the outermost tools/call boundary: nothing is
        // dispatched unrecorded. The supervisor disposition filter stays
        // inner so audit observes the final shaped result.
        filters.AddCallToolFilter(AuditCallFilter.Create(
            callTimeout,
            maxCallTimeout,
            outputRequestProtector));
        filters.AddCallToolFilter(SupervisorCallFilter.Create());
    })
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

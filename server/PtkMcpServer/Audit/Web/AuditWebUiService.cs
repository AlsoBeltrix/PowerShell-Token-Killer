using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using PtkMcpServer.Audit.Export;

namespace PtkMcpServer.Audit.Web;

/// <summary>
/// The loopback audit web UI (audit-restoration R4): "open a browser, see
/// the logs". Serves the journal-backed views — recent records, quarantine
/// evidence, export health and gaps — plus the settings page that writes the
/// export configuration. It reads the journal artifacts directly, so it
/// works identically with no SIEM, an unreachable SIEM, or a healthy SIEM
/// (oar1-3), and it NEVER gates execution: any fault here degrades to a
/// missing web page.
///
/// One UI per audit root: supervisors race to bind the loopback port and the
/// losers stand by, retrying periodically, so whichever process survives
/// keeps serving. Requests authenticate with a bearer token minted into an
/// owner-only file under the audit root — loopback binding alone does not
/// stop a hostile web page from scripting requests at 127.0.0.1 (DNS
/// rebinding), but such a page cannot read the token file.
///
/// The token is minted fresh per bind, published only while this process
/// owns the listener, and deleted on stop (cr5-1): a credential is never
/// published while an unauthenticated process could own the configured
/// port, and a token a squatter manages to harvest dies at the next bind
/// instead of unlocking the real UI later. The unavoidable residue is
/// spoofing — a squatter can serve a fake page to an operator who types
/// the port by hand — but it cannot use what it captures.
/// </summary>
internal sealed class AuditWebUiService : IHostedService, IAsyncDisposable
{
    internal const string TokenFileName = "ui-token";
    internal const int DefaultPort = 8317;
    internal const string PortEnvironmentVariable = "PTK_AUDIT_UI_PORT";
    internal const string DisableEnvironmentVariable = "PTK_AUDIT_UI_DISABLED";
    private const int MaximumTailRecords = 500;
    private const int MaximumRequestBytes = 64 * 1024;

    private readonly AuditOptions _options;
    private readonly AuditHealth _health;
    private readonly AuditExportHealth _exportHealth;
    private readonly Func<AuditJournal?> _journalSource;
    private readonly int _port;
    private readonly TimeSpan _bindRetryInterval;
    private readonly CancellationTokenSource _stopping = new();
    private HttpListener? _listener;
    private Task? _loop;
    private string? _token;
    private bool _tokenPublished;
    private int _disposed;

    internal AuditWebUiService(
        AuditOptions options,
        AuditHealth health,
        AuditExportHealth exportHealth,
        Func<AuditJournal?> journalSource,
        int? port = null,
        TimeSpan? bindRetryInterval = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(exportHealth);
        ArgumentNullException.ThrowIfNull(journalSource);
        _options = options;
        _health = health;
        _exportHealth = exportHealth;
        _journalSource = journalSource;
        _port = port ?? ReadConfiguredPort();
        _bindRetryInterval = bindRetryInterval ?? TimeSpan.FromSeconds(60);
    }

    internal bool IsServing => _listener is not null;

    internal Uri? BoundAddress =>
        _listener is null ? null : new Uri($"http://127.0.0.1:{_port}/");

    private static int ReadConfiguredPort()
    {
        var text = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        return int.TryParse(text, out var port) && port is >= 1 and <= 65535
            ? port
            : DefaultPort;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(DisableEnvironmentVariable) == "1")
            return Task.CompletedTask;
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        // Retire the published credential before releasing the port: a
        // token must never outlive the listener it authenticates (cr5-1).
        if (_tokenPublished)
        {
            _tokenPublished = false;
            try { File.Delete(Path.Combine(_options.RootDirectory, TokenFileName)); }
            catch (Exception exception) when (!IsFatal(exception)) { }
        }
        try { _listener?.Stop(); }
        catch (Exception exception) when (!IsFatal(exception)) { }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Bind FIRST, mint after (cr5-1): the credential exists only
                // while this process owns the listener it opens, so nothing
                // is ever published toward a port a squatter could hold.
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                listener.Start();
                try
                {
                    _token = MintAndPublishToken();
                    _tokenPublished = true;
                }
                catch
                {
                    // Unpublishable token: release the port so another
                    // supervisor (or this one, next pass) can serve.
                    try { listener.Stop(); }
                    catch (Exception stopFailure) when (!IsFatal(stopFailure)) { }
                    throw;
                }
                _listener = listener;
                await ServeAsync(listener, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // Another supervisor on this root already serves the UI, or
                // the port is otherwise unavailable: stand by and retry. The
                // UI must never take the audit runtime down with it.
                _listener = null;
            }

            try
            {
                await Task.Delay(_bindRetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ServeAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                if (cancellationToken.IsCancellationRequested) return;
                continue;
            }

            _ = Task.Run(
                () => HandleSafelyAsync(context, cancellationToken),
                CancellationToken.None);
        }
    }

    private async Task HandleSafelyAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            try
            {
                await WriteJsonAsync(
                    context.Response,
                    500,
                    new { error = "internal" }).ConfigureAwait(false);
            }
            catch (Exception writeFailure) when (!IsFatal(writeFailure)) { }
        }
        finally
        {
            try { context.Response.Close(); }
            catch (Exception exception) when (!IsFatal(exception)) { }
        }
    }

    private async Task HandleAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        // Loopback + host pinning: a rebound DNS name still sends its own
        // Host header, and any non-loopback binding is refused outright.
        if (request.RemoteEndPoint?.Address is not { } remote ||
            !IPAddress.IsLoopback(remote) ||
            !IsLoopbackHost(request.UserHostName))
        {
            await WriteJsonAsync(context.Response, 403, new { error = "forbidden" })
                .ConfigureAwait(false);
            return;
        }

        if (!HasValidToken(request))
        {
            await WriteJsonAsync(context.Response, 401, new { error = "unauthorized" })
                .ConfigureAwait(false);
            return;
        }

        var path = request.Url?.AbsolutePath ?? "/";
        switch (request.HttpMethod, path)
        {
            case ("GET", "/"):
                await WriteHtmlAsync(context.Response, IndexHtml).ConfigureAwait(false);
                return;
            case ("GET", "/api/health"):
                await WriteJsonAsync(context.Response, 200, BuildHealth()).ConfigureAwait(false);
                return;
            case ("GET", "/api/records"):
                var read = ReadRecentRecords(ParseTail(request));
                await WriteJsonAsync(
                    context.Response,
                    200,
                    new
                    {
                        records = read.Records,
                        partial = read.Partial,
                        unreadable_count = read.UnreadableCount,
                        unreadable_segments = read.UnreadableSegments,
                        live_tail_error = read.LiveTailError,
                        read_error = read.ReadError,
                    })
                    .ConfigureAwait(false);
                return;
            case ("GET", "/api/quarantine"):
                await WriteJsonAsync(context.Response, 200, new { items = ReadQuarantine() })
                    .ConfigureAwait(false);
                return;
            case ("GET", "/api/settings"):
                await WriteJsonAsync(context.Response, 200, ReadSettings()).ConfigureAwait(false);
                return;
            case ("PUT", "/api/settings"):
                await HandleSettingsWriteAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await WriteJsonAsync(context.Response, 404, new { error = "not_found" })
                    .ConfigureAwait(false);
                return;
        }
    }

    private static bool IsLoopbackHost(string? userHostName)
    {
        if (string.IsNullOrEmpty(userHostName)) return false;
        var host = userHostName;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.Contains(']')) host = host[..colon];
        return host is "127.0.0.1" or "localhost" or "[::1]";
    }

    private bool HasValidToken(HttpListenerRequest request)
    {
        if (_token is null) return false;
        var presented = request.Headers["Authorization"] is { } header &&
            header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..]
            : request.QueryString["token"];
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(_token));
    }

    /// <summary>
    /// Mints a fresh token for THIS bind and publishes it atomically. A
    /// retained token is never reused: rotation is what makes a harvested
    /// or stale credential worthless against every future listener. The
    /// overwrite is safe because only the process holding the bind reaches
    /// here — a bind-failed standby never touches the file (cr5-5).
    /// </summary>
    private string MintAndPublishToken()
    {
        var path = Path.Combine(_options.RootDirectory, TokenFileName);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var temporaryPath = Path.Combine(
            _options.RootDirectory,
            $".{TokenFileName}.{Guid.NewGuid():N}.tmp");
        using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
        {
            stream.Write(Encoding.ASCII.GetBytes(token));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
        return token;
    }

    private object BuildHealth()
    {
        var audit = _health.Snapshot();
        var export = _exportHealth.Snapshot();
        long spoolBytes = 0;
        var segmentCount = 0;
        try
        {
            foreach (var file in Directory.GetFiles(_options.SpoolDirectory, "*.jsonl"))
            {
                segmentCount++;
                spoolBytes += new FileInfo(file).Length;
            }
        }
        catch (Exception exception) when (!IsFatal(exception)) { }

        return new
        {
            audit = new
            {
                state = audit.State.ToString().ToLowerInvariant(),
                mode = audit.ProtectionMode == AuditProtectionMode.LocalOnly
                    ? "local-only"
                    : "anchored",
                failure_class = audit.FailureClass,
                undelivered_evictions = audit.UndeliveredEvictions,
                lineage_publish_failures = audit.LineagePublishFailures,
            },
            export = new
            {
                status_line = export.StatusLine(),
                configured = export.Configured,
                delivered = export.DeliveredRecords,
                pending_bytes = export.PendingBytes,
                export_gaps = export.ExportGaps,
                missing_records = export.MissingRecords,
                refused_records = export.RefusedRecords,
                unverified_boot_boundaries = export.UnverifiedBootBoundaries,
                standby = export.Standby,
            },
            spool = new { segments = segmentCount, bytes = spoolBytes },
        };
    }

    private sealed record RecordsRead(
        IReadOnlyList<string> Records,
        int UnreadableCount,
        IReadOnlyList<object> UnreadableSegments,
        string? LiveTailError,
        string? ReadError)
    {
        public bool Partial =>
            UnreadableCount > 0 || LiveTailError is not null || ReadError is not null;
    }

    private const int MaximumReportedUnreadableSegments = 8;

    /// <summary>
    /// The newest records across the spool, oldest-first within the answer.
    /// Closed segments are read as files; this supervisor's own live tail is
    /// read through the journal writer's handle. Another supervisor's live
    /// segment becomes readable after rotation — the honest limit of a
    /// shared root, stated in the UI. Any other read failure is evidence the
    /// answer is missing, and the answer says so (cr5-3): only a vanished
    /// file (retention) and a lock-shaped failure on the newest segment of
    /// its boot — the one position a live segment can occupy — pass as
    /// expected.
    /// </summary>
    private RecordsRead ReadRecentRecords(int tail)
    {
        var lines = new List<string>();
        var unreadableCount = 0;
        var unreadable = new List<object>();
        string? liveTailError = null;
        string? readError = null;
        try
        {
            var files = new DirectoryInfo(_options.SpoolDirectory)
                .GetFiles("*.jsonl")
                .Select(file => AuditSpoolSegmentIdentity.TryParse(file.Name, out var identity)
                    ? (File: file, Identity: identity)
                    : default)
                .Where(entry => entry.File is not null)
                .OrderBy(entry => entry.File.LastWriteTimeUtc)
                .ToArray();
            var newestIndexPerBoot = files
                .GroupBy(entry => entry.Identity.SupervisorBootId)
                .ToDictionary(group => group.Key, group => group.Max(entry => entry.Identity.Index));
            foreach (var (file, identity) in files)
            {
                try
                {
                    using var stream = new FileStream(
                        file.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    while (reader.ReadLine() is { } line)
                    {
                        if (line.Length > 0) lines.Add(line);
                    }
                }
                catch (Exception exception) when (exception
                    is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Retention deleted it between enumeration and read.
                }
                catch (IOException) when (
                    identity.Index == newestIndexPerBoot[identity.SupervisorBootId])
                {
                    // The locked live segment: served below when it is ours,
                    // and readable after rotation when another supervisor's.
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    unreadableCount++;
                    if (unreadable.Count < MaximumReportedUnreadableSegments)
                    {
                        unreadable.Add(new
                        {
                            segment = file.Name,
                            error = exception.GetType().Name,
                        });
                    }
                }
            }

            var journal = _journalSource();
            if (journal is not null)
            {
                try
                {
                    // The live tail holds the NEWEST records, so it is read
                    // unconditionally (cr5-4): a populated closed spool must
                    // not short-circuit it. The read is bounded by the live
                    // segment itself — rotation caps its size, and every
                    // pass either advances the offset or breaks.
                    long offset = 0;
                    var identity = default(AuditSpoolSegmentIdentity);
                    var identityKnown = false;
                    while (true)
                    {
                        AuditCommittedSpoolRead read;
                        if (!identityKnown)
                        {
                            read = journal.ReadCommittedSpool(
                                AuditSpoolSegmentIdentity.Create(journal.SupervisorBootId, 0),
                                0,
                                _options.MaxRecordBytes);
                            if (read.CurrentSegment is not { } current) break;
                            identity = current;
                            identityKnown = true;
                            offset = 0;
                            continue;
                        }

                        read = journal.ReadCommittedSpool(identity, offset, _options.MaxRecordBytes);
                        if (read.Status != AuditCommittedSpoolReadStatus.Data ||
                            read.Bytes.IsEmpty)
                        {
                            break;
                        }
                        var text = Encoding.UTF8.GetString(read.Bytes.Span);
                        var lastNewline = text.LastIndexOf('\n');
                        if (lastNewline < 0) break;
                        foreach (var line in text[..lastNewline].Split('\n'))
                        {
                            if (line.Length > 0) lines.Add(line);
                        }
                        offset += Encoding.UTF8.GetByteCount(text[..(lastNewline + 1)]);
                    }
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    liveTailError = exception.GetType().Name;
                }
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            readError = exception.GetType().Name;
        }

        return new RecordsRead(
            lines.Count <= tail ? lines : lines[^tail..],
            unreadableCount,
            unreadable,
            liveTailError,
            readError);
    }

    private static int ParseTail(HttpListenerRequest request) =>
        int.TryParse(request.QueryString["tail"], out var tail) &&
        tail is >= 1 and <= MaximumTailRecords
            ? tail
            : 100;

    private IReadOnlyList<object> ReadQuarantine()
    {
        try
        {
            var directory = new DirectoryInfo(
                Path.Combine(_options.RootDirectory, AuditJournalFactory.QuarantineDirectoryName));
            if (!directory.Exists) return [];
            return directory.GetFiles()
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(100)
                .Select(object (file) => new
                {
                    name = file.Name,
                    bytes = file.Length,
                    modified_utc = file.LastWriteTimeUtc.ToString("O"),
                })
                .ToArray();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return [];
        }
    }

    private object ReadSettings()
    {
        var settings = AuditExportSettings.Load(_options.RootDirectory, out var failure);
        return new
        {
            kind = AuditExportSettings.KindText(settings.Kind),
            endpoint = settings.Endpoint?.ToString(),
            credential_set = !string.IsNullOrEmpty(settings.Credential),
            configuration_failure = failure,
            note = "Changes apply when PTK next starts; the export configuration is startup-frozen.",
        };
    }

    private async Task HandleSettingsWriteAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength64 is < 0 or > MaximumRequestBytes)
        {
            await WriteJsonAsync(context.Response, 400, new { error = "request_too_large" })
                .ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string? kind = null;
        string? endpoint = null;
        string? credential = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            kind = ReadString(root, "kind");
            endpoint = ReadString(root, "endpoint");
            credential = ReadString(root, "credential");
        }
        catch (JsonException)
        {
            await WriteJsonAsync(context.Response, 400, new { error = "invalid_json" })
                .ConfigureAwait(false);
            return;
        }

        if (!AuditExportSettings.TryValidateForWrite(kind, endpoint, out var validationFailure))
        {
            await WriteJsonAsync(context.Response, 400, new { error = validationFailure })
                .ConfigureAwait(false);
            return;
        }

        if (!AuditExportSettings.TryWrite(
                _options.RootDirectory,
                kind,
                endpoint,
                credential))
        {
            await WriteJsonAsync(context.Response, 500, new { error = "write_failed" })
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
            context.Response,
            200,
            new { saved = true, applies = "next start" }).ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int status,
        object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.StatusCode = status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = 200;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
        try { _listener?.Close(); }
        catch (Exception exception) when (!IsFatal(exception)) { }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private const string IndexHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>PTK Audit</title>
<style>
body{font-family:ui-monospace,Menlo,Consolas,monospace;margin:1.5rem;background:#111;color:#ddd}
h1{font-size:1.2rem} h2{font-size:1rem;margin-top:1.5rem;color:#9cf}
pre{background:#1a1a1a;padding:.75rem;overflow:auto;border-radius:6px}
table{border-collapse:collapse;width:100%} td,th{border-bottom:1px solid #333;padding:.25rem .5rem;text-align:left;font-size:.85rem}
input,select{background:#222;color:#ddd;border:1px solid #444;padding:.3rem;border-radius:4px}
button{background:#265;color:#fff;border:0;padding:.4rem .8rem;border-radius:4px;cursor:pointer}
.warn{color:#fa5}.ok{color:#5c5}#msg{margin-left:.75rem}
</style>
</head>
<body>
<h1>PTK Audit — local journal</h1>
<p>Everything on this page is read from the local audit journal; it does not
depend on any SIEM being reachable. Another supervisor's in-progress segment
appears here after it rotates.</p>
<h2>Health</h2><pre id="health">loading…</pre>
<h2>Recent records (<span id="count">…</span>)</h2>
<div id="partial" class="warn"></div>
<table id="records"><thead><tr><th>time</th><th>type</th><th>session</th><th>outcome</th></tr></thead><tbody></tbody></table>
<h2>Quarantine</h2><pre id="quarantine">loading…</pre>
<h2>SIEM connection</h2>
<form id="settings" onsubmit="return saveSettings(event)">
<label>Kind <select id="kind">
<option value="none">none (local only)</option>
<option value="otlp_http">OTLP / PTK receiver</option>
<option value="splunk_hec">Splunk HEC</option>
</select></label>
<label>Endpoint <input id="endpoint" size="40" placeholder="https://host:4318/"></label>
<label>Token <input id="credential" type="password" size="24" placeholder="(unchanged)"></label>
<button>Save</button><span id="msg"></span>
</form>
<p>Settings apply when PTK next starts; the export configuration is
startup-frozen by design.</p>
<script>
const token=new URLSearchParams(location.search).get('token')||'';
const api=(p)=>fetch(p,{headers:{Authorization:'Bearer '+token}});
const put=(p,b)=>fetch(p,{method:'PUT',headers:{Authorization:'Bearer '+token,'Content-Type':'application/json'},body:JSON.stringify(b)});
async function refresh(){
 const h=await (await api('/api/health')).json();
 document.getElementById('health').textContent=JSON.stringify(h,null,1);
 const r=await (await api('/api/records?tail=100')).json();
 const body=document.querySelector('#records tbody');body.innerHTML='';
 document.getElementById('count').textContent=r.records.length;
 document.getElementById('partial').textContent=r.partial?'WARNING: partial read — some journal evidence could not be read ('+(r.unreadable_count||0)+' unreadable segment(s)'+(r.live_tail_error?', live tail: '+r.live_tail_error:'')+(r.read_error?', spool: '+r.read_error:'')+')':'';
 for(const line of r.records.slice().reverse()){
  let rec;try{rec=JSON.parse(line)}catch{rec=null}
  const tr=document.createElement('tr');
  const cells=rec?[rec.observed_utc,rec.event_type,(rec.session&&rec.session.name)||'',(rec.outcome&&rec.outcome.state)||'']:[ '','unparseable','',''];
  for(const c of cells){const td=document.createElement('td');td.textContent=c||'';tr.appendChild(td)}
  tr.title=line;body.appendChild(tr);
 }
 const q=await (await api('/api/quarantine')).json();
 document.getElementById('quarantine').textContent=q.items.length?JSON.stringify(q.items,null,1):'none';
 const s=await (await api('/api/settings')).json();
 document.getElementById('kind').value=s.kind||'none';
 document.getElementById('endpoint').value=s.endpoint||'';
}
async function saveSettings(e){
 e.preventDefault();
 const body={kind:document.getElementById('kind').value,endpoint:document.getElementById('endpoint').value};
 const cred=document.getElementById('credential').value;if(cred)body.credential=cred;
 const res=await put('/api/settings',body);
 document.getElementById('msg').textContent=res.ok?'saved — applies at next PTK start':'save failed';
 document.getElementById('msg').className=res.ok?'ok':'warn';
 return false;
}
refresh();setInterval(refresh,5000);
</script>
</body>
</html>
""";
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PtkSiemReceiver.Configuration;

namespace PtkSiemReceiver.Web;

/// <summary>
/// Marker set on connections accepted by the operator listener, so request
/// handlers can tell the two surfaces apart without port bookkeeping: ingest
/// must never serve on the operator port and the operator API must never
/// serve on ingest, whatever ports the deployment chose.
/// </summary>
internal interface IOperatorSurfaceFeature;

internal sealed class OperatorSurfaceFeature : IOperatorSurfaceFeature
{
    internal static readonly OperatorSurfaceFeature Instance = new();
}

/// <summary>Container-owned holder so the operator HTTPS certificate is
/// disposed with the application (mirrors the ingest certificate's
/// factory-singleton ownership).</summary>
internal sealed class OperatorTlsMaterial(
    System.Security.Cryptography.X509Certificates.X509Certificate2? certificate) : IDisposable
{
    internal System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate { get; }
        = certificate;

    public void Dispose() => Certificate?.Dispose();
}

/// <summary>
/// The read-only operator query API + dashboard (mini-SIEM S5, executed as
/// audit-restoration R5b): events by time/type/session/boot filters, event
/// detail with chain context, chain status, and the quarantine evidence
/// list, rendered by one embedded static page. Separate listener from
/// ingest; bearer-token auth from the protected config; loopback-bound by
/// default, and the config loader refuses a non-loopback bind without an
/// operator HTTPS certificate, so the token never travels plaintext
/// off-host. Everything here reads the store through short-lived read-only
/// connections — this surface can inspect evidence, never create or change
/// it (the alert-lifecycle writer arrives with S6).
/// </summary>
internal static class OperatorEndpoints
{
    private const int DefaultEventLimit = 100;
    private const int MaximumEventLimit = 500;
    internal const int MaximumChainLimit = 200;

    internal static void Map(WebApplication application)
    {
        application.MapGet("/", HandleDashboardAsync);
        application.MapGet("/api/events", HandleEventsAsync);
        application.MapGet("/api/events/{eventId}", HandleEventDetailAsync);
        application.MapGet("/api/chains", HandleChainsAsync);
        application.MapGet("/api/quarantine", HandleQuarantineAsync);
    }

    // ---- Admission ----

    private static async Task<bool> AdmitAsync(HttpContext context, SiemReceiverOptions options)
    {
        if (!await AdmitSurfaceAsync(context, options).ConfigureAwait(false)) return false;

        if (!HasValidOperatorToken(context.Request, options.OperatorToken))
        {
            await WriteJsonAsync(
                context, 401, new { error = "unauthorized" }).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    /// <summary>Surface + Host admission without the credential: the static
    /// dashboard page carries zero evidence, so it serves token-free and the
    /// operator pastes the token into the page instead of the URL — a URL
    /// travels through request logs and browser history, a header does
    /// not.</summary>
    private static async Task<bool> AdmitSurfaceAsync(
        HttpContext context, SiemReceiverOptions options)
    {
        if (context.Features.Get<IOperatorSurfaceFeature>() is null)
        {
            await WriteJsonAsync(
                context, 404, new { error = "not_found" }).ConfigureAwait(false);
            return false;
        }

        // Plain-HTTP serving is loopback-only by configuration; pin the Host
        // header too so a DNS-rebound page cannot script the API (the
        // producer UI's rule). An HTTPS operator endpoint authenticates the
        // server by certificate instead.
        if (options.OperatorHttpsCertificatePath is null &&
            !IsLoopbackHost(context.Request.Host.Host))
        {
            await WriteJsonAsync(
                context, 403, new { error = "forbidden" }).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "localhost" or "::1";

    // Header-only on purpose: a query-string credential lands in request
    // logs and browser history (cr7-1).
    private static bool HasValidOperatorToken(HttpRequest request, string operatorToken)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var presented = header["Bearer ".Length..];
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(operatorToken));
    }

    // ---- Read-only queries ----

    internal static async Task HandleEventsAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        var limit = ParseLimit(context.Request.Query["limit"].ToString());
        var filters = new List<string>();
        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        AddOptionalFilter(command, filters, "occurred_utc >= $from",
            "$from", context.Request.Query["from"].ToString());
        AddOptionalFilter(command, filters, "occurred_utc <= $to",
            "$to", context.Request.Query["to"].ToString());
        AddOptionalFilter(command, filters, "event_type = $type",
            "$type", context.Request.Query["type"].ToString());
        AddOptionalFilter(command, filters, "session_name = $session",
            "$session", context.Request.Query["session"].ToString());
        AddOptionalFilter(command, filters, "supervisor_boot_id = $boot",
            "$boot", context.Request.Query["boot"].ToString());
        command.CommandText =
            "SELECT event_id, supervisor_boot_id, sequence, schema_version, event_type, " +
            "occurred_utc, observed_utc, session_name, session_generation, outcome_state, " +
            "received_utc FROM events" +
            (filters.Count > 0 ? " WHERE " + string.Join(" AND ", filters) : string.Empty) +
            " ORDER BY occurred_utc DESC, sequence DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<object>();
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                events.Add(new
                {
                    event_id = reader.GetString(0),
                    supervisor_boot_id = reader.GetString(1),
                    sequence = reader.GetInt64(2),
                    schema_version = reader.GetString(3),
                    event_type = reader.GetString(4),
                    occurred_utc = reader.GetString(5),
                    observed_utc = reader.GetString(6),
                    session_name = reader.IsDBNull(7) ? null : reader.GetString(7),
                    session_generation = reader.IsDBNull(8) ? (long?)null : reader.GetInt64(8),
                    outcome_state = reader.IsDBNull(9) ? null : reader.GetString(9),
                    received_utc = reader.GetString(10),
                });
            }
        }

        await WriteJsonAsync(context, 200, new { events, limit }).ConfigureAwait(false);
    }

    internal static async Task HandleEventDetailAsync(
        HttpContext context,
        SiemReceiverOptions options,
        string eventId)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;
        if (!Guid.TryParseExact(eventId, "D", out _))
        {
            await WriteJsonAsync(
                context, 400, new { error = "event_id" }).ConfigureAwait(false);
            return;
        }

        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT event_id, supervisor_boot_id, sequence, schema_version, event_type, " +
            "occurred_utc, observed_utc, previous_event_hash, event_hash, exact_json_body, " +
            "received_utc FROM events WHERE event_id = $id;";
        command.Parameters.AddWithValue("$id", eventId);
        string bootId;
        long sequence;
        object detail;
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                await WriteJsonAsync(
                    context, 404, new { error = "unknown_event" }).ConfigureAwait(false);
                return;
            }

            bootId = reader.GetString(1);
            sequence = reader.GetInt64(2);
            detail = new
            {
                event_id = reader.GetString(0),
                supervisor_boot_id = bootId,
                sequence,
                schema_version = reader.GetString(3),
                event_type = reader.GetString(4),
                occurred_utc = reader.GetString(5),
                observed_utc = reader.GetString(6),
                previous_event_hash = reader.IsDBNull(7) ? null : reader.GetString(7),
                event_hash = reader.GetString(8),
                body = Encoding.UTF8.GetString((byte[])reader.GetValue(9)),
                received_utc = reader.GetString(10),
            };
        }

        var neighbors = new List<object>();
        using (var neighborCommand = connection.CreateCommand())
        {
            neighborCommand.CommandText =
                "SELECT event_id, sequence, event_type FROM events " +
                "WHERE supervisor_boot_id = $boot AND sequence IN ($prev, $next);";
            neighborCommand.Parameters.AddWithValue("$boot", bootId);
            neighborCommand.Parameters.AddWithValue("$prev", sequence - 1);
            neighborCommand.Parameters.AddWithValue("$next", sequence + 1);
            using var reader = await neighborCommand.ExecuteReaderAsync(context.RequestAborted)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                neighbors.Add(new
                {
                    event_id = reader.GetString(0),
                    sequence = reader.GetInt64(1),
                    event_type = reader.GetString(2),
                });
            }
        }

        object? chain = null;
        using (var chainCommand = connection.CreateCommand())
        {
            chainCommand.CommandText =
                "SELECT head_sequence, head_event_id, head_event_hash FROM chains " +
                "WHERE supervisor_boot_id = $boot;";
            chainCommand.Parameters.AddWithValue("$boot", bootId);
            using var reader = await chainCommand.ExecuteReaderAsync(context.RequestAborted)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                chain = new
                {
                    head_sequence = reader.GetInt64(0),
                    head_event_id = reader.GetString(1),
                    head_event_hash = reader.GetString(2),
                };
            }
        }

        await WriteJsonAsync(
            context, 200, new { @event = detail, neighbors, chain }).ConfigureAwait(false);
    }

    internal static async Task HandleChainsAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        // Bounded like every other list (cr7-3): the newest boots window
        // serves triage; growth in retained history must not grow this
        // response. One extra row detects truncation.
        command.CommandText =
            "SELECT c.supervisor_boot_id, c.head_sequence, c.head_event_id, " +
            "c.head_event_hash, COUNT(e.event_id), MAX(e.received_utc) " +
            "FROM chains c LEFT JOIN events e ON e.supervisor_boot_id = c.supervisor_boot_id " +
            "GROUP BY c.supervisor_boot_id " +
            "ORDER BY MAX(e.received_utc) DESC, c.supervisor_boot_id DESC " +
            "LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", MaximumChainLimit + 1);
        var chains = new List<object>();
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                chains.Add(new
                {
                    supervisor_boot_id = reader.GetString(0),
                    head_sequence = reader.GetInt64(1),
                    head_event_id = reader.GetString(2),
                    head_event_hash = reader.GetString(3),
                    stored_events = reader.GetInt64(4),
                    last_received_utc = reader.IsDBNull(5) ? null : reader.GetString(5),
                });
            }
        }

        var truncated = chains.Count > MaximumChainLimit;
        if (truncated) chains.RemoveAt(MaximumChainLimit);

        await WriteJsonAsync(context, 200, new { chains, truncated }).ConfigureAwait(false);
    }

    internal static async Task HandleQuarantineAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitAsync(context, options).ConfigureAwait(false)) return;

        using var connection = OpenReadOnly(options.SqlitePath);
        using var command = connection.CreateCommand();
        // Bounded and blob-free: the list is triage; the raw evidence stays
        // in the store.
        command.CommandText =
            "SELECT attempt_id, failure_code, claimed_event_id, " +
            "claimed_supervisor_boot_id, claimed_sequence, received_utc " +
            "FROM quarantine ORDER BY attempt_id DESC LIMIT 100;";
        var items = new List<object>();
        using (var reader = await command.ExecuteReaderAsync(context.RequestAborted)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false))
            {
                items.Add(new
                {
                    attempt_id = reader.GetInt64(0),
                    failure_code = reader.GetString(1),
                    claimed_event_id = reader.IsDBNull(2) ? null : reader.GetString(2),
                    claimed_supervisor_boot_id =
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                    claimed_sequence = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4),
                    received_utc = reader.GetString(5),
                });
            }
        }

        await WriteJsonAsync(context, 200, new { items }).ConfigureAwait(false);
    }

    internal static async Task HandleDashboardAsync(
        HttpContext context,
        SiemReceiverOptions options)
    {
        if (!await AdmitSurfaceAsync(context, options).ConfigureAwait(false)) return;
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(DashboardHtml);
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted)
            .ConfigureAwait(false);
    }

    // ---- Plumbing ----

    private static SqliteConnection OpenReadOnly(string sqlitePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = sqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();
        return connection;
    }

    private static void AddOptionalFilter(
        SqliteCommand command,
        List<string> filters,
        string clause,
        string parameterName,
        string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        filters.Add(clause);
        command.Parameters.AddWithValue(parameterName, value);
    }

    private static int ParseLimit(string text) =>
        int.TryParse(text, out var limit) && limit is >= 1 and <= MaximumEventLimit
            ? limit
            : DefaultEventLimit;

    private static async Task WriteJsonAsync(HttpContext context, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted)
            .ConfigureAwait(false);
    }

    // No third-party script: the plan sketched htmx, but a static page with
    // inline fetch calls serves the same read-only views without vendoring a
    // dependency into the evidence surface (simplicity rule; same posture as
    // the producer's audit UI).
    private const string DashboardHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>PTK SIEM Receiver</title>
<style>
body{font-family:ui-monospace,Menlo,Consolas,monospace;margin:1.5rem;background:#111;color:#ddd}
h1{font-size:1.2rem} h2{font-size:1rem;margin-top:1.5rem;color:#9cf}
pre{background:#1a1a1a;padding:.75rem;overflow:auto;border-radius:6px}
table{border-collapse:collapse;width:100%} td,th{border-bottom:1px solid #333;padding:.25rem .5rem;text-align:left;font-size:.85rem}
input,select{background:#222;color:#ddd;border:1px solid #444;padding:.3rem;border-radius:4px}
button{background:#265;color:#fff;border:0;padding:.4rem .8rem;border-radius:4px;cursor:pointer}
.warn{color:#fa5}
</style>
</head>
<body>
<h1>PTK SIEM Receiver — stored audit evidence</h1>
<form id="auth" onsubmit="return saveToken(event)" style="display:none">
<input id="tok" type="password" placeholder="operator token" size="40"> <button>Unlock</button>
<span class="warn">token required</span>
</form>
<h2>Chains</h2><pre id="chains">loading…</pre>
<h2>Events</h2>
<form onsubmit="return refreshEvents(event)">
<input id="type" placeholder="event_type"> <input id="session" placeholder="session">
<input id="boot" placeholder="boot id" size="38"> <button>Filter</button>
</form>
<table id="events"><thead><tr><th>occurred</th><th>type</th><th>boot</th><th>seq</th><th>session</th><th>outcome</th></tr></thead><tbody></tbody></table>
<h2>Quarantine</h2><pre id="quarantine">loading…</pre>
<script>
let token=sessionStorage.getItem('ptk_operator_token')||'';
const api=(p)=>fetch(p,{headers:{Authorization:'Bearer '+token}});
function saveToken(e){
 e.preventDefault();
 token=document.getElementById('tok').value.trim();
 sessionStorage.setItem('ptk_operator_token',token);
 refresh();
 return false;
}
async function refreshEvents(e){
 if(e)e.preventDefault();
 const q=new URLSearchParams();
 for(const k of ['type','session','boot']){const v=document.getElementById(k).value;if(v)q.set(k,v);}
 const r=await (await api('/api/events?'+q)).json();
 const body=document.querySelector('#events tbody');body.innerHTML='';
 for(const ev of r.events){
  const tr=document.createElement('tr');
  for(const c of [ev.occurred_utc,ev.event_type,ev.supervisor_boot_id,ev.sequence,ev.session_name||'',ev.outcome_state||'']){
   const td=document.createElement('td');td.textContent=c;tr.appendChild(td);}
  tr.title=ev.event_id;body.appendChild(tr);
 }
 return false;
}
async function refresh(){
 const r=await api('/api/chains');
 if(r.status===401){document.getElementById('auth').style.display='';return;}
 document.getElementById('auth').style.display='none';
 const c=await r.json();
 document.getElementById('chains').textContent=JSON.stringify(c.chains,null,1);
 await refreshEvents();
 const q=await (await api('/api/quarantine')).json();
 document.getElementById('quarantine').textContent=q.items.length?JSON.stringify(q.items,null,1):'none';
}
async function loop(){try{await refresh();}finally{setTimeout(loop,10000);}}
loop();
</script>
</body>
</html>
""";
}

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.Models;

namespace DonutHypixelPlayerComparer.Services;

public sealed record DonutBridgeStatus(
    bool Active,
    bool ClientConnected,
    int QueueDepth,
    string? CurrentUsername,
    DateTimeOffset? LastHeartbeat);

public sealed class DonutBridgeHost : IDisposable
{
    private const int MaxRequestBodyBytes = 256_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AppSettings _settings;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<BridgeJob> _queue = new();
    private readonly ConcurrentDictionary<string, BridgeJob> _jobs = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private BridgeHeartbeat? _heartbeat;
    private BridgeJob? _leasedJob;
    private volatile bool _active;

    public DonutBridgeHost(AppSettings settings) => _settings = settings;

    /// <summary>Raised for every bridge event so the scan log can show what the Minecraft client is doing.</summary>
    public event Action<string>? Diagnostic;

    private void Emit(string message, string? detail = null)
    {
        BridgeDebugLog.Write(message, detail);
        try { Diagnostic?.Invoke(message); } catch { }
    }

    public DonutBridgeStatus Status
    {
        get
        {
            lock (_gate)
            {
                var heartbeat = _heartbeat;
                var connected = heartbeat is not null
                    && DateTimeOffset.UtcNow - heartbeat.ReceivedAt <= TimeSpan.FromSeconds(8);
                var leased = _leasedJob;
                return new DonutBridgeStatus(
                    _active,
                    connected,
                    _queue.Count + (leased is null ? 0 : 1),
                    leased?.Username,
                    heartbeat?.ReceivedAt);
            }
        }
    }

    public void Start()
    {
        string listenerPrefix;
        lock (_gate)
        {
            if (_active) return;
            _currentUsernameReset();
            _queue.Clear();
            _jobs.Clear();
            _heartbeat = null;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            // A Java client using "localhost" often resolves to ::1 first, so listen on both loopback stacks.
            var ipv4 = $"http://127.0.0.1:{_settings.DonutBridgePort}/";
            var ipv6 = $"http://[::1]:{_settings.DonutBridgePort}/";
            try
            {
                listenerPrefix = StartListener(ipv4, ipv6);
            }
            catch (HttpListenerException ex)
            {
                _cts.Dispose();
                _cts = null;
                throw new InvalidOperationException(
                    $"Could not start Donut bridge on port {_settings.DonutBridgePort}. Close other apps using that port. ({ex.Message})");
            }
            _active = true;
            // The token is read now, not when the task body starts: Dispose can null _cts first,
            // which would kill the listen loop with a NullReferenceException nobody ever sees.
            var loopToken = _cts.Token;
            _loop = Task.Run(() => ListenAsync(loopToken));
        }
        BridgeDebugLog.StartSession(
            $"Bridge session started on {listenerPrefix} (job timeout {_settings.DonutBridgeJobTimeoutSeconds}s, command '{_settings.DonutBridgeCommandTemplate}')");
    }

    private string StartListener(string ipv4, string ipv6)
    {
        _listener.Stop();
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(ipv4);
        _listener.Prefixes.Add(ipv6);
        try
        {
            _listener.Start();
            return ipv4 + " and " + ipv6;
        }
        catch (HttpListenerException)
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(ipv4);
            _listener.Start();
            return ipv4 + " (IPv6 loopback unavailable)";
        }
    }

    public async Task<DonutStats?> WaitForStatsAsync(string username, CancellationToken token)
    {
        BridgeJob job;
        lock (_gate)
        {
            if (!_active) throw new InvalidOperationException("Donut bridge is not running.");
            job = new BridgeJob(username);
            _jobs[job.JobId] = job;
            _queue.Enqueue(job);
        }
        Emit($"Bridge: queued {username} (job {job.JobId[..8]}).");

        var leaseSeconds = Math.Max(90, _settings.DonutBridgeJobTimeoutSeconds * 3);
        try
        {
            try
            {
                await job.Leased.Task.WaitAsync(TimeSpan.FromSeconds(leaseSeconds), token);
            }
            catch (TimeoutException)
            {
                job.TryFail("Client never picked up the job.");
                throw new TimeoutException(
                    $"No Minecraft client requested a job for {leaseSeconds}s. Enable PlayerCheckerBridge on DonutSMP and confirm port {_settings.DonutBridgePort}.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(_settings.DonutBridgeJobTimeoutSeconds));
            var result = await job.Completion.Task.WaitAsync(timeout.Token);
            if (result.Stats is null)
                throw new InvalidOperationException(result.Error ?? "Donut bridge job failed.");
            return result.Stats;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            job.TryFail("Timed out waiting for Minecraft client bridge.");
            Emit($"Bridge: {username} timed out after {_settings.DonutBridgeJobTimeoutSeconds}s waiting for the client to submit stats.");
            throw new TimeoutException(
                $"Donut bridge timed out for {username} after {_settings.DonutBridgeJobTimeoutSeconds}s. The client took the job but never posted results.");
        }
        finally
        {
            lock (_gate)
            {
                _jobs.TryRemove(job.JobId, out _);
                if (ReferenceEquals(_leasedJob, job)) _leasedJob = null;
                job.Abandoned = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_active && _cts is null) return;
            _active = false;
            _cts?.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            foreach (var job in _jobs.Values) job.TryFail("Scan ended.");
            _jobs.Clear();
            while (_queue.TryDequeue(out _)) { }
            _leasedJob = null;
            _heartbeat = null;
        }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    private void _currentUsernameReset() => _leasedJob = null;

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { if (token.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => HandleRequestAsync(context));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!IsLoopbackHost(context.Request.UserHostName))
            {
                // The listener is loopback-only, but a page in the user's browser can still reach it
                // through a hostname that resolves to 127.0.0.1. Requiring a loopback Host header
                // keeps the bridge answering only to things that addressed it as localhost.
                Emit($"Bridge: rejected a request with a non-loopback Host header '{context.Request.UserHostName}'.");
                await WriteTextAsync(context, 403, "Forbidden");
                return;
            }
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            var method = context.Request.HttpMethod.ToUpperInvariant();
            if (method == "GET" && path == "/api/v1/status")
                await WriteJsonAsync(context, 200, BuildStatusPayload());
            else if (method == "POST" && path == "/api/v1/heartbeat")
                await HandleHeartbeatAsync(context);
            else if (method == "GET" && path == "/api/v1/job")
                await HandleNextJobAsync(context);
            else if (method == "POST" && path.StartsWith("/api/v1/job/", StringComparison.Ordinal)
                     && path.EndsWith("/complete", StringComparison.Ordinal))
                await HandleCompleteAsync(context, path);
            else if (method == "POST" && path.StartsWith("/api/v1/job/", StringComparison.Ordinal)
                     && path.EndsWith("/fail", StringComparison.Ordinal))
                await HandleFailAsync(context, path);
            else
            {
                Emit($"Bridge: client called unknown endpoint {method} {path}.");
                await WriteTextAsync(context, 404, "Not found");
            }
        }
        catch (Exception ex)
        {
            BridgeDebugLog.Write("Bridge: request handler threw " + ex.GetType().Name, ex.Message);
            try { await WriteJsonAsync(context, 500, new { error = "Bridge request failed." }); } catch { }
        }
    }

    internal static bool IsLoopbackHost(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader)) return false;
        var host = hostHeader.Trim();
        // Strip the port, taking care not to split an unbracketed IPv6 literal on its own colons.
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']');
            if (end < 0) return false;
            host = host[1..end];
        }
        else
        {
            var colon = host.IndexOf(':');
            if (colon >= 0 && host.IndexOf(':', colon + 1) < 0) host = host[..colon];
        }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private object BuildStatusPayload()
    {
        var status = Status;
        return new
        {
            active = status.Active,
            queueDepth = status.QueueDepth,
            currentUsername = status.CurrentUsername,
            commandTemplate = _settings.DonutBridgeCommandTemplate,
            commandDelayMs = _settings.DonutBridgeCommandDelayMs,
            clientConnected = status.ClientConnected,
            lastHeartbeat = status.LastHeartbeat
        };
    }

    private async Task HandleHeartbeatAsync(HttpListenerContext context)
    {
        var body = await ReadBodyAsync(context.Request);
        if (body is null)
        {
            await WriteJsonAsync(context, 400, new { error = "Invalid heartbeat body." });
            return;
        }
        BridgeHeartbeat heartbeat;
        try
        {
            heartbeat = JsonSerializer.Deserialize<BridgeHeartbeat>(body, JsonOptions) ?? new BridgeHeartbeat();
        }
        catch (JsonException ex)
        {
            BridgeDebugLog.Write("Bridge: invalid heartbeat JSON: " + ex.Message, BridgeDebugLog.Preview(body));
            await WriteJsonAsync(context, 400, new { error = "Invalid heartbeat body." });
            return;
        }
        heartbeat.ReceivedAt = DateTimeOffset.UtcNow;
        bool first;
        // Status reads _heartbeat under the gate, so the swap belongs there too — otherwise two
        // concurrent heartbeats can both decide they are the first one.
        lock (_gate)
        {
            first = _heartbeat is null;
            _heartbeat = heartbeat;
        }
        if (first)
            Emit($"Bridge: client connected — {heartbeat.PlayerName} on {heartbeat.ServerAddress} (v{heartbeat.ClientVersion}).");
        await WriteJsonAsync(context, 200, new { ok = true });
    }

    private async Task HandleNextJobAsync(HttpListenerContext context)
    {
        // A caller that timed out or was cancelled leaves its job in the queue. Handing that job to
        // the client would make it walk an abandoned player and would pin the status display to a
        // username nothing is waiting for, so those are skipped here.
        BridgeJob? job = null;
        while (_queue.TryDequeue(out var candidate))
        {
            lock (_gate)
            {
                if (candidate.Abandoned || !_jobs.ContainsKey(candidate.JobId)) continue;
                job = candidate;
                _leasedJob = candidate;
            }
            break;
        }
        if (job is null)
        {
            context.Response.StatusCode = 204;
            context.Response.Close();
            return;
        }
        job.MarkLeased();
        Emit($"Bridge: client took job for {job.Username} (job {job.JobId[..8]}).");
        await WriteJsonAsync(context, 200, new { jobId = job.JobId, username = job.Username });
    }

    private async Task HandleCompleteAsync(HttpListenerContext context, string path)
    {
        var jobId = ExtractJobId(path, "/complete");
        var body = await ReadBodyAsync(context.Request);
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            // The lease has to be released even when its job is gone, or the status bar keeps
            // reporting a player that nothing is waiting for.
            ReleaseLease(jobId);
            Emit($"Bridge: completion for unknown/expired job {Shorten(jobId)} was ignored.",
                BridgeDebugLog.Preview(body));
            await WriteJsonAsync(context, 404, new { error = "Unknown job." });
            return;
        }
        BridgeDebugLog.Write($"Bridge: completion body for {job.Username} ({body?.Length ?? 0} chars)",
            BridgeDebugLog.Preview(body, 2000));
        if (body is null)
        {
            job.TryFail("Client posted an empty completion body.");
            Emit($"Bridge: {job.Username} sent an empty completion body.");
            await WriteJsonAsync(context, 400, new { error = "Invalid JSON body." });
            return;
        }
        DonutBridgeStatsPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<DonutBridgeStatsPayload>(body, JsonOptions) ?? new DonutBridgeStatsPayload();
        }
        catch (JsonException ex)
        {
            job.TryFail("Client sent invalid JSON: " + ex.Message);
            Emit($"Bridge: {job.Username} sent invalid JSON — {ex.Message}");
            await WriteJsonAsync(context, 400, new { error = "Invalid JSON body." });
            return;
        }
        DonutStats stats;
        try
        {
            stats = DonutGuiStatsParser.FromPayload(payload);
            if (stats.Money == 0 && payload.RawGuiText?.TryGetValue("chat", out var chatLines) == true
                && DonutGuiStatsParser.TryParseBalanceFromChat(chatLines, job.Username, out var chatMoney))
            {
                stats.Money = chatMoney;
            }
        }
        catch (Exception ex)
        {
            job.TryFail("Stat parsing failed: " + ex.Message);
            Emit($"Bridge: could not parse stats for {job.Username} — {ex.Message}");
            await WriteJsonAsync(context, 422, new { error = "Could not parse stats." });
            return;
        }
        if (!DonutGuiStatsParser.HasMinimumStats(stats, payload))
        {
            var reason = payload.RawGuiText is { Count: > 0 }
                ? "every stat parsed as zero"
                : "the client sent no stat values and no rawGuiText";
            job.TryFail($"Client returned no usable stats ({reason}). See bridge-log.txt.");
            Emit($"Bridge: {job.Username} returned no usable stats — {reason}.");
            await WriteJsonAsync(context, 422, new { error = "Parsed stats missing required fields." });
            return;
        }
        lock (_gate)
        {
            if (ReferenceEquals(_leasedJob, job)) _leasedJob = null;
        }
        job.TryComplete(stats);
        Emit($"Bridge: {job.Username} → money {stats.Money:N0}, shards {stats.Shards:N0}, "
             + $"kills {stats.Kills:N0}, deaths {stats.Deaths:N0}, playtime {stats.PlaytimeSeconds / 3600}h.");
        await WriteJsonAsync(context, 200, new { ok = true });
    }

    private async Task HandleFailAsync(HttpListenerContext context, string path)
    {
        var jobId = ExtractJobId(path, "/fail");
        var body = await ReadBodyAsync(context.Request);
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            ReleaseLease(jobId);
            Emit($"Bridge: failure report for unknown/expired job {Shorten(jobId)} was ignored.",
                BridgeDebugLog.Preview(body));
            await WriteJsonAsync(context, 404, new { error = "Unknown job." });
            return;
        }
        BridgeFailPayload? payload = null;
        if (body is not null)
        {
            try { payload = JsonSerializer.Deserialize<BridgeFailPayload>(body, JsonOptions); }
            catch (JsonException) { }
        }
        var salvaged = DonutGuiStatsParser.TrySalvageFromBridgePayload(payload?.Error, payload?.RawGuiText, job.Username);
        if (salvaged is not null)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_leasedJob, job)) _leasedJob = null;
            }
            job.TryComplete(salvaged);
            Emit($"Bridge: salvaged {job.Username} from client failure — money {salvaged.Money:N0}.");
            await WriteJsonAsync(context, 200, new { ok = true, salvaged = true });
            return;
        }
        lock (_gate)
        {
            if (ReferenceEquals(_leasedJob, job)) _leasedJob = null;
        }
        var error = string.IsNullOrWhiteSpace(payload?.Error) ? "Client reported a failure." : payload!.Error;
        BridgeDebugLog.Write($"Bridge: failure body for {job.Username}", BridgeDebugLog.Preview(body, 2000));
        job.TryFail(error);
        Emit($"Bridge: client failed {job.Username} — {error}");
        await WriteJsonAsync(context, 200, new { ok = true });
    }

    /// <summary>
    /// Reads at most one body's worth of characters. A chunked request reports ContentLength64 as -1,
    /// so the length header alone cannot be trusted to bound the read.
    /// </summary>
    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxRequestBodyBytes) return null;
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var buffer = new char[8192];
        var builder = new StringBuilder();
        while (builder.Length <= MaxRequestBodyBytes)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            builder.Append(buffer, 0, read);
        }
        if (builder.Length > MaxRequestBodyBytes) return null;
        var body = builder.ToString();
        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private void ReleaseLease(string jobId)
    {
        lock (_gate)
        {
            if (_leasedJob is not null && _leasedJob.JobId == jobId) _leasedJob = null;
        }
    }

    private static string Shorten(string jobId) =>
        jobId.Length <= 8 ? (jobId.Length == 0 ? "<none>" : jobId) : jobId[..8];

    private static string ExtractJobId(string path, string suffix)
    {
        const string prefix = "/api/v1/job/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
            return string.Empty;
        return path[prefix.Length..^suffix.Length];
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await WriteTextAsync(context, statusCode, json, "application/json");
    }

    private static async Task WriteTextAsync(HttpListenerContext context, int statusCode, string text, string contentType = "text/plain")
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType + "; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentLength64 = bytes.Length;
        try
        {
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (HttpListenerException)
        {
            // The Minecraft client hung up mid-write; drop the connection instead of leaking it.
            context.Response.Abort();
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { context.Response.Abort(); }
    }

    private sealed class BridgeJob
    {
        public BridgeJob(string username)
        {
            Username = username;
            JobId = Guid.NewGuid().ToString("N");
        }

        public string JobId { get; }
        public string Username { get; }

        /// <summary>Set once the caller has stopped waiting; guarded by the host's gate.</summary>
        public bool Abandoned { get; set; }
        public TaskCompletionSource<bool> Leased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BridgeJobResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkLeased() => Leased.TrySetResult(true);

        public void TryComplete(DonutStats stats) =>
            Completion.TrySetResult(new BridgeJobResult(stats, null));

        public void TryFail(string error)
        {
            Leased.TrySetResult(true);
            Completion.TrySetResult(new BridgeJobResult(null, error));
        }
    }

    private sealed record BridgeJobResult(DonutStats? Stats, string? Error);

    private sealed class BridgeHeartbeat
    {
        public string ClientVersion { get; set; } = string.Empty;
        public string ServerAddress { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public DateTimeOffset ReceivedAt { get; set; }
    }

    private sealed class BridgeFailPayload
    {
        public string Error { get; set; } = string.Empty;
        public Dictionary<string, List<string>>? RawGuiText { get; set; }
    }
}

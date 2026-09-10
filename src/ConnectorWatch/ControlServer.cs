using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ConnectorWatch;

/// <summary>
/// Current-user control endpoint for one ConnectorWatch data directory.
/// Commands are one newline-delimited JSON request per pipe connection.
/// </summary>
public sealed class ControlServer : IDisposable, IAsyncDisposable
{
    const int MaxRequestBytes = 16 * 1024;
    static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(ControlProtocol.LeaseSeconds);
    static readonly long LeaseTtlTicks = Math.Max(1L,
        (long)(Stopwatch.Frequency * LeaseTtl.TotalSeconds));
    readonly string dataDirectory;
    readonly string pipeName;
    readonly CancellationTokenSource serverStop;
    readonly Action requestStop;
    readonly object leaseGate = new();
    readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string? leaseClient;
    long leaseUntilTicks;
    Task? run;
    int disposed;

    /// <summary>
    /// Handles identity-bound operator commands that belong to the daemon's
    /// runtime state. The callback is invoked outside the lease lock so a
    /// persistence operation cannot block lease expiry or hello/live traffic.
    /// </summary>
    public Func<ControlRequest, ControlCommandResult?>? ReferenceCommand { get; set; }

    /// <summary>Handles explicit incident lifecycle commands separately from
    /// reference acceptance so acknowledgement cannot alter the reference.</summary>
    public Func<ControlRequest, ControlCommandResult?>? IncidentCommand { get; set; }
    public Func<ControlCommandResult>? ApprovalRefresh { get; set; }

    public ControlServer(string dataDirectory, CancellationTokenSource shutdown,
        string? instanceId = null, int? processId = null)
        : this(dataDirectory, shutdown.Token, shutdown.Cancel, instanceId, processId)
    {
    }

    public ControlServer(string dataDirectory, CancellationToken stopToken, Action? requestStop = null,
        string? instanceId = null, int? processId = null)
    {
        this.dataDirectory = ControlEndpoint.NormalizeDataDirectory(dataDirectory);
        pipeName = ControlEndpoint.Name(this.dataDirectory);
        this.requestStop = requestStop ?? (() => { });
        serverStop = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        InstanceId = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;
        ProcessId = processId ?? Environment.ProcessId;
    }

    public string DataDirectory => dataDirectory;
    public string PipeName => pipeName;
    public string InstanceId { get; }
    public int ProcessId { get; }
    public Task Completion => run ?? Task.CompletedTask;
    public Task Ready => ready.Task;
    public static TimeSpan LeaseDuration => LeaseTtl;
    public bool StopRequested => Volatile.Read(ref stopCommandReceived) != 0;

    /// <summary>True while a valid GUI lease has not expired.</summary>
    public bool AlertsSuppressed
    {
        get
        {
            lock (leaseGate)
            {
                ExpireLeaseLocked(Stopwatch.GetTimestamp());
                return leaseClient is not null;
            }
        }
    }

    public bool NotificationLeaseActive => AlertsSuppressed;

    public void Start()
    {
        if (Interlocked.CompareExchange(ref runStarted, 1, 0) != 0)
            throw new InvalidOperationException("The control server has already been started.");
        run = RunAsync(serverStop.Token);
    }

    int runStarted;
    int stopCommandReceived;
    readonly object liveGate = new();
    readonly Queue<string> liveRows = new();
    int liveChars;
    string? liveStatus;

    public void Publish(string status, string row)
    {
        lock (liveGate)
        {
            liveStatus = status;
            liveRows.Enqueue(row); liveChars += row.Length;
            while (liveRows.Count > 512 || liveChars > 512 * 1024)
                liveChars -= liveRows.Dequeue().Length;
        }
    }

    LiveTelemetry? ReadLive()
    {
        lock (liveGate) return liveStatus is null ? null : new(liveStatus, HybridStorage.Header, liveRows.ToArray());
    }

    /// <summary>
    /// Handles a request after validating its command and client identity. The
    /// response always carries the daemon identity, including for rejected input.
    /// </summary>
    public ControlResponse HandleRequest(ControlRequest? request)
    {
        bool ok = false;
        bool stopRequested = false;
        bool referenceCommandRequested = false;
        bool incidentCommandRequested = false;
        bool identityMatches = false;
        ControlCommandResult? commandResult = null;
        if (request is not null &&
            !string.IsNullOrWhiteSpace(request.Command) &&
            !string.IsNullOrWhiteSpace(request.ClientId) &&
            request.ClientId.Length <= 256)
        {
            string command = request.Command.Trim().ToLowerInvariant();
            var now = Stopwatch.GetTimestamp();
            lock (leaseGate)
            {
                ExpireLeaseLocked(now);
                // A GUI first discovers the daemon instance through hello. All
                // mutating commands must echo that identity so a delayed stop
                // cannot affect a replacement daemon using the same endpoint.
                identityMatches = string.Equals(request.ExpectedInstanceId, InstanceId, StringComparison.Ordinal);
                ok = command switch
                {
                    "hello" => true,
                    "live" when identityMatches => true,
                    "lease" when identityMatches => GrantLeaseLocked(request.ClientId, now),
                    "release" when identityMatches => ReleaseLeaseLocked(request.ClientId),
                    "stop" when identityMatches => true,
                    "refresh-driver-approvals" when identityMatches => true,
                    "accept-reference" when identityMatches => true,
                    "migrate-reference" when identityMatches => true,
                    "archive-reference" when identityMatches => true,
                    "acknowledge-incident" when identityMatches &&
                        !string.IsNullOrWhiteSpace(request.IncidentId) => true,
                    "resolve-incident" when identityMatches &&
                        !string.IsNullOrWhiteSpace(request.IncidentId) => true,
                    _ => false,
                };
                referenceCommandRequested = identityMatches && command is
                    ("accept-reference" or "migrate-reference" or "archive-reference");
                incidentCommandRequested = identityMatches && command is
                    ("acknowledge-incident" or "resolve-incident");
                stopRequested = ok && command == "stop";
            }

            if (referenceCommandRequested)
            {
                try
                {
                    commandResult = ReferenceCommand?.Invoke(request) ??
                        new ControlCommandResult(false, "Reference operator commands are unavailable during startup.");
                    ok = commandResult.Ok;
                }
                catch (Exception ex)
                {
                    commandResult = new ControlCommandResult(false, ex.Message);
                    ok = false;
                }
            }

            if (identityMatches && command == "refresh-driver-approvals")
            {
                try { commandResult = ApprovalRefresh?.Invoke() ?? new(false, "Driver approvals are not ready yet."); }
                catch (Exception ex) { commandResult = new(false, ex.Message); }
                ok = commandResult.Ok;
            }

            if (incidentCommandRequested)
            {
                if (string.IsNullOrWhiteSpace(request.IncidentId))
                {
                    commandResult = new ControlCommandResult(false,
                        "incident_id is required for incident commands.");
                    ok = false;
                }
                else
                {
                    try
                    {
                        commandResult = IncidentCommand?.Invoke(request) ??
                            new ControlCommandResult(false,
                                "Incident operator commands are unavailable during startup.");
                        ok = commandResult.Ok;
                    }
                    catch (Exception ex)
                    {
                        commandResult = new ControlCommandResult(false, ex.Message,
                            IncidentId: request.IncidentId);
                        ok = false;
                    }
                }
            }

            if (stopRequested)
            {
                Volatile.Write(ref stopCommandReceived, 1);
                requestStop();
            }
        }

        return new ControlResponse(ControlProtocol.Version, ProcessId, dataDirectory, InstanceId, ok,
            ok && request is not null && string.Equals(request.Command.Trim(), "live", StringComparison.OrdinalIgnoreCase) ? ReadLive() : null,
            commandResult?.Detail,
            commandResult?.ReferenceState,
            commandResult?.IncidentId,
            commandResult?.IncidentState);
    }

    bool GrantLeaseLocked(string clientId, long now)
    {
        // Keep presentation ownership exclusive until the current owner releases
        // or its monotonic five-second lease expires. The endpoint is
        // current-user-only, but exclusivity still prevents two GUI instances
        // from racing notification ownership.
        if (leaseClient is not null && !string.Equals(leaseClient, clientId, StringComparison.Ordinal))
            return false;
        leaseClient = clientId;
        leaseUntilTicks = now + LeaseTtlTicks;
        return true;
    }

    bool ReleaseLeaseLocked(string clientId)
    {
        if (leaseClient is null) return true;
        if (!string.Equals(leaseClient, clientId, StringComparison.Ordinal)) return false;
        leaseClient = null;
        leaseUntilTicks = 0;
        return true;
    }

    void ExpireLeaseLocked(long now)
    {
        if (leaseClient is not null && now >= leaseUntilTicks)
        {
            leaseClient = null;
            leaseUntilTicks = 0;
        }
    }

    async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var pipe = CreatePipe(pipeName);
                ready.TrySetResult(true);
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // A client that connects and sends nothing must not hold the
                    // single control instance indefinitely.
                }

                try
                {
                    await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // A client that disappears mid-request must not stop the
                    // sampling process or the endpoint.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Per-connection timeout; continue accepting the next GUI
                    // heartbeat while the daemon keeps sampling.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            Console.Error.WriteLine("ConnectorWatch control endpoint stopped: " + ex.Message);
        }
        finally
        {
            ready.TrySetResult(false);
        }
    }

    static NamedPipeServerStream CreatePipe(string name)
    {
        var options = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows()) options |= PipeOptions.CurrentUserOnly;
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, options, 16 * 1024, 16 * 1024);
    }

    async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        string? line = await ReadLineBoundedAsync(pipe, timeout.Token).ConfigureAwait(false);
        if (line is null) return;
        ControlProtocol.TryParseRequest(line ?? "", out var request);
        var response = HandleRequest(request);
        // Stop cancels the sampling token in HandleRequest. Give its acknowledgement
        // an independent, bounded write window before closing the endpoint.
        using var writeTimeout = new CancellationTokenSource(ConnectionTimeout);
        await writer.WriteLineAsync(ControlProtocol.Serialize(response).AsMemory(), writeTimeout.Token).ConfigureAwait(false);

        // The response is flushed before cancellation closes the accept loop, so
        // a stop command remains observable to the GUI.
        if (request is not null && string.Equals(request.Command.Trim(), "stop", StringComparison.OrdinalIgnoreCase) && response.Ok)
            serverStop.Cancel();
    }

    static async Task<string?> ReadLineBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var chunk = new byte[1024];
        using var line = new MemoryStream(capacity: 256);
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return line.Length == 0 ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, checked((int)line.Length));
            int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            int take = newline >= 0 ? newline : read;
            if (line.Length + take > MaxRequestBytes)
                return string.Empty;
            line.Write(chunk, 0, take);
            if (newline >= 0)
            {
                var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, checked((int)line.Length));
                return text.EndsWith('\r') ? text[..^1] : text;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        serverStop.Cancel();
        try { run?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        serverStop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        serverStop.Cancel();
        if (run is not null)
        {
            try { await run.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        serverStop.Dispose();
    }
}

namespace ConnectorWatch;

/// <summary>Serializes native session ownership; catalog I/O never holds the acquisition lock.</summary>
public sealed class ApprovalRailSource : IElectricalSource, IDisposable
{
    private readonly object gate = new();
    private readonly DriverApprovalService approvals;
    private readonly Func<DriverIdentity> observe;
    private readonly Func<Func<DriverIdentity, DriverApprovalDecision>, IElectricalSource> create;
    private readonly bool developer;
    private readonly CancellationTokenSource ending = new();
    private IElectricalSource? reader;
    private Task? refresh;
    private bool disposed;

    public ApprovalRailSource(Config config, DriverApprovalService approvals)
        : this(config.GpuUuid, approvals, !config.ValidateDriverVersion,
            ObserveSessionIdentity(config.GpuUuid),
            approval => new DirectNvRails(config.GpuUuid, approval)) { }

    private static Func<DriverIdentity> ObserveSessionIdentity(string uuid)
    {
        bool first = true;
        return () =>
        {
            if (first) { var result = DirectNvRails.ObserveIdentity(uuid); first = false; return result; }
            return DirectNvRails.IdentityForDriver(DirectNvRails.ReadInstalledDriver(uuid));
        };
    }

    internal ApprovalRailSource(string uuid, DriverApprovalService approvals, bool developer,
        Func<DriverIdentity> observe,
        Func<Func<DriverIdentity, DriverApprovalDecision>, IElectricalSource> create)
    {
        this.approvals = approvals; this.developer = developer;
        this.observe = observe; this.create = create;
        Identity = observe();
        Description = DirectNvRails.Describe(uuid, Identity.DriverVersion);
        Decision = Decide(Identity);
        RequestRefresh();
    }

    public DriverIdentity Identity { get; }
    public string DriverVersion => Identity.DriverVersion;
    public string Description { get; }
    public bool TerminalOnFailure => true;
    public DriverApprovalDecision Decision { get; private set; }
    public object Diagnostics => new
    {
        state = Decision.State.ToString(), detail = Decision.Reason,
        driver_version = DriverVersion, catalog_revision = Decision.CatalogRevision,
        expires_utc = Decision.Expires, unvalidated = Decision.IsUnvalidated,
        last_checked_utc = approvals.LastCheckedUtc, last_error = approvals.LastError,
    };

    private DriverApprovalDecision Decide(DriverIdentity identity) => approvals.Decide(identity,
        DirectNvRails.ReaderProfile, DirectNvRails.ReaderVersion, DirectNvRails.AppVersion, developer);

    public void RequestRefresh(bool force = false)
    {
        lock (gate)
        {
            if (disposed || refresh is { IsCompleted: false }) return;
            refresh = Task.Run(async () =>
            {
                try { await approvals.RefreshAsync(force, ending.Token); }
                catch (OperationCanceledException) when (ending.IsCancellationRequested) { }
            });
        }
    }

    public ElectricalSample ReadElectrical(DateTimeOffset now)
    {
        RequestRefresh();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Observing the public identity before every call also detects a
            // driver replacement while a catalog download is in flight.
            var current = observe();
            if (current != Identity)
            {
                DisposeReader();
                RequestRefresh(true);
                throw new DriverSessionChangedException();
            }
            Decision = Decide(current);
            if (!Decision.MayStart)
            {
                DisposeReader();
                throw new DriverApprovalPausedException(Decision);
            }
            try
            {
                reader ??= create(Decide);
                return reader.ReadElectrical(now);
            }
            catch (DriverApprovalException)
            {
                DisposeReader();
                Decision = Decide(current);
                throw new DriverApprovalPausedException(Decision);
            }
        }
    }

    public Voltage Read(DateTimeOffset now) => ReadElectrical(now).ToLegacyVoltage();
    private void DisposeReader() { (reader as IDisposable)?.Dispose(); reader = null; }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; ending.Cancel(); DisposeReader();
        }
        try { refresh?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        ending.Dispose();
    }
}

public sealed class DriverSessionChangedException : Exception
{
    public DriverSessionChangedException() : base("The installed driver changed; restarting the monitoring session and checking approval.") { }
}

public sealed class DriverApprovalPausedException : Exception
{
    public DriverApprovalPausedException(DriverApprovalDecision decision) : base(decision.Reason) => Decision = decision;
    public DriverApprovalDecision Decision { get; }
    public string Status => Decision.State switch
    {
        DriverApprovalState.NotListed => "DRIVER_AWAITING_APPROVAL",
        DriverApprovalState.Revoked => "DRIVER_REVOKED",
        DriverApprovalState.RequiresNewerReader or DriverApprovalState.RequiresNewerApp => "APP_UPDATE_REQUIRED",
        _ => "DRIVER_APPROVAL_UNAVAILABLE",
    };
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>Every decision is explicit; the default policy is BLOCKED.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MitigationDecision
{
    NO_OP,
    REDUCE,
    BLOCKED,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MitigationStage
{
    NONE,
    MITIGATION_REQUESTED,
    MITIGATION_ACCEPTED,
    MITIGATION_VERIFIED,
    MITIGATION_FAILED,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MitigationLoadVerification
{
    NOT_OBSERVED,
    REDUCTION_OBSERVED,
    UNOBSERVABLE_ALREADY_LOW,
    UNAVAILABLE,
    NO_REDUCTION,
    TIMED_OUT,
}

/// <summary>Stable fail-closed reasons exposed to an operator or audit log.</summary>
public static class MitigationBlockReason
{
    public const string Disabled = "DISABLED";
    public const string NoLatchedIncident = "NO_LATCHED_INCIDENT";
    public const string IncidentAlreadyHandled = "INCIDENT_ALREADY_HANDLED";
    public const string DeviceUnavailable = "DEVICE_UNAVAILABLE";
    public const string DeviceStale = "DEVICE_STALE";
    public const string Unsupported = "UNSUPPORTED";
    public const string IdentityMismatch = "IDENTITY_MISMATCH";
    public const string LeaseMissing = "LEASE_MISSING";
    public const string LeaseExpired = "LEASE_EXPIRED";
    public const string RestartRearmRequired = "RESTART_REARM_REQUIRED";
    public const string InvalidMonotonicTime = "INVALID_MONOTONIC_TIME";
    public const string LimitUnavailable = "LIMIT_UNAVAILABLE";
    public const string FloorUnavailable = "FLOOR_UNAVAILABLE";
    public const string TargetInvalid = "TARGET_INVALID";
    public const string TargetBelowFloor = "TARGET_BELOW_CONFIGURED_FLOOR";
    public const string TargetNotLower = "TARGET_NOT_LOWER_THAN_CURRENT";
    public const string ConcurrentChange = "CONCURRENT_CHANGE";
    public const string AuditNotDurable = "AUDIT_NOT_DURABLE";
    public const string WriteFailed = "WRITE_FAILED";
    public const string WriteTimeout = "WRITE_TIMEOUT";
    public const string ReadbackFailed = "READBACK_FAILED";
    public const string NoIncrease = "NO_INCREASE_ALLOWED";
    public const string UnauthorizedCaller = "UNAUTHORIZED_CALLER";
    public const string LoadUnobservable = "LOAD_UNOBSERVABLE_ALREADY_LOW";
    public const string LoadUnavailable = "LOAD_OBSERVATION_UNAVAILABLE";
    public const string LoadNotReduced = "LOAD_NOT_REDUCED";
    public const string ObservationTimeout = "LOAD_OBSERVATION_TIMEOUT";
}

/// <summary>Input to the optional mitigation policy.  Values come from an
/// injected adapter or a persisted incident owner; no native call is made by
/// this file.</summary>
public sealed record MitigationContext(
    string Identity,
    string IncidentId,
    bool IncidentLatched,
    bool DeviceAvailable,
    bool DeviceFresh,
    bool DeviceSupported,
    double? CurrentLimitWatts,
    double? ConfiguredFloorWatts,
    double? RequestedTargetWatts,
    DateTimeOffset TimestampUtc,
    double MonotonicSeconds,
    string? LeaseToken = null,
    string Detail = "",
    string CallerIdentity = "",
    string AuthorizationContext = "")
{
    public DateTimeOffset Utc => TimestampUtc.ToUniversalTime();
}

/// <summary>Identity-bound, monotonic lease.  A restored policy always needs a
/// new lease, even if this value was present in an old state file.</summary>
public sealed record MitigationLease(
    string Token,
    string Identity,
    double IssuedAtMonotonicSeconds,
    double ExpiresAtMonotonicSeconds)
{
    public bool IsActive(string identity, string? token, double monotonicSeconds) =>
        !string.IsNullOrWhiteSpace(token) &&
        string.Equals(Identity, identity, StringComparison.Ordinal) &&
        string.Equals(Token, token, StringComparison.Ordinal) &&
        double.IsFinite(monotonicSeconds) &&
        monotonicSeconds >= IssuedAtMonotonicSeconds &&
        monotonicSeconds < ExpiresAtMonotonicSeconds;
}

/// <summary>Persisted policy state.  It contains no hardware handle or write
/// delegate, and is safe to discard or archive after an incident.</summary>
public sealed record MitigationState(
    int SchemaVersion = 1,
    string Identity = "",
    bool RequiresRearm = true,
    MitigationLease? Lease = null,
    IReadOnlyList<string>? AppliedIncidentIds = null,
    MitigationDecision LastDecision = MitigationDecision.NO_OP,
    string LastReason = "",
    DateTimeOffset? LastDecisionUtc = null,
    MitigationStage LastStage = MitigationStage.NONE,
    bool LastIncidentLatched = false,
    IReadOnlyList<string>? AttemptedIncidentIds = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> Applied => AppliedIncidentIds ?? Array.Empty<string>();
    [JsonIgnore]
    public IReadOnlyList<string> Attempted => AttemptedIncidentIds ?? Array.Empty<string>();

    public MitigationState Normalize()
    {
        if (SchemaVersion != 1)
            throw new NotSupportedException($"Unsupported mitigation state schema {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(Identity))
            throw new FormatException("Mitigation state requires an identity.");
        if (Applied.Any(string.IsNullOrWhiteSpace))
            throw new FormatException("Mitigation state contains an empty incident id.");
        if (Applied.Count > 64)
            throw new FormatException("Mitigation state contains too many incident ids.");
        if (Attempted.Count > 64 || Attempted.Any(string.IsNullOrWhiteSpace))
            throw new FormatException("Mitigation state contains invalid attempted incident ids.");
        if (Lease is not null &&
            (string.IsNullOrWhiteSpace(Lease.Token) || string.IsNullOrWhiteSpace(Lease.Identity) ||
             !string.Equals(Lease.Identity.Trim(), Identity.Trim(), StringComparison.Ordinal) ||
             !double.IsFinite(Lease.IssuedAtMonotonicSeconds) ||
             !double.IsFinite(Lease.ExpiresAtMonotonicSeconds) ||
             Lease.ExpiresAtMonotonicSeconds <= Lease.IssuedAtMonotonicSeconds))
            throw new FormatException("Mitigation state contains an invalid lease.");
        return this with
        {
            Identity = Identity.Trim(),
            AppliedIncidentIds = Applied.Distinct(StringComparer.Ordinal).ToArray(),
            AttemptedIncidentIds = Attempted.Distinct(StringComparer.Ordinal).ToArray(),
            LastReason = LastReason?.Trim() ?? "",
        };
    }
}

/// <summary>Machine-readable policy output.  <see cref="NoSafetyCertification"
/// /> is permanently true: derating policy is an operational gate, not a
/// connector-safety determination.</summary>
public sealed record MitigationResult(
    MitigationDecision Decision,
    string Reason,
    string Identity,
    string IncidentId,
    bool LeaseValid,
    bool ReadOnlyPolicyGate,
    bool NoSafetyCertification,
    double? CurrentLimitWatts,
    double? ConfiguredFloorWatts,
    double? RequestedTargetWatts,
    double? AppliedTargetWatts,
    DateTimeOffset TimestampUtc,
    string Detail)
{
    public MitigationStage Stage { get; init; } = MitigationStage.NONE;
    public IReadOnlyList<MitigationStage> Stages { get; init; } = Array.Empty<MitigationStage>();
    public MitigationLoadVerification LoadVerification { get; init; } = MitigationLoadVerification.NOT_OBSERVED;
    public double? LoadBeforeWatts { get; init; }
    public double? LoadAfterWatts { get; init; }
    public bool IncidentLatched { get; init; }
    public bool IsBlocked => Decision == MitigationDecision.BLOCKED;
    public bool IsReduction => Decision == MitigationDecision.REDUCE;

    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this,
        OptionalMitigationJson.Options(indented));
}

/// <summary>Read/apply/readback seam.  Production code must provide an
/// explicitly reviewed adapter; this roadmap gate intentionally provides no
/// NVML/NVAPI implementation.</summary>
public interface IPowerLimitMitigationAdapter
{
    MitigationDeviceRead Read();
    MitigationAdapterApply Apply(double targetWatts);
}

/// <summary>Mock-only trust boundary. A production implementation must authenticate
/// the caller outside the request payload; strings alone are not credentials.</summary>
public interface IMitigationAuthorizationGate
{
    bool Authorize(string callerIdentity, string authorizationContext, string deviceIdentity);
}

/// <summary>Read-only observation seam. Implementations must return within the
/// supplied duration and sample budget. No production observer is provided.</summary>
public interface IMitigationLoadObserver
{
    MitigationLoadObservation Observe(double maximumSeconds, int maximumSamples);
}

public sealed record MitigationLoadSample(string Identity, double ElapsedSeconds,
    double? BoardPowerWatts, bool Fresh = true);

public sealed record MitigationLoadObservation(IReadOnlyList<MitigationLoadSample> Samples,
    double ElapsedSeconds, bool TimedOut = false);

public sealed record MitigationDeviceRead(
    string Identity,
    bool Supported,
    bool Available,
    bool Fresh,
    double? CurrentLimitWatts,
    double? BoardPowerWatts = null,
    string Detail = "");

public sealed record MitigationAdapterApply(
    bool Succeeded,
    string Detail = "",
    double? ReportedTargetWatts = null);

/// <summary>Pre-write audit boundary.  Returning false must prevent Apply.</summary>
public interface IMitigationAuditGate
{
    bool RecordDurably(MitigationAuditEntry entry);
}

public sealed record MitigationAuditEntry(
    string Identity,
    string IncidentId,
    double PreviousLimitWatts,
    double TargetLimitWatts,
    double ConfiguredFloorWatts,
    DateTimeOffset TimestampUtc,
    double MonotonicSeconds,
    string Reason,
    string CallerIdentity = "",
    MitigationStage Stage = MitigationStage.MITIGATION_REQUESTED);

public sealed record OptionalMitigationOptions(
    bool Enabled = false,
    double LeaseMaximumSeconds = 300,
    double ConcurrentToleranceWatts = .5,
    double ReadbackToleranceWatts = 1,
    double ObservationMaximumSeconds = 5,
    int ObservationMaximumSamples = 16,
    double MinimumLoadReductionWatts = 5)
{
    public void Validate()
    {
        if (!double.IsFinite(LeaseMaximumSeconds) || LeaseMaximumSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(LeaseMaximumSeconds));
        if (!double.IsFinite(ConcurrentToleranceWatts) || ConcurrentToleranceWatts < 0)
            throw new ArgumentOutOfRangeException(nameof(ConcurrentToleranceWatts));
        if (!double.IsFinite(ReadbackToleranceWatts) || ReadbackToleranceWatts < 0)
            throw new ArgumentOutOfRangeException(nameof(ReadbackToleranceWatts));
        if (!double.IsFinite(ObservationMaximumSeconds) || ObservationMaximumSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(ObservationMaximumSeconds));
        if (ObservationMaximumSamples < 2 || ObservationMaximumSamples > 1024)
            throw new ArgumentOutOfRangeException(nameof(ObservationMaximumSamples));
        if (!double.IsFinite(MinimumLoadReductionWatts) || MinimumLoadReductionWatts <= 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumLoadReductionWatts));
    }
}

public static class OptionalMitigationJson
{
    public static JsonSerializerOptions Options(bool indented = true) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// Disabled-by-default, downward-only mitigation policy.  It decides and
/// gates an injected adapter but has no native or process-privilege code.
/// Every write requires a fresh second read and a durable audit record first.
/// </summary>
public sealed class OptionalMitigationPolicy
{
    public const int CurrentSchemaVersion = 1;
    public const bool EnabledByDefault = false;

    readonly OptionalMitigationOptions options;
    readonly string identity;
    readonly IMitigationAuthorizationGate? authorization;
    readonly object transactionGate = new();
    bool applying;
    MitigationState state;

    public OptionalMitigationPolicy(OptionalMitigationOptions? options = null,
        string identity = "unspecified", MitigationState? persisted = null,
        IMitigationAuthorizationGate? authorization = null)
    {
        this.options = options ?? new OptionalMitigationOptions();
        this.options.Validate();
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("Mitigation identity is required.", nameof(identity));
        this.identity = identity.Trim();
        this.authorization = authorization;
        state = (persisted ?? new MitigationState(Identity: this.identity)).Normalize();
        if (!string.Equals(state.Identity, this.identity, StringComparison.Ordinal))
            throw new ArgumentException("Mitigation state identity does not match.", nameof(persisted));
    }

    public OptionalMitigationOptions Options => options;
    public MitigationState State { get { lock (transactionGate) return state; } }
    public string Identity => identity;

    /// <summary>Arm a finite identity-bound lease.  This is the explicit
    /// operator opt-in; it never changes a device limit.</summary>
    public MitigationLease ArmLease(string token, double nowMonotonicSeconds,
        double durationSeconds)
    {
        lock (transactionGate)
            return ArmLeaseCore(token, nowMonotonicSeconds, durationSeconds);
    }

    MitigationLease ArmLeaseCore(string token, double nowMonotonicSeconds,
        double durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException(
            "An explicit lease token is required.", nameof(token));
        if (!double.IsFinite(nowMonotonicSeconds) || nowMonotonicSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(nowMonotonicSeconds));
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 ||
            durationSeconds > options.LeaseMaximumSeconds)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        var lease = new MitigationLease(token.Trim(), identity, nowMonotonicSeconds,
            nowMonotonicSeconds + durationSeconds);
        state = state with { RequiresRearm = false, Lease = lease };
        return lease;
    }

    public void RevokeLease()
    {
        lock (transactionGate)
            state = state with { Lease = null, RequiresRearm = true };
    }

    public string SerializeState()
    {
        lock (transactionGate)
            return JsonSerializer.Serialize(state, OptionalMitigationJson.Options(true));
    }

    public static OptionalMitigationPolicy Restore(string json,
        OptionalMitigationOptions? options = null, string? identity = null,
        IMitigationAuthorizationGate? authorization = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Mitigation state is empty.");
        var saved = JsonSerializer.Deserialize<MitigationState>(json,
            OptionalMitigationJson.Options(false))?.Normalize()
            ?? throw new FormatException("Mitigation state is invalid.");
        var effectiveIdentity = string.IsNullOrWhiteSpace(identity) ? saved.Identity : identity.Trim();
        // A process restart invalidates the previous opt-in lease.  Keep
        // historical incident ids, but require a fresh explicit arm operation.
        saved = saved with { RequiresRearm = true, Lease = null };
        return new OptionalMitigationPolicy(options, effectiveIdentity, saved, authorization);
    }

    public MitigationResult Evaluate(MitigationContext context)
    {
        lock (transactionGate) return EvaluateCore(context);
    }

    MitigationResult EvaluateCore(MitigationContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var common = (context.Identity?.Trim() ?? "",
            context.IncidentId?.Trim() ?? "", context.Utc);
        if (!options.Enabled)
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.Disabled,
                "Optional mitigation is disabled by default.");
        if (!string.Equals(common.Item1, identity, StringComparison.Ordinal))
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.IdentityMismatch,
                "Incident identity does not match the policy identity.");
        if (string.IsNullOrWhiteSpace(common.Item2))
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.IdentityMismatch,
                "A non-empty incident id is required.");
        if (!context.IncidentLatched)
            return NoOp(common.Item1, common.Item2, context, false,
                MitigationBlockReason.NoLatchedIncident, "No latched incident requires mitigation.");
        if (state.Applied.Contains(common.Item2, StringComparer.Ordinal) ||
            state.Attempted.Contains(common.Item2, StringComparer.Ordinal))
            return NoOp(common.Item1, common.Item2, context, true,
                MitigationBlockReason.IncidentAlreadyHandled,
                "This incident was already handled; repeated application is suppressed.");
        if (!context.DeviceSupported)
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.Unsupported,
                "The injected device adapter does not support mitigation.");
        if (!context.DeviceAvailable)
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.DeviceUnavailable,
                "The device read is unavailable.");
        if (!context.DeviceFresh)
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.DeviceStale,
                "The device limit telemetry is stale.");
        if (!double.IsFinite(context.MonotonicSeconds) || context.MonotonicSeconds < 0)
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.InvalidMonotonicTime,
                "A finite monotonic timestamp is required for lease expiry.");
        if (state.RequiresRearm || state.Lease is null)
            return Block(common.Item1, common.Item2, context,
                state.RequiresRearm ? MitigationBlockReason.RestartRearmRequired : MitigationBlockReason.LeaseMissing,
                "An explicit, identity-bound mitigation lease is required.");
        if (context.MonotonicSeconds >= state.Lease.ExpiresAtMonotonicSeconds)
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.LeaseExpired,
                "The mitigation lease expired before evaluation.");
        if (!state.Lease.IsActive(identity, context.LeaseToken, context.MonotonicSeconds))
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.LeaseMissing,
                "The supplied lease token or identity is invalid.");
        try
        {
            if (authorization is null || string.IsNullOrWhiteSpace(context.CallerIdentity) ||
                string.IsNullOrWhiteSpace(context.AuthorizationContext) ||
                !authorization.Authorize(context.CallerIdentity, context.AuthorizationContext, identity))
                return Block(common.Item1, common.Item2, context, MitigationBlockReason.UnauthorizedCaller,
                    "The caller or authorization context is not authorized for this device.");
        }
        catch (Exception)
        {
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.UnauthorizedCaller,
                "Caller authorization could not be verified.");
        }
        if (!FinitePositive(context.CurrentLimitWatts))
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.LimitUnavailable,
                "Current observed power limit is unavailable.");
        if (!FiniteNonNegative(context.ConfiguredFloorWatts))
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.FloorUnavailable,
                "Configured mitigation floor is unavailable.");
        if (!FinitePositive(context.RequestedTargetWatts))
            return NoOp(common.Item1, common.Item2, context, true,
                MitigationBlockReason.TargetInvalid, "No finite downward target was requested.");
        if (context.RequestedTargetWatts!.Value < context.ConfiguredFloorWatts!.Value)
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.TargetBelowFloor,
                "Requested target is below the configured floor.");
        if (context.RequestedTargetWatts.Value >= context.CurrentLimitWatts!.Value)
            return Block(common.Item1, common.Item2, context, MitigationBlockReason.TargetNotLower,
                "A mitigation target must be strictly lower than the current limit.");
        return new(MitigationDecision.REDUCE, "READY", common.Item1, common.Item2, true,
            ReadOnlyPolicyGate: true, NoSafetyCertification: true, context.CurrentLimitWatts,
            context.ConfiguredFloorWatts, context.RequestedTargetWatts, null, common.Item3,
            "Target passed the downward-only policy gates; adapter and durable audit checks remain.");
    }

    /// <summary>
    /// Performs the gated adapter transaction.  The adapter is called only
    /// after the policy passes, an unchanged second read, and a durable audit.
    /// </summary>
    public MitigationResult Apply(MitigationContext context,
        IPowerLimitMitigationAdapter adapter, IMitigationAuditGate audit,
        double? monotonicAtWrite = null)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        // Reject contention instead of queuing a request whose telemetry and
        // lease timestamp could be stale by the time the transaction starts.
        if (!System.Threading.Monitor.TryEnter(transactionGate))
            return Block(context.Identity, context.IncidentId, context,
                MitigationBlockReason.ConcurrentChange, "Another mitigation operation is in progress.");
        try
        {
            // Monitor is reentrant: an injected audit/adapter callback must
            // not start a nested transaction before incident state is saved.
            if (applying)
                return Block(context.Identity, context.IncidentId, context,
                    MitigationBlockReason.ConcurrentChange, "Another mitigation operation is in progress.");
            applying = true;
            try
            {
                var previousState = state;
                var stages = new List<MitigationStage> { MitigationStage.MITIGATION_REQUESTED };
                state = state with { LastStage = MitigationStage.MITIGATION_REQUESTED,
                    LastIncidentLatched = state.LastIncidentLatched || context.IncidentLatched };
                var result = ApplyCore(context, adapter, audit, monotonicAtWrite, stages);
                if (result.Decision == MitigationDecision.NO_OP)
                {
                    state = previousState;
                    return result with { IncidentLatched = state.LastIncidentLatched || context.IncidentLatched };
                }
                var finalStage = result.IsBlocked ? MitigationStage.MITIGATION_FAILED : result.Stage;
                if (finalStage == MitigationStage.NONE) finalStage = MitigationStage.MITIGATION_REQUESTED;
                if (stages[^1] != finalStage) stages.Add(finalStage);
                state = state with { LastStage = finalStage, LastDecision = result.Decision,
                    LastReason = result.Reason, LastDecisionUtc = context.Utc };
                return result with { Stage = finalStage, Stages = stages.AsReadOnly(),
                    IncidentLatched = state.LastIncidentLatched };
            }
            finally { applying = false; }
        }
        finally { System.Threading.Monitor.Exit(transactionGate); }
    }

    MitigationResult ApplyCore(MitigationContext context,
        IPowerLimitMitigationAdapter adapter, IMitigationAuditGate audit,
        double? monotonicAtWrite, List<MitigationStage> stages)
    {
        var decision = Evaluate(context);
        if (decision.Decision != MitigationDecision.REDUCE) return decision;
        if (adapter is null)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.DeviceUnavailable, "No mitigation adapter was supplied.");
        if (audit is null)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.AuditNotDurable, "No durable audit gate was supplied.");
        if (monotonicAtWrite is double writeTime &&
            (!double.IsFinite(writeTime) || writeTime < context.MonotonicSeconds))
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.InvalidMonotonicTime,
                "The pre-write monotonic timestamp is invalid or moved backwards.");

        MitigationDeviceRead first;
        try { first = adapter.Read(); }
        catch (Exception ex)
        {
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.DeviceUnavailable, "Initial adapter read failed: " + ex.Message);
        }
        var checkedRead = ValidateRead(first, decision.CurrentLimitWatts!.Value,
            decision.ConfiguredFloorWatts!.Value);
        if (checkedRead is not null) return Block(decision.Identity, decision.IncidentId,
            context, checkedRead, "Initial adapter read failed a mitigation gate.");

        MitigationDeviceRead beforeWrite;
        try { beforeWrite = adapter.Read(); }
        catch (Exception ex)
        {
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.DeviceUnavailable, "Pre-write adapter read failed: " + ex.Message);
        }
        checkedRead = ValidateRead(beforeWrite, first.CurrentLimitWatts!.Value,
            decision.ConfiguredFloorWatts!.Value);
        if (checkedRead is not null)
            return Block(decision.Identity, decision.IncidentId, context,
                checkedRead == MitigationBlockReason.DeviceUnavailable ? checkedRead : MitigationBlockReason.ConcurrentChange,
                "The current limit changed before the write; no write was attempted.");

        double target = decision.RequestedTargetWatts!.Value;
        if (target < decision.ConfiguredFloorWatts!.Value &&
            target >= 0) return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.TargetBelowFloor, "Target failed the floor check before the write.");
        if (target >= beforeWrite.CurrentLimitWatts!.Value)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.TargetNotLower, "Target is no longer below the current limit.");
        if (state.RequiresRearm || state.Lease is null ||
            !state.Lease.IsActive(identity, context.LeaseToken,
                monotonicAtWrite ?? context.MonotonicSeconds))
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.LeaseExpired,
                "The opt-in lease expired before the write; no write was attempted.");

        var auditEntry = new MitigationAuditEntry(identity, decision.IncidentId,
            beforeWrite.CurrentLimitWatts.Value, target, decision.ConfiguredFloorWatts!.Value,
            context.Utc, context.MonotonicSeconds, "optional-downward-only-mitigation", context.CallerIdentity);
        bool durable;
        try { durable = audit.RecordDurably(auditEntry); }
        catch (Exception ex)
        {
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.AuditNotDurable, "Durable audit gate failed: " + ex.Message);
        }
        if (!durable)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.AuditNotDurable, "Durable audit gate rejected the write.");
        if (state.RequiresRearm || state.Lease is null)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.RestartRearmRequired,
                "The opt-in lease was revoked at the write boundary; no write was attempted.");
        if (!state.Lease.IsActive(identity, context.LeaseToken,
            monotonicAtWrite ?? context.MonotonicSeconds))
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.LeaseExpired,
                "The opt-in lease expired at the write boundary; no write was attempted.");

        var finalAuthorization = EvaluateCore(context with
            { MonotonicSeconds = monotonicAtWrite ?? context.MonotonicSeconds });
        if (!finalAuthorization.IsReduction) return finalAuthorization;

        // An exception/denial/readback failure can leave the device changed.
        // Preserve the incident and suppress repeated writes even if verification fails.
        var attempted = state.Attempted.Append(decision.IncidentId).Distinct(StringComparer.Ordinal)
            .TakeLast(64).ToArray();
        state = state with { AttemptedIncidentIds = attempted };

        MitigationAdapterApply applied;
        try { applied = adapter.Apply(target); }
        catch (TimeoutException)
        {
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.WriteTimeout, "Adapter acceptance timed out; the incident remains latched and no retry is scheduled.");
        }
        catch (Exception ex)
        {
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.WriteFailed, "Adapter apply failed: " + ex.Message);
        }
        if (applied is null || !applied.Succeeded)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.WriteFailed, applied?.Detail ?? "Adapter returned no acceptance result.");

        stages.Add(MitigationStage.MITIGATION_ACCEPTED);
        state = state with { LastStage = MitigationStage.MITIGATION_ACCEPTED };

        MitigationDeviceRead after;
        try { after = adapter.Read(); }
        catch (Exception ex)
        {
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.ReadbackFailed, "Readback failed: " + ex.Message);
        }
        if (after is null || !after.Supported || !after.Available || !after.Fresh ||
            !string.Equals(after.Identity, identity, StringComparison.Ordinal) ||
            !FinitePositive(after.CurrentLimitWatts))
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.ReadbackFailed, "Readback is unavailable, stale, unsupported, or identity-mismatched.");
        if (after.CurrentLimitWatts!.Value < decision.ConfiguredFloorWatts!.Value)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.ReadbackFailed, "Adapter rounding placed the result below the configured floor.");
        if (after.CurrentLimitWatts!.Value >= beforeWrite.CurrentLimitWatts!.Value)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.NoIncrease, "Readback did not prove a strict downward change.");
        if (Math.Abs(after.CurrentLimitWatts.Value - target) > options.ReadbackToleranceWatts)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.ReadbackFailed, "Readback differs from the requested target beyond tolerance.");

        var ids = state.Applied.ToList();
        ids.Add(decision.IncidentId);
        while (ids.Count > 64) ids.RemoveAt(0);
        state = state with
        {
            AppliedIncidentIds = ids,
            LastDecision = MitigationDecision.REDUCE,
            LastReason = "APPLIED",
            LastDecisionUtc = context.Utc,
        };
        var accepted = decision with
        {
            Reason = "APPLIED",
            AppliedTargetWatts = after.CurrentLimitWatts,
            Stage = MitigationStage.MITIGATION_ACCEPTED,
            Detail = "Downward-only limit accepted and read back; electrical load verification remains.",
        };
        return VerifyLoad(accepted, beforeWrite.BoardPowerWatts, adapter as IMitigationLoadObserver);
    }

    MitigationResult VerifyLoad(MitigationResult accepted, double? loadBefore,
        IMitigationLoadObserver? observer)
    {
        MitigationResult Failure(string reason, MitigationLoadVerification verification, string detail) =>
            accepted with { Decision = MitigationDecision.BLOCKED, Reason = reason,
                Stage = MitigationStage.MITIGATION_FAILED, LoadVerification = verification,
                LoadBeforeWatts = loadBefore, Detail = detail };
        if (!FiniteNonNegative(loadBefore))
            return Failure(MitigationBlockReason.LoadUnavailable, MitigationLoadVerification.UNAVAILABLE,
                "Fresh pre-write electrical load is unavailable; limit readback does not verify load reduction.");
        if (loadBefore!.Value <= accepted.RequestedTargetWatts!.Value)
            return accepted with { Reason = MitigationBlockReason.LoadUnobservable,
                LoadVerification = MitigationLoadVerification.UNOBSERVABLE_ALREADY_LOW,
                LoadBeforeWatts = loadBefore,
                Detail = "Load was already below the requested cap; a mitigation-induced reduction is unobservable. Incident remains latched." };
        if (observer is null)
            return Failure(MitigationBlockReason.LoadUnavailable, MitigationLoadVerification.UNAVAILABLE,
                "No bounded electrical load observer was supplied.");
        MitigationLoadObservation observation;
        try { observation = observer.Observe(options.ObservationMaximumSeconds, options.ObservationMaximumSamples); }
        catch (TimeoutException)
        {
            return Failure(MitigationBlockReason.ObservationTimeout, MitigationLoadVerification.TIMED_OUT,
                "Electrical load observation timed out; incident remains latched.");
        }
        catch (Exception)
        {
            return Failure(MitigationBlockReason.LoadUnavailable, MitigationLoadVerification.UNAVAILABLE,
                "Electrical load observation failed; incident remains latched.");
        }
        if (observation is null || observation.Samples is null ||
            !double.IsFinite(observation.ElapsedSeconds) || observation.ElapsedSeconds < 0 ||
            observation.Samples.Count > options.ObservationMaximumSamples)
            return Failure(MitigationBlockReason.LoadUnavailable, MitigationLoadVerification.UNAVAILABLE,
                "The observation violated its sample or time contract.");
        if (observation.TimedOut || observation.ElapsedSeconds > options.ObservationMaximumSeconds)
            return Failure(MitigationBlockReason.ObservationTimeout, MitigationLoadVerification.TIMED_OUT,
                "Electrical load was not verified within the observation window.");
        double previousTime = 0;
        int consecutiveReduced = 0;
        double? lastLoad = null;
        foreach (var sample in observation.Samples)
        {
            if (sample is null || !sample.Fresh ||
                !string.Equals(sample.Identity, identity, StringComparison.Ordinal) ||
                !double.IsFinite(sample.ElapsedSeconds) || sample.ElapsedSeconds <= previousTime ||
                sample.ElapsedSeconds > observation.ElapsedSeconds || !FiniteNonNegative(sample.BoardPowerWatts))
                return Failure(MitigationBlockReason.LoadUnavailable, MitigationLoadVerification.UNAVAILABLE,
                    "Electrical load samples must be fresh, ordered, finite, and from the same device.");
            previousTime = sample.ElapsedSeconds;
            lastLoad = sample.BoardPowerWatts;
            consecutiveReduced = loadBefore.Value - sample.BoardPowerWatts!.Value >= options.MinimumLoadReductionWatts
                ? consecutiveReduced + 1 : 0;
        }
        if (consecutiveReduced < 2)
            return Failure(MitigationBlockReason.LoadNotReduced, MitigationLoadVerification.NO_REDUCTION,
                "The bounded observation did not end with two consecutive reduced electrical load samples.")
                with { LoadAfterWatts = lastLoad };
        return accepted with { Stage = MitigationStage.MITIGATION_VERIFIED,
            LoadVerification = MitigationLoadVerification.REDUCTION_OBSERVED,
            LoadBeforeWatts = loadBefore, LoadAfterWatts = lastLoad,
            Detail = "Limit readback and consecutive electrical load reductions verified; incident remains latched and no restore is scheduled." };
    }

    string? ValidateRead(MitigationDeviceRead read, double expectedCurrent, double floor)
    {
        if (read is null || !read.Supported) return MitigationBlockReason.Unsupported;
        if (!read.Available) return MitigationBlockReason.DeviceUnavailable;
        if (!read.Fresh) return MitigationBlockReason.DeviceStale;
        if (!string.Equals(read.Identity, identity, StringComparison.Ordinal))
            return MitigationBlockReason.IdentityMismatch;
        if (read.CurrentLimitWatts is not double current || !double.IsFinite(current) || current <= 0)
            return MitigationBlockReason.LimitUnavailable;
        if (Math.Abs(current - expectedCurrent) > options.ConcurrentToleranceWatts)
            return MitigationBlockReason.ConcurrentChange;
        if (current < floor) return MitigationBlockReason.ConcurrentChange;
        return null;
    }

    MitigationResult NoOp(string resultIdentity, string incidentId, MitigationContext context,
        bool leaseValid, string reason, string detail) => new(
        MitigationDecision.NO_OP, reason, resultIdentity, incidentId, leaseValid,
        ReadOnlyPolicyGate: true, NoSafetyCertification: true, context.CurrentLimitWatts,
        context.ConfiguredFloorWatts, context.RequestedTargetWatts, null, context.Utc, detail);

    MitigationResult Block(string resultIdentity, string incidentId, MitigationContext context,
        string reason, string detail) => new(
        MitigationDecision.BLOCKED, reason, resultIdentity, incidentId, false,
        ReadOnlyPolicyGate: true, NoSafetyCertification: true, context.CurrentLimitWatts,
        context.ConfiguredFloorWatts, context.RequestedTargetWatts, null, context.Utc, detail);

    static bool FinitePositive(double? value) => value is double number &&
        double.IsFinite(number) && number > 0;

    static bool FiniteNonNegative(double? value) => value is double number &&
        double.IsFinite(number) && number >= 0;
}

/// <summary>Small fake adapter/audit implementations for offline tests.  They
/// are intentionally kept here, rather than providing a real device adapter.</summary>
public sealed class FakePowerLimitMitigationAdapter : IPowerLimitMitigationAdapter, IMitigationLoadObserver
{
    public string Identity { get; set; } = "fixture";
    public bool Supported { get; set; } = true;
    public bool Available { get; set; } = true;
    public bool Fresh { get; set; } = true;
    public double CurrentLimitWatts { get; set; } = 450;
    public double? BoardPowerWatts { get; set; } = 450;
    public MitigationLoadObservation? Observation { get; set; }
    public bool ObservationTimesOut { get; set; }
    public bool ApplyTimesOut { get; set; }
    public int ObservationCount { get; private set; }
    public double? LastObservationMaximumSeconds { get; private set; }
    public int? LastObservationMaximumSamples { get; private set; }
    public double RoundingWatts { get; set; }
    public bool FailApply { get; set; }
    public bool FailReadback { get; set; }
    public bool IncreaseOnApply { get; set; }
    public int ReadCount { get; private set; }
    public int ApplyCount { get; private set; }
    public bool ConcurrentChangeOnSecondRead { get; set; }
    public double ConcurrentLimitWatts { get; set; } = 430;

    public MitigationDeviceRead Read()
    {
        ReadCount++;
        if (ConcurrentChangeOnSecondRead && ReadCount == 2)
            CurrentLimitWatts = ConcurrentLimitWatts;
        if (FailReadback && ApplyCount > 0) return new(Identity, Supported, Available,
            Fresh, null, BoardPowerWatts, "fixture readback failure");
        return new(Identity, Supported, Available, Fresh, CurrentLimitWatts,
            BoardPowerWatts, "fixture adapter");
    }

    public MitigationAdapterApply Apply(double targetWatts)
    {
        ApplyCount++;
        if (ApplyTimesOut) throw new TimeoutException("fixture apply timeout");
        if (FailApply) return new(false, "fixture write failure");
        CurrentLimitWatts = IncreaseOnApply ? CurrentLimitWatts + 5 : RoundingWatts > 0
            ? Math.Round(targetWatts / RoundingWatts) * RoundingWatts : targetWatts;
        return new(true, "fixture write", CurrentLimitWatts);
    }

    public MitigationLoadObservation Observe(double maximumSeconds, int maximumSamples)
    {
        ObservationCount++;
        LastObservationMaximumSeconds = maximumSeconds;
        LastObservationMaximumSamples = maximumSamples;
        if (ObservationTimesOut) throw new TimeoutException("fixture observation timeout");
        return Observation ?? new(new[]
        {
            new MitigationLoadSample(Identity, maximumSeconds / 2, CurrentLimitWatts),
            new MitigationLoadSample(Identity, maximumSeconds, CurrentLimitWatts),
        }, maximumSeconds);
    }
}

/// <summary>Offline fixture, not an authentication mechanism.</summary>
public sealed class FakeMitigationAuthorizationGate : IMitigationAuthorizationGate
{
    public string CallerIdentity { get; set; } = "fixture-operator";
    public string AuthorizationContext { get; set; } = "fixture-auth";
    public string DeviceIdentity { get; set; } = "fixture-gpu";
    public bool Allowed { get; set; } = true;

    public bool Authorize(string callerIdentity, string authorizationContext, string deviceIdentity) =>
        Allowed && string.Equals(callerIdentity, CallerIdentity, StringComparison.Ordinal) &&
        string.Equals(authorizationContext, AuthorizationContext, StringComparison.Ordinal) &&
        string.Equals(deviceIdentity, DeviceIdentity, StringComparison.Ordinal);
}

public sealed class FakeMitigationAuditGate : IMitigationAuditGate
{
    public bool Durable { get; set; } = true;
    public int CallCount { get; private set; }
    public MitigationAuditEntry? LastEntry { get; private set; }

    public bool RecordDurably(MitigationAuditEntry entry)
    {
        CallCount++;
        LastEntry = entry;
        return Durable;
    }
}

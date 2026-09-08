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
    public const string ReadbackFailed = "READBACK_FAILED";
    public const string NoIncrease = "NO_INCREASE_ALLOWED";
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
    string Detail = "")
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
    DateTimeOffset? LastDecisionUtc = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> Applied => AppliedIncidentIds ?? Array.Empty<string>();

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
    string Reason);

public sealed record OptionalMitigationOptions(
    bool Enabled = false,
    double LeaseMaximumSeconds = 300,
    double ConcurrentToleranceWatts = .5,
    double ReadbackToleranceWatts = 1)
{
    public void Validate()
    {
        if (!double.IsFinite(LeaseMaximumSeconds) || LeaseMaximumSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(LeaseMaximumSeconds));
        if (!double.IsFinite(ConcurrentToleranceWatts) || ConcurrentToleranceWatts < 0)
            throw new ArgumentOutOfRangeException(nameof(ConcurrentToleranceWatts));
        if (!double.IsFinite(ReadbackToleranceWatts) || ReadbackToleranceWatts < 0)
            throw new ArgumentOutOfRangeException(nameof(ReadbackToleranceWatts));
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
    MitigationState state;

    public OptionalMitigationPolicy(OptionalMitigationOptions? options = null,
        string identity = "unspecified", MitigationState? persisted = null)
    {
        this.options = options ?? new OptionalMitigationOptions();
        this.options.Validate();
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("Mitigation identity is required.", nameof(identity));
        this.identity = identity.Trim();
        state = (persisted ?? new MitigationState(Identity: this.identity)).Normalize();
        if (!string.Equals(state.Identity, this.identity, StringComparison.Ordinal))
            throw new ArgumentException("Mitigation state identity does not match.", nameof(persisted));
    }

    public OptionalMitigationOptions Options => options;
    public MitigationState State => state;
    public string Identity => identity;

    /// <summary>Arm a finite identity-bound lease.  This is the explicit
    /// operator opt-in; it never changes a device limit.</summary>
    public MitigationLease ArmLease(string token, double nowMonotonicSeconds,
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

    public void RevokeLease() => state = state with { Lease = null, RequiresRearm = true };

    public string SerializeState() => JsonSerializer.Serialize(state,
        OptionalMitigationJson.Options(true));

    public static OptionalMitigationPolicy Restore(string json,
        OptionalMitigationOptions? options = null, string? identity = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Mitigation state is empty.");
        var saved = JsonSerializer.Deserialize<MitigationState>(json,
            OptionalMitigationJson.Options(false))?.Normalize()
            ?? throw new FormatException("Mitigation state is invalid.");
        var effectiveIdentity = string.IsNullOrWhiteSpace(identity) ? saved.Identity : identity.Trim();
        // A process restart invalidates the previous opt-in lease.  Keep
        // historical incident ids, but require a fresh explicit arm operation.
        saved = saved with { RequiresRearm = true, Lease = null };
        return new OptionalMitigationPolicy(options, effectiveIdentity, saved);
    }

    public MitigationResult Evaluate(MitigationContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        var common = (string.IsNullOrWhiteSpace(context.Identity) ? identity : context.Identity.Trim(),
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
        if (state.Applied.Contains(common.Item2, StringComparer.Ordinal))
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
        if (monotonicAtWrite is double writeMonotonic &&
            !state.Lease!.IsActive(identity, context.LeaseToken, writeMonotonic))
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.LeaseExpired,
                "The opt-in lease expired before the write; no write was attempted.");

        var auditEntry = new MitigationAuditEntry(identity, decision.IncidentId,
            beforeWrite.CurrentLimitWatts.Value, target, decision.ConfiguredFloorWatts!.Value,
            context.Utc, context.MonotonicSeconds, "optional-downward-only-mitigation");
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
        if (monotonicAtWrite is double finalWriteMonotonic &&
            !state.Lease!.IsActive(identity, context.LeaseToken, finalWriteMonotonic))
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.LeaseExpired,
                "The opt-in lease expired at the write boundary; no write was attempted.");

        MitigationAdapterApply applied;
        try { applied = adapter.Apply(target); }
        catch (Exception ex)
        {
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.WriteFailed, "Adapter apply failed: " + ex.Message);
        }
        if (!applied.Succeeded)
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.WriteFailed, applied.Detail);

        MitigationDeviceRead after;
        try { after = adapter.Read(); }
        catch (Exception ex)
        {
            return Block(decision.Identity, decision.IncidentId, context,
                MitigationBlockReason.ReadbackFailed, "Readback failed: " + ex.Message);
        }
        if (!after.Supported || !after.Available || !after.Fresh ||
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
        return decision with
        {
            Reason = "APPLIED",
            AppliedTargetWatts = after.CurrentLimitWatts,
            Detail = "Downward-only target applied and verified by readback; no restore is scheduled.",
        };
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
public sealed class FakePowerLimitMitigationAdapter : IPowerLimitMitigationAdapter
{
    public string Identity { get; set; } = "fixture";
    public bool Supported { get; set; } = true;
    public bool Available { get; set; } = true;
    public bool Fresh { get; set; } = true;
    public double CurrentLimitWatts { get; set; } = 450;
    public double? BoardPowerWatts { get; set; } = 100;
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
        if (FailApply) return new(false, "fixture write failure");
        CurrentLimitWatts = IncreaseOnApply ? CurrentLimitWatts + 5 : RoundingWatts > 0
            ? Math.Round(targetWatts / RoundingWatts) * RoundingWatts : targetWatts;
        return new(true, "fixture write", CurrentLimitWatts);
    }
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

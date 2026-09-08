namespace ConnectorWatch;

/// <summary>Offline policy tests.  They use only fake adapters and never call
/// native APIs; the policy is deliberately not wired to a real GPU adapter.</summary>
public static class OptionalMitigationTests
{
    public static void Run()
    {
        DisabledAndLeaseGatesAreFailClosed();
        DownwardApplyRequiresAuditAndReadback();
        FailuresAndConcurrentChangesBlock();
        LeaseExpiryRestartAndRepeatIncidentAreSafe();
        OverlappingAppliesAreRejected();
        ReentrantApplyAndRevocationFailClosed();
        FailedTransactionReleasesGate();
        CallerAuthorizationFailsClosed();
        LifecycleRequiresElectricalLoadObservation();
        DenialTimeoutAndVerificationFailurePreserveLatch();
    }

    static void DisabledAndLeaseGatesAreFailClosed()
    {
        var t = Time(0);
        var disabled = new OptionalMitigationPolicy(identity: "fixture-gpu");
        Check(disabled.Evaluate(Context(t, 0)).Decision == MitigationDecision.BLOCKED &&
            disabled.Evaluate(Context(t, 0)).Reason == MitigationBlockReason.Disabled,
            "mitigation is disabled by default");

        var policy = Enabled();
        var beforeArm = policy.Evaluate(Context(t, 0));
        Check(beforeArm.Decision == MitigationDecision.BLOCKED &&
            beforeArm.Reason == MitigationBlockReason.RestartRearmRequired,
            "fresh policy requires an explicit opt-in lease");
        policy.ArmLease("lease-a", 0, 10);
        var noIncident = policy.Evaluate(Context(t, 1, latched: false, lease: "lease-a"));
        Check(noIncident.Decision == MitigationDecision.NO_OP &&
            noIncident.Reason == MitigationBlockReason.NoLatchedIncident,
            "unlatched incidents produce no-op");
        var wrongIdentity = policy.Evaluate(Context(t, 1, identity: "other-gpu", lease: "lease-a"));
        Check(wrongIdentity.Decision == MitigationDecision.BLOCKED &&
            wrongIdentity.Reason == MitigationBlockReason.IdentityMismatch,
            "identity mismatch blocks before adapter use");
        var missingIdentity = policy.Evaluate(Context(t, 1, identity: "", lease: "lease-a"));
        Check(missingIdentity.Decision == MitigationDecision.BLOCKED &&
            missingIdentity.Reason == MitigationBlockReason.IdentityMismatch,
            "blank identity cannot borrow the policy identity or its lease");
        var unsupported = policy.Evaluate(Context(t, 1, lease: "lease-a", supported: false));
        Check(unsupported.Decision == MitigationDecision.BLOCKED &&
            unsupported.Reason == MitigationBlockReason.Unsupported,
            "unsupported device blocks");
        var stale = policy.Evaluate(Context(t, 1, lease: "lease-a", fresh: false));
        Check(stale.Decision == MitigationDecision.BLOCKED &&
            stale.Reason == MitigationBlockReason.DeviceStale,
            "stale device telemetry blocks");
    }

    static void DownwardApplyRequiresAuditAndReadback()
    {
        var policy = Enabled();
        policy.ArmLease("lease-a", 0, 10);
        var adapter = new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu" };
        var audit = new FakeMitigationAuditGate();
        var result = policy.Apply(Context(Time(1), 1, lease: "lease-a"), adapter, audit);
        Check(result.Decision == MitigationDecision.REDUCE && result.Reason == "APPLIED" &&
            result.AppliedTargetWatts == 420 && adapter.ApplyCount == 1 && audit.CallCount == 1,
            "valid downward reduction is audited, applied, and read back");
        Check(audit.LastEntry?.PreviousLimitWatts == 450 &&
            audit.LastEntry.TargetLimitWatts == 420 &&
            audit.LastEntry.ConfiguredFloorWatts == 400,
            "durable audit includes identity-bound old target and floor");
        var repeated = policy.Evaluate(Context(Time(2), 2, lease: "lease-a"));
        Check(repeated.Decision == MitigationDecision.NO_OP &&
            repeated.Reason == MitigationBlockReason.IncidentAlreadyHandled,
            "same incident cannot be applied repeatedly");

        var floor = Enabled();
        floor.ArmLease("lease-floor", 0, 10);
        var belowFloor = floor.Evaluate(Context(Time(1), 1, lease: "lease-floor", target: 399));
        Check(belowFloor.Decision == MitigationDecision.BLOCKED &&
            belowFloor.Reason == MitigationBlockReason.TargetBelowFloor,
            "target below configured floor blocks");
        var notLower = floor.Evaluate(Context(Time(1), 1, lease: "lease-floor", target: 450));
        Check(notLower.Decision == MitigationDecision.BLOCKED &&
            notLower.Reason == MitigationBlockReason.TargetNotLower,
            "equal or upward target never passes");

        var rounded = Enabled();
        rounded.ArmLease("lease-round", 0, 10);
        var roundedAdapter = new FakePowerLimitMitigationAdapter
        {
            Identity = "fixture-gpu", RoundingWatts = 10
        };
        var roundedResult = rounded.Apply(Context(Time(1), 1, lease: "lease-round",
            target: 400, floor: 400), roundedAdapter, new FakeMitigationAuditGate());
        Check(roundedResult.Decision == MitigationDecision.REDUCE &&
            roundedResult.AppliedTargetWatts == 400,
            "adapter floor rounding remains downward and within the floor");
    }

    static void FailuresAndConcurrentChangesBlock()
    {
        var auditFailure = Enabled();
        auditFailure.ArmLease("lease-a", 0, 10);
        var audit = new FakeMitigationAuditGate { Durable = false };
        var noAudit = auditFailure.Apply(Context(Time(1), 1, lease: "lease-a"),
            new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu" }, audit);
        Check(noAudit.Decision == MitigationDecision.BLOCKED &&
            noAudit.Reason == MitigationBlockReason.AuditNotDurable && audit.CallCount == 1,
            "non-durable audit prevents the adapter write");

        var writeFailure = Enabled();
        writeFailure.ArmLease("lease-w", 0, 10);
        var writeAdapter = new FakePowerLimitMitigationAdapter
        {
            Identity = "fixture-gpu", FailApply = true
        };
        var write = writeFailure.Apply(Context(Time(1), 1, lease: "lease-w"), writeAdapter,
            new FakeMitigationAuditGate());
        Check(write.Decision == MitigationDecision.BLOCKED &&
            write.Reason == MitigationBlockReason.WriteFailed,
            "adapter write failure blocks");

        var readbackFailure = Enabled();
        readbackFailure.ArmLease("lease-r", 0, 10);
        var readback = readbackFailure.Apply(Context(Time(1), 1, lease: "lease-r"),
            new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu", FailReadback = true },
            new FakeMitigationAuditGate());
        Check(readback.Decision == MitigationDecision.BLOCKED &&
            readback.Reason == MitigationBlockReason.ReadbackFailed,
            "readback failure blocks");

        var concurrent = Enabled();
        concurrent.ArmLease("lease-c", 0, 10);
        var concurrentAdapter = new FakePowerLimitMitigationAdapter
        {
            Identity = "fixture-gpu", ConcurrentChangeOnSecondRead = true,
            ConcurrentLimitWatts = 430
        };
        var changed = concurrent.Apply(Context(Time(1), 1, lease: "lease-c"), concurrentAdapter,
            new FakeMitigationAuditGate());
        Check(changed.Decision == MitigationDecision.BLOCKED &&
            changed.Reason == MitigationBlockReason.ConcurrentChange &&
            concurrentAdapter.ApplyCount == 0,
            "concurrent limit change is detected before any write");

        var roundedBelowFloor = Enabled();
        roundedBelowFloor.ArmLease("lease-b", 0, 10);
        var belowAdapter = new FakePowerLimitMitigationAdapter
        {
            Identity = "fixture-gpu", RoundingWatts = 10
        };
        var below = roundedBelowFloor.Apply(Context(Time(1), 1, lease: "lease-b",
            target: 405, floor: 405), belowAdapter, new FakeMitigationAuditGate());
        Check(below.Decision == MitigationDecision.BLOCKED &&
            below.Reason == MitigationBlockReason.ReadbackFailed,
            "rounding below the floor fails closed");

        var hostile = Enabled();
        hostile.ArmLease("lease-h", 0, 10);
        var hostileAdapter = new FakePowerLimitMitigationAdapter
        {
            Identity = "fixture-gpu", IncreaseOnApply = true
        };
        var increased = hostile.Apply(Context(Time(1), 1, lease: "lease-h"),
            hostileAdapter, new FakeMitigationAuditGate());
        Check(increased.Decision == MitigationDecision.BLOCKED &&
            increased.Reason == MitigationBlockReason.NoIncrease,
            $"a non-conforming adapter readback cannot turn mitigation into an increase (actual: {increased.Reason})");
    }

    static void LeaseExpiryRestartAndRepeatIncidentAreSafe()
    {
        var policy = Enabled();
        policy.ArmLease("lease-a", 0, 2);
        var expired = policy.Evaluate(Context(Time(2), 2, lease: "lease-a"));
        Check(expired.Decision == MitigationDecision.BLOCKED &&
            expired.Reason == MitigationBlockReason.LeaseExpired,
            "expired lease blocks at evaluation");
        var beforeWrite = policy.Apply(Context(Time(1), 1, lease: "lease-a"),
            new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu" },
            new FakeMitigationAuditGate(), monotonicAtWrite: 2);
        Check(beforeWrite.Decision == MitigationDecision.BLOCKED &&
            beforeWrite.Reason == MitigationBlockReason.LeaseExpired,
            "lease expiry at the write boundary prevents a write");
        policy.RevokeLease();
        Check(policy.Evaluate(Context(Time(1), 1)).Reason == MitigationBlockReason.RestartRearmRequired,
            "revoked lease requires re-arm");

        policy.ArmLease("lease-live", 3, 10);
        var restored = OptionalMitigationPolicy.Restore(policy.SerializeState(),
            policy.Options, "fixture-gpu");
        var afterRestart = restored.Evaluate(Context(Time(4), 4, lease: "lease-live"));
        Check(afterRestart.Decision == MitigationDecision.BLOCKED &&
            afterRestart.Reason == MitigationBlockReason.RestartRearmRequired,
            "restart invalidates the previous opt-in lease");
        Check(!PowerLimitWatchdog.WritesPowerLimit,
            "optional mitigation is separate from the read-only watchdog contract");
    }

    static void OverlappingAppliesAreRejected()
    {
        var policy = Enabled();
        policy.ArmLease("lease-a", 0, 10);
        var adapter = new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu" };
        using var enteredAudit = new System.Threading.ManualResetEventSlim();
        using var releaseAudit = new System.Threading.ManualResetEventSlim();
        var audit = new CallbackAudit(() =>
        {
            enteredAudit.Set();
            if (!releaseAudit.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Test did not release the audit gate.");
            return true;
        });
        var first = System.Threading.Tasks.Task.Run(() => policy.Apply(
            Context(Time(1), 1, lease: "lease-a"), adapter, audit));
        try
        {
            Check(enteredAudit.Wait(TimeSpan.FromSeconds(5)), "first apply reaches durable audit");
            var competingAudit = new FakeMitigationAuditGate();
            var sameIncident = policy.Apply(Context(Time(1), 1, lease: "lease-a"),
                adapter, competingAudit);
            var otherIncident = policy.Apply(Context(Time(1), 1, lease: "lease-a",
                incident: "incident-2"), adapter, competingAudit);
            Check(sameIncident.Reason == MitigationBlockReason.ConcurrentChange &&
                otherIncident.Reason == MitigationBlockReason.ConcurrentChange &&
                sameIncident.IsBlocked && otherIncident.IsBlocked &&
                competingAudit.CallCount == 0 && adapter.ReadCount == 2 && adapter.ApplyCount == 0,
                "overlapping incidents cannot enter adapter or audit while a transaction is active");
        }
        finally
        {
            releaseAudit.Set();
            Check(first.Wait(TimeSpan.FromSeconds(5)), "first apply finishes after audit release");
        }
        Check(first.Result.Reason == "APPLIED" && adapter.ApplyCount == 1 &&
            policy.State.Applied.SequenceEqual(new[] { "incident-1" }),
            "only the admitted transaction writes and records incident state");
        Check(policy.Apply(Context(Time(2), 2, lease: "lease-a"), adapter,
            new FakeMitigationAuditGate()).Reason == MitigationBlockReason.IncidentAlreadyHandled,
            "completed transaction still suppresses repeated incidents");
    }

    static void ReentrantApplyAndRevocationFailClosed()
    {
        var policy = Enabled();
        policy.ArmLease("lease-a", 0, 10);
        var adapter = new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu" };
        var nestedAudit = new FakeMitigationAuditGate();
        MitigationResult? nested = null;
        var result = policy.Apply(Context(Time(1), 1, lease: "lease-a"), adapter,
            new CallbackAudit(() =>
            {
                nested = policy.Apply(Context(Time(1), 1, lease: "lease-a"), adapter, nestedAudit);
                return true;
            }));
        Check(result.Reason == "APPLIED" && nested?.IsBlocked == true &&
            nested.Reason == MitigationBlockReason.ConcurrentChange &&
            nestedAudit.CallCount == 0 && adapter.ApplyCount == 1,
            "reentrant audit callback cannot start a nested apply transaction");

        var revoked = Enabled();
        revoked.ArmLease("lease-a", 0, 10);
        var revokedAdapter = new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu" };
        var revokedResult = revoked.Apply(Context(Time(1), 1, lease: "lease-a"), revokedAdapter,
            new CallbackAudit(() => { revoked.RevokeLease(); return true; }));
        Check(revokedResult.IsBlocked &&
            revokedResult.Reason == MitigationBlockReason.RestartRearmRequired &&
            revokedAdapter.ApplyCount == 0,
            "lease revocation inside an audit callback fails closed before writing");
    }

    static void FailedTransactionReleasesGate()
    {
        var policy = Enabled();
        policy.ArmLease("lease-a", 0, 10);
        var adapter = new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu" };
        var failed = policy.Apply(Context(Time(1), 1, lease: "lease-a"), adapter,
            new CallbackAudit(() => throw new InvalidOperationException("fixture audit exception")));
        Check(failed.Reason == MitigationBlockReason.AuditNotDurable && adapter.ApplyCount == 0,
            "audit exception remains fail-closed");
        var retry = policy.Apply(Context(Time(2), 2, lease: "lease-a"), adapter,
            new FakeMitigationAuditGate());
        Check(retry.Reason == "APPLIED" && adapter.ApplyCount == 1,
            "failed transaction releases gate for a subsequent request");
    }

    static void CallerAuthorizationFailsClosed()
    {
        var context = Context(Time(1), 1, lease: "lease-a");
        foreach (var invalid in new[]
        {
            context with { CallerIdentity = "other-operator" },
            context with { AuthorizationContext = "other-auth" },
            context with { AuthorizationContext = "" },
        })
        {
            var policy = Enabled();
            policy.ArmLease("lease-a", 0, 10);
            var adapter = new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu" };
            var audit = new FakeMitigationAuditGate();
            var result = policy.Apply(invalid, adapter, audit);
            Check(result.Reason == MitigationBlockReason.UnauthorizedCaller &&
                result.Stage == MitigationStage.MITIGATION_FAILED &&
                adapter.ReadCount == 0 && adapter.ApplyCount == 0 && audit.CallCount == 0,
                "wrong caller or authorization context cannot reach the adapter or audit");
        }
        var missingGate = new OptionalMitigationPolicy(new(Enabled: true), "fixture-gpu");
        missingGate.ArmLease("lease-a", 0, 10);
        Check(missingGate.Evaluate(context).Reason == MitigationBlockReason.UnauthorizedCaller,
            "missing authorization gate is denied even with a valid lease");
    }

    static void LifecycleRequiresElectricalLoadObservation()
    {
        var policy = Enabled();
        policy.ArmLease("lease-a", 0, 10);
        var adapter = new FakePowerLimitMitigationAdapter { Identity = "fixture-gpu" };
        var result = policy.Apply(Context(Time(1), 1, lease: "lease-a"), adapter,
            new CallbackAudit(() =>
            {
                Check(policy.State.LastStage == MitigationStage.MITIGATION_REQUESTED,
                    "audit sees a requested transaction before any adapter acceptance");
                return true;
            }));
        Check(result.Stage == MitigationStage.MITIGATION_VERIFIED && result.Stages.SequenceEqual(new[]
            { MitigationStage.MITIGATION_REQUESTED, MitigationStage.MITIGATION_ACCEPTED,
                MitigationStage.MITIGATION_VERIFIED }) &&
            result.LoadVerification == MitigationLoadVerification.REDUCTION_OBSERVED &&
            result.LoadBeforeWatts == 450 && result.LoadAfterWatts == 420 &&
            adapter.LastObservationMaximumSeconds == policy.Options.ObservationMaximumSeconds &&
            adapter.LastObservationMaximumSamples == policy.Options.ObservationMaximumSamples,
            "verification requires observed electrical reduction within explicit time and sample budgets");
        Check(result.IncidentLatched && policy.State.LastIncidentLatched &&
            policy.State.LastStage == MitigationStage.MITIGATION_VERIFIED,
            "successful mitigation preserves the incident latch");

        var low = Enabled();
        low.ArmLease("lease-a", 0, 10);
        var lowAdapter = new FakePowerLimitMitigationAdapter
            { Identity = "fixture-gpu", BoardPowerWatts = 100 };
        var lowResult = low.Apply(Context(Time(1), 1, lease: "lease-a"), lowAdapter,
            new FakeMitigationAuditGate());
        Check(lowResult.Stage == MitigationStage.MITIGATION_ACCEPTED &&
            lowResult.LoadVerification == MitigationLoadVerification.UNOBSERVABLE_ALREADY_LOW &&
            !lowResult.Stages.Contains(MitigationStage.MITIGATION_VERIFIED) &&
            lowResult.IncidentLatched && lowAdapter.ObservationCount == 0,
            "already-low load is explicitly unobservable and never falsely verified");
    }

    static void DenialTimeoutAndVerificationFailurePreserveLatch()
    {
        var cases = new[]
        {
            (new FakePowerLimitMitigationAdapter { FailApply = true }, MitigationBlockReason.WriteFailed),
            (new FakePowerLimitMitigationAdapter { ApplyTimesOut = true }, MitigationBlockReason.WriteTimeout),
            (new FakePowerLimitMitigationAdapter { ObservationTimesOut = true }, MitigationBlockReason.ObservationTimeout),
            (new FakePowerLimitMitigationAdapter { Observation = new(new[]
            {
                new MitigationLoadSample("fixture-gpu", 1, 450),
                new MitigationLoadSample("fixture-gpu", 2, 450),
            }, 2) }, MitigationBlockReason.LoadNotReduced),
            (new FakePowerLimitMitigationAdapter { BoardPowerWatts = null }, MitigationBlockReason.LoadUnavailable),
            (new FakePowerLimitMitigationAdapter { Observation = new(new[]
            {
                new MitigationLoadSample("fixture-gpu", 1, 410),
                new MitigationLoadSample("fixture-gpu", 2, 450),
            }, 2) }, MitigationBlockReason.LoadNotReduced),
            (new FakePowerLimitMitigationAdapter { Observation = new(new[]
            {
                new MitigationLoadSample("fixture-gpu", 1, 410),
                new MitigationLoadSample("fixture-gpu", 2, 410, Fresh: false),
            }, 2) }, MitigationBlockReason.LoadUnavailable),
            (new FakePowerLimitMitigationAdapter { Observation = new(
                Enumerable.Range(1, 17).Select(i => new MitigationLoadSample("fixture-gpu",
                    i / 10.0, 410)).ToArray(), 2) }, MitigationBlockReason.LoadUnavailable),
            (new FakePowerLimitMitigationAdapter { Observation = new(new[]
            {
                new MitigationLoadSample("fixture-gpu", 1, 410),
                new MitigationLoadSample("fixture-gpu", 6, 410),
            }, 6) }, MitigationBlockReason.ObservationTimeout),
            (new FakePowerLimitMitigationAdapter { Observation = new(new[]
            {
                new MitigationLoadSample("fixture-gpu", 1, 410),
                new MitigationLoadSample("other-gpu", 2, 410),
            }, 2) }, MitigationBlockReason.LoadUnavailable),
        };
        foreach (var (adapter, reason) in cases)
        {
            var policy = Enabled();
            policy.ArmLease("lease-a", 0, 10);
            adapter.Identity = "fixture-gpu";
            var result = policy.Apply(Context(Time(1), 1, lease: "lease-a"), adapter,
                new FakeMitigationAuditGate());
            Check(result.IsBlocked && result.Reason == reason &&
                result.Stage == MitigationStage.MITIGATION_FAILED &&
                !result.Stages.Contains(MitigationStage.MITIGATION_VERIFIED) &&
                result.IncidentLatched && policy.State.LastIncidentLatched,
                "denial, timeout, or missing load reduction fails without clearing the latch: " + reason);
            Check(policy.Apply(Context(Time(2), 2, lease: "lease-a"), adapter,
                new FakeMitigationAuditGate()).Reason == MitigationBlockReason.IncidentAlreadyHandled &&
                adapter.ApplyCount == 1,
                "uncertain or failed post-write outcomes cannot automatically retry");
            var restored = OptionalMitigationPolicy.Restore(policy.SerializeState(), policy.Options);
            Check(restored.State.LastIncidentLatched && restored.State.RequiresRearm &&
                restored.State.Attempted.Contains("incident-1"),
                "restart preserves incident latch and attempted write history");
        }
    }

    sealed class CallbackAudit(Func<bool> record) : IMitigationAuditGate
    {
        public bool RecordDurably(MitigationAuditEntry entry) => record();
    }

    static OptionalMitigationPolicy Enabled() => new(
        new OptionalMitigationOptions(Enabled: true), "fixture-gpu",
        authorization: new FakeMitigationAuthorizationGate());

    static MitigationContext Context(DateTimeOffset timestamp, double monotonic,
        string identity = "fixture-gpu", string incident = "incident-1",
        bool latched = true, bool available = true, bool fresh = true,
        bool supported = true, double current = 450, double floor = 400,
        double target = 420, string? lease = null) => new(
        identity, incident, latched, available, fresh, supported, current, floor, target,
        timestamp, monotonic, lease, CallerIdentity: "fixture-operator",
        AuthorizationContext: "fixture-auth");

    static DateTimeOffset Time(double seconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAILED: " + description);
    }
}

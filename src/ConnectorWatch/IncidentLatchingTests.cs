namespace ConnectorWatch;

/// <summary>Deterministic, hardware-free checks for issue #12.</summary>
public static class IncidentLatchingTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            passed++;
        }

        var start = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var options = new IncidentCaptureOptions
        {
            PreSamples = 3,
            PreWindow = TimeSpan.FromSeconds(10),
            PostSamples = 2,
            PostWindow = TimeSpan.FromSeconds(10),
            MaxIncidents = 4,
            MaxRecentObservationIds = 32,
        };
        var ledger = new IncidentLedger("fixture-identity", options, start);

        IncidentObservation Row(int second, string status = "NO_SHIFT_DETECTED",
            double voltage = 12.1) => new(start.AddSeconds(second), status, voltage, 440);

        ledger.Observe(Row(0));
        ledger.Observe(Row(1));
        ledger.Observe(Row(2));
        var trigger = new IncidentTrigger("legacy_trend", "SUDDEN_DROOP",
            IncidentSeverity.CRITICAL, detail: "rapid drop");
        var triggerResult = ledger.Observe(Row(3, "SUDDEN_DROOP", 11.7), trigger);
        Check(triggerResult.Accepted && triggerResult.Triggered && triggerResult.Incident is not null,
            "detector trigger creates a latched incident");
        var incident = triggerResult.Incident!;
        Check(incident.IncidentId.StartsWith("INC-", StringComparison.Ordinal) &&
            incident.State == IncidentLifecycleState.LATCHED,
            "incident id and initial latched state are stable");
        Check(incident.Forensic.Pre.Count == 3 &&
            incident.Forensic.Trigger.TimestampUtc == start.AddSeconds(3) &&
            incident.Forensic.Post.Count == 0,
            "trigger is separated from bounded pre/post windows");

        var afterOne = ledger.Observe(Row(4, "NO_SHIFT_DETECTED"));
        Check(afterOne.Accepted && ledger.Get(incident.IncidentId)?.Forensic.Post.Count == 1,
            "post-trigger sample is retained");
        var acknowledgement = ledger.Acknowledge(incident.IncidentId,
            start.AddSeconds(5), "operator-a", "seen");
        var acknowledged = ledger.Get(incident.IncidentId)!;
        Check(acknowledgement.IncidentId == incident.IncidentId &&
            acknowledged.State == IncidentLifecycleState.ACKNOWLEDGED &&
            acknowledged.IsLatched && !acknowledged.IsResolved,
            "acknowledgement is separate from resolution");

        // A healthy row cannot clear a latched incident. The second post row
        // completes capture, but only Resolve changes lifecycle state.
        var afterTwo = ledger.Observe(Row(5, "NO_SHIFT_DETECTED"));
        var captured = ledger.Get(incident.IncidentId)!;
        Check(afterTwo.Accepted && captured.Forensic.Post.Count == 2 &&
            captured.Forensic.PostComplete &&
            ledger.Get(incident.IncidentId)!.IsLatched,
            "post window completes without auto-resolving the incident");
        var resolution = ledger.Resolve(incident.IncidentId,
            start.AddSeconds(6), "operator-a", "investigated");
        Check(resolution.IncidentId == incident.IncidentId &&
            ledger.Get(incident.IncidentId)!.State == IncidentLifecycleState.RESOLVED,
            "explicit resolution ends the latch");
        Check(ledger.Resolve(incident.IncidentId, start.AddSeconds(7), "other").ResolvedAtUtc ==
            start.AddSeconds(6), "resolution is idempotent after restart-safe replay");

        string json = ledger.ToJson();
        var restored = IncidentLedger.Load(json, "fixture-identity", options);
        Check(restored.ToJson() == json, "ledger persistence is deterministic");
        Check(restored.Get(incident.IncidentId)?.Forensic.Post.Count == 2 &&
            restored.Get(incident.IncidentId)?.Acknowledgement?.AcknowledgedBy == "operator-a",
            "forensic and acknowledgement records survive restart");
        var duplicate = restored.Observe(Row(5, "NO_SHIFT_DETECTED"));
        Check(duplicate.Duplicate && !duplicate.Accepted &&
            restored.Get(incident.IncidentId)!.Forensic.Post.Count == 2,
            "replaying the last row after restart does not duplicate evidence");

        // A second episode of the same detector gets a new deterministic id
        // after the first episode is explicitly resolved.
        var next = restored.Observe(Row(8, "BASELINE_SHIFT", 11.4),
            new IncidentTrigger("legacy_trend", "BASELINE_SHIFT"));
        Check(next.Triggered && next.Incident is not null &&
            next.Incident.IncidentId != incident.IncidentId,
            "a later episode is distinct after explicit resolution");
        var independent = new IncidentLedger("fixture-identity", options, start);
        independent.Observe(Row(0));
        independent.Observe(Row(1));
        independent.Observe(Row(2));
        var same = independent.Observe(Row(3, "SUDDEN_DROOP", 11.7), trigger);
        Check(same.Incident?.IncidentId == incident.IncidentId,
            "incident id is deterministic across independent replay");

        bool mismatchRejected = false;
        try { _ = IncidentLedger.Load(json, "other-identity", options); }
        catch (InvalidDataException) { mismatchRejected = true; }
        Check(mismatchRejected, "identity mismatch fails closed on restore");

        Console.WriteLine($"PASS: {passed} incident-latching checks.");
    }
}

using System.Diagnostics;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>Monotonic timestamps are process-local measurements.  They are
/// intentionally paired with an instance id before being exposed outside the
/// process; a Stopwatch tick from one process must never be compared with a
/// tick from a restarted process.</summary>
public static class MonotonicTime
{
    public static long NowTicks => Stopwatch.GetTimestamp();
    public static double Frequency => Stopwatch.Frequency;

    public static double? ElapsedSeconds(long start, long end)
    {
        if (end < start) return null;
        var seconds = (end - start) / (double)Stopwatch.Frequency;
        return double.IsFinite(seconds) && seconds >= 0 ? seconds : null;
    }
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PollValueChangeFlags
{
    None = 0,
    ConnectorVoltage = 1,
    ConnectorCurrent = 2,
    SlotVoltage = 4,
    SlotCurrent = 8,
    InitialObservation = 16,
    All = ConnectorVoltage | ConnectorCurrent | SlotVoltage | SlotCurrent,
}

/// <summary>The raw values used for repeated-observation detection.  Null is
/// meaningful: an absent current is different from a measured zero current.
/// This record does not infer freshness from equal values.</summary>
public readonly record struct RawElectricalObservation(
    double? ConnectorVoltageV,
    double? ConnectorCurrentA,
    double? SlotVoltageV,
    double? SlotCurrentA)
{
    public double? PcieVoltageV => SlotVoltageV;
    public double? PcieCurrentA => SlotCurrentA;

    public static RawElectricalObservation FromElectricalSample(ElectricalSample sample)
    {
        if (sample is null) throw new ArgumentNullException(nameof(sample));
        return new(sample.Connector.VoltageV, sample.Connector.CurrentA,
            sample.Pcie.VoltageV, sample.Pcie.CurrentA);
    }
}

/// <summary>One completed native/source poll.  HostTimestampUtc is wall-clock
/// context only; latency and progress use the monotonic fields.</summary>
public sealed record PollTimingObservation(
    [property: JsonPropertyName("poll_start_monotonic")] long PollStartMonotonic,
    [property: JsonPropertyName("poll_end_monotonic")] long PollEndMonotonic,
    [property: JsonPropertyName("latency_seconds")] double LatencySeconds,
    [property: JsonPropertyName("host_timestamp_utc")] DateTimeOffset HostTimestampUtc,
    [property: JsonPropertyName("connector_voltage_v")] double? ConnectorVoltageV,
    [property: JsonPropertyName("connector_current_a")] double? ConnectorCurrentA,
    [property: JsonPropertyName("slot_voltage_v")] double? SlotVoltageV,
    [property: JsonPropertyName("slot_current_a")] double? SlotCurrentA,
    [property: JsonPropertyName("value_change_flags")] PollValueChangeFlags ValueChangeFlags,
    [property: JsonPropertyName("consecutive_identical_observations")] int ConsecutiveIdenticalObservations)
{
    public long StartMonotonic => PollStartMonotonic;
    public long EndMonotonic => PollEndMonotonic;
    public long PollStartMonotonicTicks => PollStartMonotonic;
    public long PollEndMonotonicTicks => PollEndMonotonic;
    public double LatencyMilliseconds => LatencySeconds * 1000;
    public bool IsFirstObservation =>
        (ValueChangeFlags & PollValueChangeFlags.InitialObservation) != 0;
    public bool ValuesChanged =>
        IsFirstObservation || (ValueChangeFlags & PollValueChangeFlags.All) != PollValueChangeFlags.None;
    public bool IsRepeated => !IsFirstObservation && !ValuesChanged;
    public int IdenticalObservationCount => ConsecutiveIdenticalObservations;
    public int ConsecutiveIdenticalCount => ConsecutiveIdenticalObservations;
    public double? RawConnectorVoltageV => ConnectorVoltageV;
    public double? RawConnectorCurrentA => ConnectorCurrentA;
    public double? RawSlotVoltageV => SlotVoltageV;
    public double? RawSlotCurrentA => SlotCurrentA;
    public double? PcieVoltageV => SlotVoltageV;
    public double? PcieCurrentA => SlotCurrentA;
}

/// <summary>Tracks source/native call timing and value repetition.  It is
/// deliberately independent of native call ownership and timeout handling.
/// A repeated value is telemetry, not a freshness verdict.</summary>
public sealed class PollTimingTracker
{
    readonly RepeatedObservationTracker repetition = new();

    public PollTimingObservation? Last { get; private set; }
    public PollTimingObservation? LastObservation => Last;
    public int ConsecutiveIdenticalObservations => repetition.ConsecutiveIdenticalObservations;

    public PollTimingObservation Record(
        long pollStartMonotonic,
        long pollEndMonotonic,
        DateTimeOffset hostTimestampUtc,
        RawElectricalObservation values)
    {
        var latency = MonotonicTime.ElapsedSeconds(pollStartMonotonic, pollEndMonotonic)
            ?? throw new ArgumentOutOfRangeException(nameof(pollEndMonotonic),
                "Poll end must not precede poll start on the monotonic clock.");

        var repetitionState = repetition.Record(values);

        var result = new PollTimingObservation(
            pollStartMonotonic,
            pollEndMonotonic,
            latency,
            hostTimestampUtc.ToUniversalTime(),
            values.ConnectorVoltageV,
            values.ConnectorCurrentA,
            values.SlotVoltageV,
            values.SlotCurrentA,
            repetitionState.ChangeFlags,
            repetitionState.ConsecutiveIdenticalObservations);
        Last = result;
        return result;
    }

    public PollTimingObservation Record(
        long pollStartMonotonic,
        long pollEndMonotonic,
        DateTimeOffset hostTimestampUtc,
        double? connectorVoltageV,
        double? connectorCurrentA,
        double? slotVoltageV,
        double? slotCurrentA) =>
        Record(pollStartMonotonic, pollEndMonotonic, hostTimestampUtc,
            new RawElectricalObservation(connectorVoltageV, connectorCurrentA,
                slotVoltageV, slotCurrentA));

    public PollTimingObservation Record(
        long pollStartMonotonic,
        long pollEndMonotonic,
        DateTimeOffset hostTimestampUtc,
        ElectricalSample sample)
    {
        if (sample is null) throw new ArgumentNullException(nameof(sample));
        return Record(pollStartMonotonic, pollEndMonotonic, hostTimestampUtc,
            RawElectricalObservation.FromElectricalSample(sample));
    }

    public PollTimingObservation Observe(
        long pollStartMonotonic,
        long pollEndMonotonic,
        DateTimeOffset hostTimestampUtc,
        RawElectricalObservation values) =>
        Record(pollStartMonotonic, pollEndMonotonic, hostTimestampUtc, values);

    public void Reset()
    {
        repetition.Reset();
        Last = null;
    }
}

/// <summary>Conservative experiment gate for the validated native polling
/// rates.  It does not change the daemon's configured sampling period or
/// native worker lifecycle.</summary>
public static class NativePeriodPolicy
{
    public static IReadOnlyList<double> ValidatedRatesHz { get; } = [1, 2, 5];
    public const double MinimumStableSeconds = 10;

    public static bool IsValidatedRate(double rateHz) =>
        double.IsFinite(rateHz) && ValidatedRatesHz.Any(rate => rateHz == rate);

    public static bool TryValidate(double rateHz, bool stableOperation,
        out TimeSpan period, out string reason)
    {
        period = default;
        if (!IsValidatedRate(rateHz))
        {
            reason = "Only 1, 2, and 5 Hz are permitted for controlled native-period experiments.";
            return false;
        }
        if (!stableOperation)
        {
            reason = "Native-period experiments require a validated stable operating condition.";
            return false;
        }
        period = TimeSpan.FromSeconds(1 / rateHz);
        reason = "Validated experiment rate.";
        return true;
    }

    public static bool TryValidateStableDuration(TimeSpan duration, out string reason)
    {
        if (duration.TotalSeconds >= MinimumStableSeconds)
        {
            reason = "Stable duration accepted.";
            return true;
        }
        reason = $"Stable duration must be at least {MinimumStableSeconds:0} seconds.";
        return false;
    }
}

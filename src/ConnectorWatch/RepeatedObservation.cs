using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>Value-only repetition metadata.  This is intentionally separate
/// from freshness: an unchanged pair of readings may be a valid fresh sample,
/// while a changed value may still have an invalid or stale timestamp.</summary>
public sealed record RepeatedObservationState(
    [property: JsonPropertyName("values")] RawElectricalObservation Values,
    [property: JsonPropertyName("change_flags")] PollValueChangeFlags ChangeFlags,
    [property: JsonPropertyName("consecutive_identical_observations")] int ConsecutiveIdenticalObservations)
{
    public bool IsFirstObservation =>
        (ChangeFlags & PollValueChangeFlags.InitialObservation) != 0;
    public bool IsRepeated => !IsFirstObservation && ChangeFlags == PollValueChangeFlags.None;
    public bool ValuesChanged => IsFirstObservation || !IsRepeated;
    public int IdenticalCount => ConsecutiveIdenticalObservations;
}

/// <summary>Tracks exact raw-value repetition without making a sensor
/// freshness decision.  Null/present transitions count as changes; finite
/// validation belongs to the acquisition-health contract.</summary>
public sealed class RepeatedObservationTracker
{
    RawElectricalObservation? previous;
    int consecutiveIdentical;

    public RepeatedObservationState? Last { get; private set; }
    public int ConsecutiveIdenticalObservations => consecutiveIdentical;

    public RepeatedObservationState Record(RawElectricalObservation values)
    {
        PollValueChangeFlags flags;
        if (!previous.HasValue)
        {
            flags = PollValueChangeFlags.InitialObservation;
            consecutiveIdentical = 1;
        }
        else
        {
            flags = ChangeFlags(previous.Value, values);
            consecutiveIdentical = flags == PollValueChangeFlags.None
                ? consecutiveIdentical + 1
                : 1;
        }
        previous = values;
        return Last = new(values, flags, consecutiveIdentical);
    }

    public RepeatedObservationState Record(
        double? connectorVoltageV,
        double? connectorCurrentA,
        double? slotVoltageV,
        double? slotCurrentA) =>
        Record(new RawElectricalObservation(connectorVoltageV, connectorCurrentA,
            slotVoltageV, slotCurrentA));

    public void Reset()
    {
        previous = null;
        consecutiveIdentical = 0;
        Last = null;
    }

    internal static PollValueChangeFlags ChangeFlags(RawElectricalObservation before,
        RawElectricalObservation after)
    {
        var flags = PollValueChangeFlags.None;
        if (!Same(before.ConnectorVoltageV, after.ConnectorVoltageV))
            flags |= PollValueChangeFlags.ConnectorVoltage;
        if (!Same(before.ConnectorCurrentA, after.ConnectorCurrentA))
            flags |= PollValueChangeFlags.ConnectorCurrent;
        if (!Same(before.SlotVoltageV, after.SlotVoltageV))
            flags |= PollValueChangeFlags.SlotVoltage;
        if (!Same(before.SlotCurrentA, after.SlotCurrentA))
            flags |= PollValueChangeFlags.SlotCurrent;
        return flags;
    }

    static bool Same(double? left, double? right) =>
        left.HasValue == right.HasValue &&
        (!left.HasValue || left.Value.Equals(right!.Value));
}

namespace ConnectorWatch;

/// <summary>One qualified minute of recorded telemetry. Raw recordings are never changed.</summary>
public sealed record ShadowTelemetryRow(
    DateTimeOffset TimestampUtc,
    double VoltageV,
    double? ConnectorCurrentA,
    double? ConnectorPowerW,
    double? BoardPowerW,
    double? TemperatureC,
    double? PcieVoltageV,
    string CohortKey,
    int ObservationCount = 1,
    string PowerProvenance = "Unknown");

public sealed record ShadowSourceFile(string Name, long Bytes, DateTimeOffset LastWriteUtc);

public sealed record ShadowReadResult(
    IReadOnlyList<ShadowTelemetryRow> Rows,
    IReadOnlyList<ShadowSourceFile> Files,
    long RowsRead,
    long EligibleRawRows,
    long InvalidRows,
    long RejectedRows,
    long DuplicateRows,
    long ConflictingRows,
    long PartialRows,
    bool Truncated,
    DateTimeOffset InputCutoffUtc,
    IReadOnlyList<string> Warnings);

public sealed record ShadowReadOptions
{
    public DateTimeOffset InputCutoffUtc { get; init; } = DateTimeOffset.UtcNow;
    public int MaximumMinutes { get; init; } = 200_000;
    public int MinimumObservationsPerMinute { get; init; } = 5;
    public double MinimumConnectorPowerW { get; init; } = 100;
    public double MaximumAgeSeconds { get; init; } = 5;
}

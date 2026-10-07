using System.Diagnostics;

namespace ConnectorWatch;

/// <summary>Per-session inputs for deterministic runtime integration checks.
/// The command-line entry point always uses the production dependencies.</summary>
internal sealed record RuntimeSessionOverrides
{
    internal required Func<DateTimeOffset> UtcNow { get; init; }
    internal required Func<Gpu> ReadGpu { get; init; }
    internal required IVoltageSource? VoltageSource { get; init; }
    internal string? SourceSetupError { get; init; }
    internal Func<long> MonotonicTimestamp { get; init; } = Stopwatch.GetTimestamp;
    internal bool SkipSampleWait { get; init; }
    internal bool SkipBackgroundMaintenance { get; init; }
    internal Action<int, string>? SampleCompleted { get; init; }
    internal Action<string, string>? BeforeStoppedStatusWrite { get; init; }
    internal string? DiagnosticLogDirectory { get; init; }
}

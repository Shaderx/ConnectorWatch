# Offline replay and detector metrics

The daemon's analyzer can be exercised without NVML, NVAPI, a GPU, or a
running daemon through `OfflineReplayRunner`. The replay path uses the same
`Analysis` class as production and supplies a fixed UTC timestamp and a
monotonic elapsed clock for every observation. It never reads the wall clock,
machine identity, or a live sensor.

## Built-in fixtures

`OfflineReplayFixtures.All()` returns deterministic scenarios for:

- `healthy`: steady voltage in one qualified load band;
- `noisy`: bounded alternating noise below the configured thresholds;
- `gradual-sag`: a sub-threshold-per-step sag that eventually crosses the
  sustained comparison;
- `abrupt-droop`: a fresh single-reading rapid drop;
- `missing-stale`: missing and stale voltage followed by recovery; and
- `restart`: a process restart that retains the explicitly accepted reference
  and requires fresh load qualification.

Each scenario starts with a fixed baseline-learning segment. The first sample
after learning carries `acceptReferenceBefore=true`, which represents the
explicit operator acceptance required by the reference lifecycle. The replay
runner does not promote a candidate merely because its sample count is full.

## Metrics

`OfflineReplayRunner.Run(scenario, config)` returns detector rows plus a
machine-readable `ReplayMetrics` value. Metrics include:

- fresh and stale/missing sample counts;
- alert and unavailable confusion counts (true positive, false positive, and
  false negative samples);
- alert and availability precision/recall rates when a denominator exists;
- expected, detected, and missed alert segments;
- first-detection and recovery latency in seconds; and
- exact detector status and expected-state counts.

The `ToJson()` methods on `ReplayRun`, `ReplayMetrics`, and
`ReplayComparisonReport` are stable JSON serialization surfaces. Status keys
remain uppercase and are not camel-cased, so metrics can be consumed by a
script without a translation table.

Threshold comparisons run the same scenario against named profiles:

```csharp
var report = OfflineReplayRunner.Compare(
    OfflineReplayFixtures.GradualSag(),
    [
        new ReplayThresholdProfile("production"),
        new ReplayThresholdProfile("less-sensitive", ShiftVolts: .30,
            SuddenDroopVolts: .40, SustainSamples: 5),
    ]);
File.WriteAllText("gradual-sag-metrics.json", report.ToJson());
```

Scenario inputs can be exchanged as a JSON object or a metadata-header NDJSON
log with `ReplayScenario.ReadJson`/`ReadNdjson`. `ToNdjson()` keeps one sample
per line and records the scenario name and description in the first line.

The fixtures are regression evidence for detector behavior and threshold
trade-offs only. They do not characterize a physical connector, prove that a
source measures the 16-pin rail, or establish a safe operating limit.

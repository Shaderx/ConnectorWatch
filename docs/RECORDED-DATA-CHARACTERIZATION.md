# Recorded-data characterization

Run on 2026-09-09 against the two persisted telemetry CSV files and current
`status.json` in the local live data directory:

```powershell
ConnectorWatch.exe --characterize <data-directory>
```

The report classified the input as `RECORDED`. This means persisted telemetry
was present; it does not validate sensor provenance or certify connector safety.

## Observed data

- 46,425 valid rows, 0 invalid rows, spanning 77,357 seconds.
- Nominal cadence 0.9996 s; P95 interval 1.0117 s.
- One duplicate timestamp and one out-of-order row. The directory analysis
  includes `status.json`, which can repeat the latest CSV observation.
- Three gaps above 2 s; the longest was 29,716.9 s. Conclusions must not treat
  the capture as continuous across those gaps.
- Load, input voltage, temperature, board power, utilization, and power limit
  were present in every row. Connector current and PCIe rail fields were present
  in 46,424 rows. The historical connector-power column was absent, while the
  selected analysis-load field was complete.
- Observed analysis load ranged from 5.087 W to 387.669 W.
- Input voltage ranged from 11.928784 V to 12.107001 V.
- GPU temperature ranged from 32 C to 66 C.
- The descriptive voltage/load association was negative: Pearson r = -0.912,
  with an unqualified ordinary slope of -0.0002563 V/W. This is a capture
  characteristic, not connector resistance and not an alert threshold.
- Input-voltage standard deviation was 23.49 mV (P05 11.996099 V, median
  12.044201 V, P95 12.060528 V). The descriptive load-adjusted residual had a
  9.64 mV standard deviation, P05 -16.10 mV, median 0.80 mV, and P95 14.60 mV.
- PCIe voltage standard deviation was 13.49 mV. The row-aligned connector-minus-
  PCIe delta had a 13.31 mV standard deviation. These pairs share a host row;
  independent native update timestamps were not captured, so this is not proof
  of cross-rail synchronization.
- Connector-current bands 0–10, 10–20, 20–30, and 30–40 A contained 38,779,
  4,245, 3,207, and 193 rows. Their descriptive residual variances were
  0.00008795, 0.00003458, 0.00016248, and 0.00004057 V² respectively.
- First-hour versus last-hour input-voltage means differed by +53.88 mV. The
  capture has unlabeled workloads and long gaps, so this must not be attributed
  to warm-up, aging, temperature, or a connector change.
- Adjacent value repetition was uncommon for connector voltage (3 rows), but
  PCIe current repeated in 4,274 adjacent rows. Observed minimum numeric steps
  reflect serialized precision and are not a calibrated sensor-resolution claim.

## Controlled read-only polling probes

On the supported local native path, isolated 1, 2, and 5 Hz probes ran for at
least 10 seconds each, with desktop alerts disabled and no hardware writes. All
three exited normally. Results are short-duration engineering evidence only:

| Rate | Native rows | Mean / P95 poll latency | Process CPU time | Peak handles / threads | Peak working set |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 Hz | 12 | 1.85 / 4.43 ms | 0.641 s | 297 / 14 | 79.13 MiB |
| 2 Hz | 22 | 1.61 / 3.33 ms | 0.609 s | 297 / 14 | 80.94 MiB |
| 5 Hz | 52 | 1.23 / 2.13 ms | 0.969 s | 297 / 14 | 84.02 MiB |

Every native row had a distinct connector-voltage value and a maximum identical-
observation run of one. All rows correctly remained `SENSOR_UNCHARACTERIZED`
with `HostPollTimestampUnverified`: the ABI returned values, but native freshness
and independent rail timing remain unresolved. Constant observed peaks over ten
seconds do not establish long-duration driver, handle, thread, or memory stability.

## Detector sensitivity probes

The recorded descriptive residual produced zero threshold transitions across
12.894 active captured hours, or 0 observed false alerts/hour. This is an
unlabeled normal-operation estimate, not a false-negative study. Deterministic
step injections gave:

| Injected droop | Fast detector | EWMA detector |
| ---: | --- | --- |
| 10 mV | no detection | no detection |
| 20 mV | no detection | no detection |
| 50 mV | no detection | no detection |
| 100 mV | no detection | no detection |
| 200 mV | no detection | detection after 6 s |

The injections are sensitivity probes, not safety thresholds. They provide no
evidence to lower the production 0.20 V sustained / 0.25 V abrupt defaults, which
remain unchanged. The fast path's latency benefit is not demonstrated at or below
200 mV with the current 0.25 V threshold; larger abrupt-droop behavior is covered
by deterministic replay rather than claimed from this baseline recording.

The schema-version-2 machine-readable command also reports per-signal and
residual distributions, update/quantization observations, current bands,
cross-rail timing status, drift, poll evidence, detector probes, sensor coverage,
gap duration, thermal coverage, and explicit `RECORDED`, `SYNTHETIC`, or
`NO_DATA` classification. Differential-model qualification and replay must still
exclude gaps, stale rows, repeated observations, and out-of-envelope samples.

CPU, handle, thread, and working-set figures above came from the supervising
process during the controlled probes and are not embedded in old telemetry.
Workloads in the long recording were not labelled. Per-call native source time,
per-pin current, contact temperature, and destructive/stress behavior were not
measured; dependent safety and causal claims stop at those boundaries.

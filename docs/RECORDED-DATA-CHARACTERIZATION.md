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

The machine-readable command also reports sensor coverage, gap duration,
thermal duration coverage, and explicit `RECORDED`, `SYNTHETIC`, or `NO_DATA`
evidence classification. Differential-model qualification and replay tests must
still exclude gaps, stale rows, repeated observations, and out-of-envelope
samples before using this data for detection.

# Validation and release limits

ConnectorWatch vNext is experimental. The checks below describe what can be reproduced offline from the source tree and what remains outside that evidence. A public build is not declared hardware-tested here; hardware validation must be confirmed separately for the exact published artifact and machine.

## Reproduce the offline checks

Run these commands from the repository root on Windows with the .NET 8 SDK:

```powershell
dotnet run --project .\src\ConnectorWatch\ConnectorWatch.csproj -- --self-test
dotnet run --project .\src\ConnectorWatch.Gui\ConnectorWatch.Gui.csproj -- --demo --ui-self-test --test-output .\gui-tests.json
```

The daemon self-test uses parser, analysis, persistence, control, and native-decoder fixtures. It does not initialize NVML, load NVAPI, invoke the direct reader, or query a GPU. The GUI command uses synthetic demo data and checks dashboard/tray behavior without attaching to a live daemon. These are safe offline checks; they do not prove that a physical sensor source is correct.

The current vNext offline results include the existing daemon control/storage/native ABI suite plus typed electrical/provenance/freshness, bounded NDJSON-tail, reference lifecycle, deterministic replay, robust differential modelling, fast/EWMA residual detection, incident latching, recorded-data characterization, read-only power-limit monitoring, and disabled mock-mitigation fixtures. The daemon suite passes, both daemon and GUI projects build with zero warnings, and the synthetic UI/tray checks pass. These results cover source-tree behavior only and do not certify a physical sensor, connector, or packaged artifact.

The repository disables shared Roslyn compilation because the compiler-server process was observed crashing under concurrent Windows builds. Run daemon and GUI builds serially; this changes build-process reuse, not application behavior.

To make a release package after the source checks pass:

```powershell
.\scripts\Publish.ps1
```

The intended self-contained output is `dist/ConnectorWatch-win-x64`. The package must be reviewed as a release artifact before making any hardware claim.

## Validation matrix

| Area | Evidence covered by the repository checks | Boundary |
| --- | --- | --- |
| Configuration | Automatic/explicit UUID selection, source mode, numeric ranges, and conflicting source settings are checked | Does not verify that a user's UUID selects the intended card |
| NVML telemetry | Managed read and unavailable-field handling are exercised through offline seams | No GPU or driver is loaded by the offline suite |
| Voltage input | HWiNFO CSV and newline-delimited JSON parsing, freshness, timestamps, gaps, and malformed rows are covered | A parsed value is not proof that the external sensor measures the connector |
| Typed electrical contract | Direct connector/PCIe V/A/W mapping, derived-power provenance, explicit unsupported fields, stable source names, legacy projection and no-fallback selection are covered | V, I and derived V×I are one observation, not three independent sensors |
| NDJSON tail reader | Files beyond 128 KiB, partial leading/trailing records, exact boundaries, CRLF/LF, empty/oversized records and concurrent append sharing are covered | A complete record larger than the bounded window cannot be reconstructed |
| Direct decoder | ABI fixtures, buffer-size checks, canaries, metadata, freshness markers, and worker timeout ownership are covered offline | The direct native provider still requires the supported Windows board/driver and separate physical validation |
| Detector | Bin learning, stable-sample gating, persisted references, rolling windows, percentile markers, gaps, repeated timestamps, and threshold transitions are covered | Thresholds are comparisons, not safety limits |
| Reference lifecycle | Typed identity, candidate versus frozen accepted model, explicit accept/archive commands, legacy migration, restart/degraded/mismatch state, immutable archive history, and status exposure are covered | Acceptance is an operator review action; it does not certify hardware health or connector safety |
| Differential model | Qualified learning, robust fitting, frozen artifact/hash, identity matching, expected voltage, residual, slope units, and insufficient-evidence states are covered | A V/A or V/W association is descriptive and is not connector resistance or causality |
| Residual detection | Abrupt fast-path and time-aware EWMA changes, gaps, poor quality, and recovery are covered with deterministic fixtures | Synthetic thresholds and latency do not establish physical warning performance |
| Replay | Healthy, noisy, gradual-sag, abrupt, missing, stale, and restart fixtures produce deterministic precision/recall/latency and status metrics | Synthetic replay complements but does not replace representative recorded traces |
| Incidents | Deterministic latching, bounded pre/trigger/post windows, persistence, acknowledgement, resolution, and restart behavior are covered | Acknowledgement records operator attention; it cannot clear evidence or accept a reference |
| Recorded data | CSV/JSON/JSONL/NDJSON ingestion, cadence, gaps, duplicates, coverage, ranges, associations, and evidence labels are covered | Characterization is descriptive; incomplete channels and long capture gaps remain explicit |
| Power-limit watchdog | Independent desired/reported/enforced/connector/board channels, partial verification, bounded non-enforcement, drift, stale/unavailable readings, reset/restart, identity/time discontinuities, latching, and persistence are covered with mocks | The live NVML adapter exposes no enforced-limit timestamp/query; it remains partially verified and cannot prove firmware behavior or prevent a fault |
| Optional mitigation | Disabled default, caller authorization, finite identity-bound lease, strict downward-only rules, serialized audit/write/readback, REQUESTED/ACCEPTED/VERIFIED/FAILED lifecycle, bounded load-reduction observation, unobservable low-load state, restart re-arm, and adversarial fakes are covered | The production daemon has no power-limit write adapter, privileged helper, or observation deadline runner; mitigation is not enabled or hardware-tested |
| Files | Atomic status, daily telemetry, transition events, models, incidents, baseline identity, and single-writer behavior are covered | Disk retention is manual and can grow until archived or deleted |
| GUI | Typed source/freshness/load, reference/model/incident/watchdog states, synthetic history, acknowledgement, bounds, truncation, unavailable state, close-to-tray, reopen, and tray lifecycle are covered | WPF rendering and Windows notification behavior can vary by framework and host |
| Memory hardening | Bounded buffers, FIFO identifier sets, large-record handling, locked-file recovery, pooled history reads, redraw retention, and native worker behavior are covered in finite tests | These tests are not a multi-day driver or WPF reliability certification |

## Memory-hardening results

The 8 September 2026 hardening pass compared the previous and bounded history readers with the same generated input. Both retained 132,626 samples after loading and repeated refreshes. The measured component peaks were:

| Metric | Previous reader | Hardened reader |
| --- | ---: | ---: |
| Peak resident memory | 355.6 MiB | 129.7 MiB |
| Peak private memory | 340.5 MiB | 111.1 MiB |
| Managed allocation during load | 683.7 MiB | 459.4 MiB |
| Retained managed memory after collection | 49.7 MiB | 49.5 MiB |
| Initial load plus six refreshes | 878 ms | 773 ms |

The streaming fixture was capped at 200,000 samples while 200,000 additional synthetic rows were appended. Retained memory stopped following record count. One hundred synthetic WPF redraws kept retained managed memory near 34.5 MiB with 1,633 handles in the measured batches; whole-process residency still rose as framework/rendering caches and heap residency were included.

The native worker reproduction showed the old per-call-thread path at 518 handles before forced collection after 100 calls, versus 233 for the hardened worker. The hardened worker measured 239 before forced collection after 1,000 calls and 233 after collection. This supports removal of the reproduced per-poll growth pattern; it is not proof that every driver or framework allocation is leak-free.

The production limits behind those results include bounded sample and warning insertion, 4,000 notification IDs, 2,000 acknowledgement IDs, 200 GUI monitoring-loss entries, a bounded initial history tail, pooled 16 KiB reads, an 8 MiB per-file refresh budget, and rejection of records over 256 KiB. Older telemetry remains in the original files, so disk retention still requires deliberate operator management.

## Physical support boundary

The historical physical validation used one ASUS TUF RTX 5090 configuration on Windows x64 with NVIDIA driver `616.56` and the exact native identity `PCI0x2B8510DE`, subsystem `0x89EE1043`. That scope does not certify every card sold under the same product name. Same-board units are experimental until independently checked. Multiple GPUs fail closed. In 1.5.0, current acceptance requires an authenticated catalog decision for the exact hardware, driver and reader. The maintainer explicitly approved 616.56 and 616.92 on 2026-09-10 for this scope. The [616.92 evidence record](DRIVER-616.92.md) remains a structural check with no independent idle/workload comparison; approval does not expand the measurements performed.

A bounded read-only smoke check of the public source on that original ASUS TUF RTX 5090/driver combination produced three valid native samples and exited cleanly with exit code 0 and `stopped=true`, `stop_reason=sample_limit`. No production monitor remained running afterward. This finite check does not validate other boards, other driver versions, multi-GPU systems, long-duration operation, or every release-package build.

The public configuration defaults to automatic UUID detection and uses the single-GPU guard in both the NVML and NVAPI paths. The source contains no machine-specific UUID allowlist. Missing or revoked driver approval pauses private readings with an explicit approval status while public telemetry remains available. An invalid hardware identity leaves the private source unavailable. Private native integrity failures and possible timeouts remain terminal; the daemon does not silently substitute another voltage provider.

## What these checks cannot establish

No offline or aggregate-voltage check can establish per-pin current balance, connector contact temperature, contact resistance, or protection before a burned connector. GPU temperature is not connector temperature. Total board power includes slot power, and one-hertz samples can miss a transient. A normal status means only that the configured comparison did not trip under the observed conditions.

The repository checks also do not certify long-duration operation across NVIDIA driver updates, arbitrary WPF/runtime versions, sleep/resume cycles, unexpected power loss, or a public package that has not yet been confirmed on the target machine. Revalidate the exact package, board, driver, source timestamp, and cabling before relying on it.

## v1.1.3 automatic UUID selection

Offline coverage checks blank/null/auto settings, explicit UUID precedence, missing and multiple GPUs, malformed UUIDs, and GUI parsing with and without the new status field. A bounded three-sample live check with a blank UUID succeeded on the original supported GPU and stopped cleanly. Hardware support is unchanged.

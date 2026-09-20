# Automated longitudinal prediction review

[Tracking issue #19](https://github.com/Shaderx/ConnectorWatch/issues/19) records
the scope and measurement limits. The offline evaluator compares voltage models
and experimental early advisories as ordinary-use telemetry accumulates. It
does not query the GPU, stop the monitor, change an accepted reference, or apply
an experimental model to live alerts.

## Retained evidence

The daemon preserves every recorded observation. The current UTC day and the
previous two days stay in CSV; older files become verified, lossless `.csv.gz`
archives. Archives have no age-based deletion. Compression is not downsampling,
so observations older than a week remain available at their original cadence.
Storage needs grow over time; monitoring gaps and unflushed samples cannot be
reconstructed from an archive.

Evaluation reads completed UTC days from CSV and gzip. It uses qualified minute
summaries to bound memory and reduce the influence of repeated rapid samples;
these summaries exist only in the evaluation and do not replace source data.
GPU/source/load/provenance cohorts remain separate. Exclusions, duplicates,
malformed rows, coverage limits, file inventory, and the input cutoff are
reported. Insufficient evidence stays insufficient.

## Run a review

From the repository root, with the .NET 8 SDK installed:

```powershell
.\scripts\Review-Prediction.ps1
```

The script reads `%LOCALAPPDATA%\ConnectorWatch\config.json` to locate telemetry,
builds the offline evaluator, and saves timestamped JSON and Markdown reports
under `artifacts/prediction-reviews`. A separate manifest links the preceding
report and records the evaluator assembly hash. It does not publish raw telemetry.
Use `-DataDirectory` for another recording directory, `-OutputDirectory` for
another report location, or `-AsOf YYYY-MM-DD` for an exclusive UTC-day cutoff.
Reports must be outside the source data directory. Previous reports are retained.

The same helper is included in the published Windows package. From the
installed application directory, run it with PowerShell:

```powershell
.\Review-Prediction.ps1
```

When `ConnectorWatch.exe` is beside the script, it uses that packaged evaluator
directly and does not require the .NET SDK or a source checkout. The default
configuration remains `%LOCALAPPDATA%\ConnectorWatch\config.json`, and reports
are written to `%LOCALAPPDATA%\ConnectorWatch\prediction-reviews`. Use
`-DataDirectory`, `-OutputDirectory`, or `-AsOf YYYY-MM-DD` to select another
recording directory, report location, or exclusive UTC-day cutoff. The script
keeps prior reports and rejects a report directory inside the telemetry
directory.

The equivalent command after building is:

```powershell
dotnet .\src\ConnectorWatch\bin\Release\net8.0\ConnectorWatch.dll --evaluate-prediction C:\recordings --output C:\reports\prediction.json
```

The command is dispatched before native initialization. `--prediction-self-test`
runs the focused offline checks; `--self-test` includes them in the complete
daemon suite.

## Interpreting the comparison

Models and advisory parameters are learned in chronological partitions, using
earlier training days, subsequent calibration days, and later held-out test days.
The initial training/calibration days remain fixed as more test days arrive.
Overlapping weekly reports reuse evidence and are not independent experiments.
Input files, cutoffs, model availability, and matched comparison coverage should
be checked before interpreting a lower prediction error as an improvement.
Connector power calculated from the same voltage being predicted is a coupled
input. Its model errors are retained as diagnostic comparisons, but those models
are excluded from winner ranking and from the calibrated advisory. Temperature
and load ranges are reported so a narrow operating range remains visible.

Residual-noise calibration supports an explicitly experimental advisory. The
noise estimate measures variation around the calibration median; a persistent
offset is reported separately and does not move the accepted voltage baseline.
The report distinguishes observed advisory activity from simulated step/ramp
sensitivity. Simulations are applied after fitting, and derived connector power
must change consistently with an injected voltage change. No threshold is an
established damage limit. Native sensor timing, including cross-rail alignment,
remains unverified where the source reports host-poll timestamps.
Simulated detection latency is elapsed wall-clock time and can include long
recording gaps; it is not a measurement of continuous hardware warning performance.

The weekly review compares prediction error, data coverage, advisory transitions
per observed hour, and simulated detection delay. It also checks that raw files
and archives remain available and that new completed recordings have arrived.
Reports should identify an improvement or regression only on comparable data;
unlabeled ordinary-use recordings cannot establish a hardware false-positive or
false-negative rate, a failure probability, or the remaining life of a connector.

## Preserve the physical setup

This workflow does not require unplugging, reseating, or disturbing the connector,
nor deliberately creating a fault. The user's longitudinal study keeps the contact
condition undisturbed. A hardware/configuration change reported independently
requires a new comparison cohort rather than rewriting prior observations.
Aggregate voltage cannot measure individual contact temperature or current
balance, even when a prediction is accurate.

## Scheduling

A weekly Codex task review is configured for the working installation. It invokes
`Review-Prediction.ps1`, compares the newest reports, and brings material findings
back to the existing task on Mondays at 10:00 AM Asia/Kuala_Lumpur. Scheduling is
separate from GPU sampling: the monitor
continues collecting data independently. The scheduled review needs access to
this checkout, the recordings, and the .NET SDK; an unavailable host or failing
command is a review failure, not a healthy reading. Keep the computer powered on
and Codex running for a scheduled review that needs these local files.

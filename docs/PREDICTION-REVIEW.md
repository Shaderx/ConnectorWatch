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

## Automatic app reviews

Starting with 1.5.5, the monitoring daemon runs the offline evaluator in a separate
background process. The dashboard does not need to stay open. The first run is
due when the daemon starts without a saved review. Later runs are due seven days
after a completed review, including a review with insufficient data. If the
computer or daemon was stopped, the next start performs the overdue review.
Each run excludes the current UTC day.

The dashboard's **Experimental shadow review** panel shows review status, the
last completion and next attempt, coverage, prediction errors on matched rows,
exclusions, and simulated detection results. Its history selector retains the
12 most recent summaries, subject to the summary file size limit. Failed and stale reviews remain visible; a failed
attempt does not replace the last completed result. These results do not change
live alerts or accepted references.

App reviews are local files under
`%LOCALAPPDATA%\ConnectorWatch\shadow-reviews\<data-directory-hash>`.
`state.json` contains scheduling state and recent summaries. The `reports`
subdirectory retains immutable JSON and Markdown reports. A directory-specific
lock prevents duplicate review workers. Reports are outside the telemetry
directory, and neither source recordings nor earlier reports are overwritten.
Manual helper reports remain in their existing directories.

Starting with 1.5.7, each accepted review also saves an immutable completion
record under `completions`. The scheduler writes this record after the evaluator
exits successfully and its report passes validation, before it updates
`state.json`. The record binds the data directory, run, actual completion time,
input cutoff, report digest, and compact summary.

At startup, the scheduler checks these records and repairs missing or stale
history. A recovered completion keeps its original completion time and weekly
deadline. A newer distinct attempt keeps its failure or stopped retry deadline.
If only the final snapshot write fails, the accepted completion stays in memory
and the scheduler retries that write.

Older reports without a completion record remain available in a separate
completion-unverified list. Their filenames and readable contents cannot prove
that the scheduler accepted completion. They do not enter completed history or
change its schedule. Existing accepted history remains valid. Scans are bounded;
the dashboard reports incomplete scans or recovery problems instead of claiming
that every retained file was checked.

The dashboard snapshot is limited to 256 KiB. Long warning lists and detail text
are shortened in summaries, with a note directing readers to the full report.
Older cached summaries can be omitted to keep the snapshot within that limit.
Their full reports remain available. The scheduler accepts evaluator reports up
to 16 MiB; an oversized report is a visible review failure.

A failed attempt is retried after six hours. A child process that exceeds
15 minutes is stopped and reported as a failed review. Stopping the daemon also
stops its review child. The installed evaluator requires no .NET SDK. Review
errors do not stop live sampling. An insufficient result means the review ran
but lacked the evidence required for comparison; it does not mean a connector
passed a safety test.

## Run a manual review

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
`--shadow-review-self-test` runs isolated scheduler and report-store checks,
including an evaluator child process with synthetic or empty input.

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
Simulated detection latency is elapsed wall-clock time and can include recording
gaps; it is not a measurement of continuous hardware warning performance.

Starting with evaluator `shadow-regression-v2`, synthetic scenarios use a fixed
six-hour window from the first held-out observation. Injection begins 30 minutes
after that observation. A step holds its specified drop. A gradual ramp reaches
its final drop over 30 wall-clock minutes, then holds it. The unmodified comparison
uses the same window. An unfinished window or missing usable post-injection
observations produces an unavailable scenario result. Native advisory replay
still uses all held-out observations.

Reports identify the algorithm, evaluation settings, frozen training/calibration
anchors, and synthetic scenario evidence. Compare scenario identities before
comparing detection delays. Adding later days does not move an established
scenario's onset or ramp. Reports from the earlier moving-window evaluator have
different test conditions and must not be treated as comparable sensitivity
measurements. Cumulative held-out error can still change because later reviews
include more observations.

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

## Existing weekly task

The existing weekly Codex review can continue during rollout. It invokes the
manual helper, compares reports, and reports material findings in the existing
task. Keep that task until an installed app review has completed and its history,
failure reporting, and next scheduled run have been verified. Installing the app
does not remove the Codex task. After verification, the separate task can be
retired or retained for interpretation of new evidence. The app's automatic
reviews require the monitoring daemon; the Codex task requires Codex, the
checkout, recordings, and .NET SDK.

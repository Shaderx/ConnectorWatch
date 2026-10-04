# ConnectorWatch 1.5.5

Prediction reviews now run automatically inside the monitoring app. The daemon
starts a separate offline evaluator when a review is due, then schedules the
next review seven days after completion. Reviews continue with the dashboard
closed and catch up after a restart. They use completed UTC days and require
neither Codex nor a .NET SDK in the installed app.

The dashboard adds **Experimental shadow review** with recent history, coverage,
exclusions, matched prediction errors, and simulated detection results. A failed
review remains visible alongside the previous completed result. Failed attempts
retry after six hours; each evaluation has a 15-minute limit. Full reports remain
local and separate from recorded telemetry.

Synthetic steps and ramps now use fixed timestamps and durations. Model,
configuration, and scenario identities make changes in test conditions explicit.
The previous moving-window simulation results are not directly comparable with
this version. An incomplete scenario remains unavailable.

These are experimental reviews. They do not promote a prediction model, change
live alert thresholds, or measure connector-damage probability. The update
preserves recorded observations, accepted references, configuration, and the
existing installer startup preference. No physical connector intervention is
required. See [prediction reviews](PREDICTION-REVIEW.md) for details.

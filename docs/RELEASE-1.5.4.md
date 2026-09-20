# ConnectorWatch 1.5.4

This release adds offline prediction reviews of retained connector-voltage
recordings. It compares current-based models on chronological training,
calibration, and later test days, and reports coverage, prediction error,
experimental advisory activity, and simulated voltage-drop sensitivity.
Power derived from the voltage being predicted remains diagnostic only.

The installer includes the evaluator and `Review-Prediction.ps1`. The packaged
helper uses the installed application without a .NET SDK or source checkout and
saves timestamped reports separately from telemetry. See
`docs/PREDICTION-REVIEW.md` for installed and source-checkout usage. Weekly review
scheduling is configured separately; installing this update does not create a
new schedule.

Fine-grained telemetry remains available beyond a week: older recordings are
losslessly compressed, with no age-based deletion. Reviews read these archives
without changing recorded observations or requiring connector disconnection.
The results are experimental evidence for future improvements, not a calibrated
failure probability or a diagnosis of physical damage. Live alerts and accepted
references continue to use the existing monitoring behavior.

Download **ConnectorWatch-Setup-1.5.4.exe** and run it to update the installed app.
The upgrade preserves configuration, accepted references, recorded history, and
the existing installer startup preference. This patch retains the current
hardware support and self-signed publishing identity.

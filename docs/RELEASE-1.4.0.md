# v1.4.0 — electrical trends, quieter storage

A Windows-first release with a simpler dashboard overview and less routine disk activity.

- Added electrical degradation confidence over 7, 30 and 90 days, defaulting to 30. Higher scores mean stronger evidence of sustained voltage decline, not a calibrated probability of damage.
- Added a detailed load-matched electrical trend with observed variation and sample context.
- Separated genuinely stale or missing telemetry from unverified sensor timing and references that are still learning.
- Kept confidence history in memory with changed checkpoints every 30 minutes and on normal GUI exit; unchanged checkpoints are skipped.
- Automatically compressed older daily telemetry to verified gzip archives in the background. Recent CSV data remains available to live charts, and confidence history can read either format.
- Preserved comparison anchors and source/reference epochs through restart, archive conversion and history expiry. Missing days remain gaps.
- Avoided rewriting unchanged baseline checkpoints.
- Replaced the front-page README with a concise Windows getting-started guide; retained detailed documentation in the user guide.

## Upgrade

Stop both GUI and monitor, extract the new Windows x64 package, and preserve `config.json` and your data directory. Keep `confidence-history.json` with the recordings, including `.csv.gz` files. Update both executables together. GUI/monitor diagnostics remain under `%LOCALAPPDATA%\ConnectorWatch\logs`.

No GPU access, reference acceptance, power-limit change or other hardware action is performed by the build tests. Direct-reader hardware support is unchanged. This remains an experimental prerelease with unsigned Windows executables.

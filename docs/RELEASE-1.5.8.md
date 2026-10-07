# ConnectorWatch 1.5.8

Verified driver upgrades preserve the accepted baseline and historical signal
degradation confidence. The baseline keeps its original driver, source,
acceptance record, and values. New observations can continue the same comparison
when signed driver approval applies and the hardware, reader profile, analysis
settings, and measurement semantics remain compatible.

The app also preserves the fitted model and incident state for compatible driver
updates. It recovers an existing reference invalidated only by a verified driver
change. Current driver provenance remains in recorded telemetry.

Historical comparisons remain visible while a new driver awaits approval. Live
cross-driver comparisons resume after approval; a developer bypass does not
qualify them. Changes to hardware, reader profile, source, or analysis settings
still require a compatible reference.

The confidence cache is rebuilt from retained measurements so compatible driver
changes do not split the comparison history. Recorded CSV and gzip files remain
unchanged.

Download **ConnectorWatch-Setup-1.5.8.exe** to update the installed app. Keep the
existing reference and telemetry; recovery runs when monitoring starts.

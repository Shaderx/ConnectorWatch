# v1.2.0 — hybrid telemetry storage

Per-sample status replacement could terminate monitoring when another process locked the destination file. Live readings now travel from bounded daemon RAM to the GUI over the existing current-user control pipe. Routine telemetry, transitions, status and baseline are checkpointed every 30 seconds by default. Warning/failure transitions and graceful shutdown flush promptly.

- `FlushSeconds`: 1–60, default 30; existing configurations inherit the default without being rewritten.
- Recent pipe history: at most 512 observations and 512 KiB of row text. GUI history merges disk and RAM without double-counting a flushed observation.
- Pending telemetry is bounded and checkpoints early at 1 MiB of text; oversized records and exhausted storage retries fail visibly.
- Temporary Windows sharing/access conflicts receive bounded backoff. Appends are never replayed after a potentially partial write.
- Fatal diagnostics are retained separately under `%LOCALAPPDATA%\ConnectorWatch\logs` with bounded rotation.
- Upgrade GUI and daemon together. `status.json` is now a checkpoint; live consumers use `hello`, then `live` with the returned instance identity.

Active data must remain outside cloud-sync folders. Existing data locations and baseline identity are preserved; this release does not automatically migrate data. Abrupt termination can lose pending samples, and normal OS buffering does not guarantee power-loss durability.

Validation includes offline daemon tests for buffering, rollover, flushes, bounds, configuration and Windows file locks; real pipe tests for identity and bounded history; GUI tests for RAM-only readings above 16 KiB, checkpoint deduplication and stale fallback; and a local 35-sample read-only GPU capture verifying no routine checkpoint at five seconds, the 30-second checkpoint, and all 35 observations saved on graceful completion. Hardware support remains unchanged and experimental.

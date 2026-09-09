# ConnectorWatch

A Windows-first dashboard for monitoring RTX 5090 input-rail voltage and electrical trends over time.

[Download for Windows](https://github.com/Shaderx/ConnectorWatch/releases/tag/v1.4.0) · [User guide](docs/USER-GUIDE.md) · [Release notes](docs/RELEASE-1.4.0.md)

![Electrical degradation confidence chart with synthetic demonstration data](docs/confidence-preview.png)

*Synthetic demonstration. The percentage summarizes evidence of voltage decline; it is not a measured probability of hardware damage.*

## A clearer view of your readings

- **Live dashboard** — input voltage, power, PCIe supply, warnings and sensor status.
- **Long-term confidence** — daily comparisons across **7, 30 or 90 days**, with 30 days selected by default.
- **Detailed electrical trends** — load-matched voltage changes, observed variation and sample details for closer inspection.
- **Less disk activity** — buffered telemetry, automatic compression of older recordings, and periodic confidence checkpoints.

## Start on Windows

1. Download the **Windows x64 ZIP** and extract the entire folder. The .NET runtime is included.
2. Run **`ConnectorWatch.Gui.exe`** or **`Start-GUI.cmd`**.
3. Leave `GpuUuid` blank for automatic detection of one NVIDIA GPU. See the supported setup below before using the direct reader.

Closing the window keeps monitoring in the system tray. Use **Stop monitoring and exit** to stop both applications, or **Exit GUI — keep monitoring** to close only the dashboard.

## Reading the confidence chart

**Up means stronger evidence of a sustained voltage decline under comparable load.** The score combines the size and persistence of the decline with the number of comparable days. It can fall when measurements recover.

The chart needs at least **three comparable completed days**. Missing, stale or insufficient measurements stay unscored. They do not become a reassuring zero. Expand **Comparison details** for the selected load band and measurement limitations.

The [user guide](docs/USER-GUIDE.md#long-term-degradation-confidence) explains the calculation and the detailed chart.

## Storage that stays out of the way

| Data | Storage policy |
| --- | --- |
| Raw telemetry | Buffered in memory; normally flushed every **30 seconds**. Warnings, shutdown and buffer limits can flush sooner. |
| Older recordings | The current UTC day and previous two days remain CSV. Older days are automatically compressed to **`.csv.gz`** in the background. Archives are retained. |
| Confidence history | Kept in memory, with changed history saved every **30 minutes** and on normal GUI exit. Initial backfill can save immediately; unchanged history is not rewritten. |
| Live charts | Read live data and use a bounded recent history in memory. |
| Diagnostics | Rotating GUI and monitor logs under `%LOCALAPPDATA%\ConnectorWatch\logs`. |

The confidence chart reads compressed recordings directly. Compression verifies the archive before removing its original CSV. An interrupted checkpoint can be rebuilt from retained recordings; an abrupt stop can still lose unflushed live samples.

## Upgrade without losing history

Stop monitoring before replacing a running installation. Extract the new release into a fresh folder, preserve your **`config.json`**, and retain the existing **data directory**, including **`confidence-history.json`** and compressed recordings. If using a new folder, copy the data or point `DataDirectory` at its existing location. Upgrade the GUI and monitor together.

Diagnostic logs remain outside the release folder and survive upgrades. Data is never automatically moved to a different installation.

## Supported setup

**Experimental Windows x64 release.** The direct reader was physically validated on one **ASUS TUF RTX 5090**, NVIDIA driver **616.56**, with one NVIDIA GPU. Other boards and driver versions are unsupported by that reader; multiple GPUs fail closed. See [validation and limits](docs/VALIDATION.md).

ConnectorWatch measures voltage trends, not individual contact temperature or connector safety. It cannot guarantee a warning before damage. It does not change GPU settings or power limits.

## Further reading

[Configuration and operation](docs/USER-GUIDE.md) · [Architecture](docs/ARCHITECTURE.md) · [Offline replay](docs/REPLAY.md) · [Build and release](docs/RELEASING.md)

[MIT license](LICENSE). Bundled runtimes retain their own licenses and notices. Windows executables are currently unsigned.

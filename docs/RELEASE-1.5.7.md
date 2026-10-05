# ConnectorWatch 1.5.7

The app now repairs saved shadow-review history from durable completion
records. Each new successful review saves its completion record before updating
the compact history index. After a restart, the scheduler can recover a missing
entry and its original weekly deadline. A newer failed or stopped attempt keeps
its retry deadline.

The dashboard shows retained reports without verified completion separately
from completed reviews. You can open these reports, including reports from
older versions. They do not change the review schedule or count as completed
history. Recovery diagnostics show when a scan is incomplete or a record cannot
be used.

This update preserves telemetry, existing reports, accepted references, live
alerts, driver approvals, and the private reader ABI. It does not infer why an
older history index became stale or retroactively certify an older report.

Download **ConnectorWatch-Setup-1.5.7.exe** to update the installed app. Recovery
runs automatically when its monitoring daemon starts. The installer retains the
existing startup preference and publisher identity.

# ConnectorWatch 1.5.6

Monitoring now continues when the host UTC clock moves backward. The daemon
starts a new coverage interval with an explicit gap and keeps actual UTC
timestamps. Poll durations and sampling progress still use monotonic time.
Source freshness and analysis availability continue to follow the existing
gates. This fixes [the clock correction crash (#20)](https://github.com/Shaderx/ConnectorWatch/issues/20).

After an unexpected failure, the daemon attempts to mark its status checkpoint
stopped while it still owns the data-directory lock. It preserves the last
measurement time and records the stop time separately. The original diagnostic
and nonzero exit remain visible if the final status write fails. This fixes
[the stale running checkpoint (#21)](https://github.com/Shaderx/ConnectorWatch/issues/21).

Deterministic offline tests exercise both fixes through the production sampling
loop with an injected clock and source. They require no GPU or machine-clock
changes. The update preserves configuration, stored samples, accepted references,
archived history, driver approvals, native guards, and the private reader ABI.

Download **ConnectorWatch-Setup-1.5.6.exe** to update the installed app. The
installer retains the existing startup preference and self-signed publisher
identity.

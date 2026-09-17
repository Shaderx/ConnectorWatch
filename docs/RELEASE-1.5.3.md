# ConnectorWatch 1.5.3

Electrical degradation confidence now replays compatible retained measurements
against the accepted reference, including measurements recorded before reference
acceptance. Existing daily CSV and compressed logs can contribute comparable days
without waiting to collect the same evidence again.

The dashboard identifies this as retrospective analysis. Replayed history uses a
separate cache and is rebuilt when the accepted reference changes. Original logs,
reference acceptance records, and live monitoring remain intact.

The existing load qualification, source identity, measurement quality, exposure,
and comparability checks still apply. EDC continues to require three comparable
completed UTC days; the incomplete current day does not receive a final score.
Waiting messages explain the evidence still needed.

Download **ConnectorWatch-Setup-1.5.3.exe** and run it to update manually.
Configuration, accepted references, and recorded history are preserved on upgrade.
This patch does not change native hardware support or the signing identity.

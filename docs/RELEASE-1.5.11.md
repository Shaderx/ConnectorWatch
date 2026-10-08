# ConnectorWatch 1.5.11

Accepted references with an unfitted differential model can now recover a fitted
model from retained telemetry. The repair uses qualified samples recorded before
reference acceptance. It preserves the accepted reference and historical
comparisons. Import checks the reference identity, model settings, and existing
artifact before it replaces the unfitted model. A receipt records the inputs and
old and new model hashes.

Driver validation now checks sensor response across the independent idle and
workload phases. It also checks polling cadence, timing variance, and oracle
pairing. The existing signed catalog binds these results through the evidence
digest. The dashboard displays the verified response and timing results with
measured timing values. Live acquisition failures retain their specific status.

The verification procedure remains agent-led. The agent reviews the complete
evidence and publishes a signed catalog after all required checks pass.

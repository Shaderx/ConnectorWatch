# NVIDIA 616.92 maintainer approval and compatibility record — 2026-09-10

Shaderx explicitly approved this existing driver for inclusion in the initial
signed catalog on 2026-09-10. The approval is an auditable maintainer attestation
for the exact ASUS TUF RTX 5090 reader scope (PCI 2B8510DE, subsystem 89EE1043,
Windows x64, `A612-A613-v1`). Its validation basis is the following focused
structural check:

- A612 metadata and A613 status returned NVAPI_OK; buffer guards passed.
- The complete A612 response was byte-identical to the validated 616.56 capture
  (SHA256 05D322730914C57173BB2D91E299D9305D1BDF4AF156D009B3A220745353A5F5).
- Both old and new captures passed the production metadata/status decoder.
- 30 consecutive daemon samples had 30 distinct timestamps and voltage values,
  no missing input voltage, and a clean sample_limit shutdown.
  Connector voltage ranged from 12.055999 to 12.094242 V at ordinary desktop load.
- Targeted DirectNvRails offline fixtures passed, including the version gate,
  opt-out, unknown version rejection, and failed version-read rejection.

Raw evidence remains in the maintainer's local validation directory. This was
not a full suite rerun or an independent HWiNFO/load comparison; HWiNFO was not
running. Native freshness remains unverified by the driver. The approval does
not turn these missing measurements into evidence and makes no broader hardware,
freshness, connector-safety, or cross-driver claim. See the public
[attestation](release/driver-approval-attestation.json).

## Driver version option

`ValidateDriverVersion` defaults to `true`; acceptance now requires a verified
catalog decision for the exact identity and embedded reader.
Set `"ValidateDriverVersion": false` in config.json and restart the monitor
as an advanced local developer escape hatch for a missing approval. An explicit
revocation still blocks the reader, and diagnostics label the session unvalidated. GPU identity,
metadata/channel layout, return-code, timeout, buffer-integrity, and reading
plausibility checks remain active. Failed driver-version queries still stop startup.
The source description and reference identity record the actual installed version.
A driver change can invalidate an existing reference; saved evidence is retained.

# Maintainer driver validation and catalog publication

This procedure creates reviewable evidence and a catalog-entry proposal. The
validation tool never approves a driver, signs a catalog, or publishes a
release. A proposal remains inert until a maintainer reviews its exact digest
and authorizes the protected publication environment.

The normal path below requires full independent idle and workload evidence for
every new driver approval. Separately, Shaderx explicitly approved the two
existing documented versions, `616.56` and `616.92`, for the initial catalog on
2026-09-10. The public [bootstrap attestation](release/driver-approval-attestation.json)
records that `616.56` has historical physical validation while `616.92` has only
structural validation without an independent HWiNFO comparison. Approval does
not rewrite or strengthen those evidence claims.

## One-time initial catalog attestation

The bootstrap path exists only to migrate the two pre-catalog decisions into
catalog revision 1. It requires `docs/release/driver-approval-attestation.json`,
rejects proposal paths and previous catalog state, and is accepted only when the
record contains exactly drivers `616.56` and `616.92` for `10DE/2B85/89EE1043`,
Windows x64, reader profile `A612-A613-v1`, reader range `[1.0.0, 1.1.0)`, and app
range `[1.5.0, 1.6.0)`. The fixed policy checks the validation record and level
for each version. It rejects omissions, duplicates, future versions, ranges, and
scope changes.

Prepare revision 1 with:

```powershell
./scripts/Publish-DriverCatalog.ps1 `
  -Mode Prepare `
  -BootstrapAttestationPath ./docs/release/driver-approval-attestation.json `
  -CatalogRevision 1 `
  -IssuedUtc '2026-09-10T13:00:00Z' `
  -ExpiresUtc '2026-10-10T13:00:00Z'
```

The publisher hashes the exact attestation bytes into both entries, copies the
record beside the immutable catalog assets, then follows the same exact-payload
digest authorization and protected signing gates as every other publication.
After revision 1, the bootstrap parameter is invalid. Renewals carry authenticated
decisions forward; new approvals return to the full validation proposal path.

## Isolated validation

Run from a clean checkout of the exact commit being tested. The script rejects
tracked modifications and untracked files so `tool_commit` identifies the
actual shipping reader bytes; commit the intended source before validation. The acknowledgement
is deliberately explicit because the command calls private, read-only NVIDIA
interfaces:

```powershell
./scripts/Validate-Driver.ps1 `
  -GpuUuid 'GPU-00000000-0000-0000-0000-000000000000' `
  -AcknowledgePrivateProbe `
  -ApprovedMetadataResponseSha256 '<last approved complete A612 SHA-256>' `
  -OutputDirectory C:\driver-validation
```

The public command creates a new `validation-<UTC>-<random>` directory and
starts a nonce-gated child process with that directory as its working and data
directory. Only the child can create the diagnostic probe. This isolated probe
may examine an unknown or revoked driver, while the ordinary monitor still
honors revocations. The native implementation retains single-GPU target checks,
fixed request sizes, return-code checks, buffer canaries, decoding bounds, and
the watchdog. A timed-out child is terminated as a process tree.

The private request containing the GPU UUID is removed after the child exits.
`evidence.json` contains the exact PCI/subsystem, OS/architecture, driver,
reader/app versions, tool commit, sanitized raw A612/A613 responses and hashes,
native return codes, guard and timeout outcomes, and decoded sample progression.
Known UUID, user-profile, and user-name byte sequences are cleared from exported
responses and counted as redactions. Paths and the child nonce are never part of
the evidence model.

A structural run writes `NO-APPROVAL-PROPOSAL.txt` and has outcome
`provisional_structural_smoke_passed`. It cannot produce `proposal.json`.

## Independent recorded oracle

Full validation adds `-OracleInput <file>`. The oracle is a recorded, paired
measurement set from an independent instrument. It must name the exact observed
scope, contain exactly `idle` and `workload` phases with at least two samples
per phase, and provide independent voltage/current readings for both PCIe +12 V
and 12VHPWR. Every pair includes the corresponding guarded raw A613 response,
so the shipping decoder performs the comparison rather than a parallel parser.

The oracle also records the SHA-256 of the last approved complete A612 response
in `approved_metadata_response_sha256`. The live A612 response must match it.
This makes the metadata-contract comparison explicit and reviewable. A future
intentional contract change requires a tested application/reader release; it is
not a driver-only catalog approval.

Minimal shape (values are illustrative only):

```json
{
  "schema_version": 1,
  "vendor_id": "10DE",
  "device_id": "2B85",
  "subsystem_id": "89EE1043",
  "os": "windows",
  "architecture": "x64",
  "driver_version": "616.92",
  "reader_profile": "A612-A613-v1",
  "approved_metadata_response_sha256": "64 lowercase hex characters",
  "voltage_absolute_tolerance_v": 0.15,
  "current_absolute_tolerance_a": 1.0,
  "current_relative_tolerance": 0.10,
  "phases": [
    {
      "name": "idle",
      "samples": [
        {
          "status_response_base64": "complete guarded A613 response",
          "native_return_code": 0,
          "guard_status": "pass",
          "timed_out": false,
          "pcie12_v": { "voltage_v": 12.05, "current_a": 0.50 },
          "twelve_v_hpwr": { "voltage_v": 12.02, "current_a": 1.50 }
        }
      ]
    },
    { "name": "workload", "samples": ["same object shape; at least two samples"] }
  ]
}
```

The voltage mean must remain within the recorded absolute tolerance. Current
must remain within either the absolute or relative tolerance. Scope mismatch,
metadata mismatch, malformed raw responses, failed native operations, missing
phases, or any failed comparison produces outcome `failed` and no proposal.

When all structural and independent checks pass, `proposal.json` contains an
exact `approved` entry wrapped in `proposal_status:
maintainer_review_required`. This is still a proposal. Its evidence and scope
digests bind the review to the captured files.

## Digest review and protected signing

Catalog releases use tags `driver-catalog-r<revision>` and never share the
application `v*` release track. Revisions strictly increase, immutable catalog
release assets are never replaced, and the mutable discovery pointer is
uploaded only after the immutable release succeeds.

Prepare the exact payload locally with no key:

```powershell
./scripts/Publish-DriverCatalog.ps1 `
  -Mode Prepare `
  -ProposalPaths C:\driver-validation\validation-...\proposal.json `
  -CatalogRevision 2 `
  -IssuedUtc '2026-09-10T12:00:00Z' `
  -ExpiresUtc '2026-10-10T12:00:00Z' `
  -PreviousEnvelopePath C:\catalog\catalog.envelope.json `
  -TrustedPublicKeySpkiBase64 '<pinned public key>'
```

Review `catalog.payload.json`, each evidence bundle, scope and rationale. Copy
the exact lowercase `payload_sha256` from `authorization-request.json` into the
manual workflow input. The authorization sentence must be exactly:

`I authorize this exact catalog payload digest`

The workflow runs only from a protected branch and uses the GitHub Environment
`driver-catalog-publication`. Configure required maintainer reviewers on that
environment. Store the P-256 private PEM only as environment secret
`DRIVER_CATALOG_SIGNING_KEY_PEM`; store its non-secret key identifier as
environment variable `DRIVER_CATALOG_KEY_ID`. Do not add a real key,
authorization record, or approval claim to the repository. Store the matching
non-secret SubjectPublicKeyInfo base64 as environment variable
`DRIVER_CATALOG_PUBLIC_KEY_SPKI_BASE64`; the publisher uses it to authenticate
the prior immutable envelope before carrying entries forward.

For a catalog key rotation, first ship a signed application that trusts both
the old and new catalog public keys. Then change the protected current signing
key variables and set `DRIVER_CATALOG_PREVIOUS_KEY_ID` plus
`DRIVER_CATALOG_PREVIOUS_KEY_SPKI_BASE64` to the explicitly pinned old key. The
publisher authenticates current catalog state with that old key while requiring
the new private key to match `DRIVER_CATALOG_PUBLIC_KEY_SPKI_BASE64`. After the
new-key catalog is deployed, a later signed application may retire or revoke the
old trust key. Clear the two previous-key variables after the transition. Never
obtain either public key from the catalog envelope being verified.

For normal proposals, the publisher recomputes evidence hashes, rejects failed or provisional
evidence, checks exact scope, accepts only shipping profile
`A612-A613-v1`/`1.0.0`, requires a monotonically higher revision, authenticates
previous state, and passes the finished payload through the shipping
`DriverCatalogPayload` parser. Only then
does it compare the maintainer-authorized payload digest and access the signing
secret. It writes a P-1363 ECDSA P-256 envelope over the exact payload bytes.
The workflow creates the immutable catalog release before replacing
`catalog-current.json` on the dedicated `driver-catalog` discovery release. That discovery asset
is the same signed envelope, so the runtime verifies it directly; its mutable
location is only an alias to the publish-last bytes.

For an urgent revocation, create a higher revision with the same exact scope,
`decision: revoked`, a public reason, and supporting evidence. Never delete or
overwrite the earlier approval. The validation command's isolated escape hatch
remains available for maintainer diagnosis of a revoked driver; ordinary users
cannot use it to start the production reader.

A revocation proposal uses the same proposal and entry fields, with
`decision: revoked`. Its adjacent `evidence.json` uses schema version 1,
`evidence_type: connectorwatch-driver-revocation`, outcome
`revocation_supported`, and the same exact `scope` object. The publisher checks
its digest and scope before replacing the earlier exact-scope decision. A
validity renewal supplies no proposal paths; it authenticates the previous
envelope, increments the revision, and changes only the catalog issue and expiry
times. Neither route can silently widen an identity scope.

Renewal is intentionally manual because signing remains behind required
environment review. Maintainers should prepare and authorize a renewal weekly,
well before the 30-day expiry; the workflow does not give an unattended schedule
access to the signing secret.

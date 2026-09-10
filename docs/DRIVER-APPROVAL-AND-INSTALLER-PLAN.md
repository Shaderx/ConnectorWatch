# Maintainer driver approvals and installer delivery

Status: implemented with fixture acceptance checks, 2026-09-10. See
[implementation and acceptance evidence](DRIVER-APPROVAL-IMPLEMENTATION.md) for
completed checks and release prerequisites. No real driver approval or public
release is granted by implementing this plan.

Maintainer amendment, 2026-09-10: ship the first installer with the project's
self-signed Authenticode identity while retaining the public CA signing path.
The maintainer explicitly approved drivers 616.92 and the previously validated
616.56 for the documented exact hardware/reader scope. Revision 1 records these
as auditable bootstrap attestations, including the limitations of 616.92's
structural evidence. This exception does not label that evidence a full
independent comparison or change the validation requirements for new drivers.

## Ownership and distribution

The maintainer validates drivers and explicitly approves publication. End users install ConnectorWatch and receive authenticated compatibility decisions automatically. GitHub hosts public assets; users need no account, source checkout, build tools, or manual validation.

Keep two independent release tracks:

- Driver catalog: small signed data updates that approve an existing reader profile for an exact hardware/driver combination.
- Application: signed installer releases containing executable code and reader implementations.

The catalog must never supply executable code, native function addresses, arbitrary buffer layouts, or download locations for native libraries. A changed native interface requires a tested app release.

## Maintainer workflow

1. Run a dedicated validation command on the maintainer's machine. It may probe an unapproved driver in an isolated process and data directory, without changing production acceptance or references.
2. Capture GPU PCI/subsystem identity, OS/architecture, exact driver version, reader profile/version, tool commit, metadata and status responses, return codes, guards, timeouts, and sample progression. Remove machine UUIDs and personal paths from public evidence.
3. Compare with the last approved metadata contract, decode with the shipping reader, and run a bounded sample test. Compare voltage/current with an independent reference at idle and an ordinary workload for full approval. Structural smoke checks alone are recorded as provisional evidence. The existing 616.92 local check lacks this independent comparison and should be completed before a new public approval claim.
4. Produce an evidence bundle and proposed catalog entry. Failed checks prevent automatic proposal of approved status; the tool never grants approval itself.
5. Maintainer reviews the evidence and explicitly approves the exact catalog digest. A protected publication workflow validates schema, scope, evidence hashes, monotonic revision and supported reader profiles. Only the maintainer may authorize publication.
6. Sign and publish immutable, versioned catalog assets to a dedicated GitHub release track. Publish the discovery pointer last. Keep app release discovery separate so catalog releases cannot be mistaken for app updates.
7. If a regression is discovered, publish a higher-revision catalog revoking the affected exact combinations, with a reason and remediation. Never overwrite history or roll the catalog revision backward.

A driver-only approval needs focused native validation and catalog tests, not the whole GUI/storage suite. A reader-code change follows normal app testing and release validation.

## Catalog contract and trust

An entry identifies vendor/device/subsystem, OS/architecture, exact driver version, embedded reader profile, compatible reader/app versions, decision (approved or revoked), evidence hash, approval timestamp and public rationale. Approval applies only to tested scope, not every RTX 5090 or every driver with the same prefix.

The signed envelope includes schema version, catalog revision, issue/expiry times, signing-key identifier and payload digest. Verify the signature over exact payload bytes before deserializing the payload. Bundle trust keys and an initial signed catalog in the installer. HTTPS or a checksum beside a file alone is insufficient authentication.

Use an established signed-update verification implementation where feasible; select the library before coding the verifier. Cover tampering, rollback, freeze/expiry, key rotation, revoked keys, download size/time limits and atomic cache replacement. If a smaller custom protocol is selected, document its narrower guarantees and do not call it TUF-compliant. Keep the approval signing key outside repository content and ordinary build jobs. Root-key replacement requires an authenticated rotation path or a signed app update, never a key fetched and trusted from the same unverified response.

Proposed refresh: startup, detected driver change, every six hours with jitter, and a manual refresh button. Use bounded retries and conditional requests. Expiry is a maintainer obligation: propose a 30-day catalog validity with weekly renewal. Renewals preserve existing decisions; they cannot approve new drivers. Offline clients can use the last verified unexpired catalog. On expiry, pause private rail acquisition with an actionable refresh message; keep public telemetry available. This explicitly trades indefinite offline operation for bounded stale-approval exposure. Offline clients cannot learn revocations immediately.

Reject invalid downloads without replacing the last verified cache. Persist the highest accepted revision. Revocations override approvals, including bundled entries. Local administrators can alter application state; this protects update delivery, not against a hostile administrator.

## Application behavior

Put downloading, signature verification, caching and compatibility matching behind one driver-approval module. The native reader receives a verified decision for the observed identity and its embedded reader profile. Recheck at session initialization and after a detected driver change; serialize refresh/restart to avoid duplicate readers.

- Approved: start the existing guarded reader automatically.
- Not listed: show “This driver is awaiting maintainer approval,” with refresh and last-check information. No end-user test procedure.
- Revoked: stop private acquisition, expose the reason, and retain history. Public GPU telemetry can continue.
- Requires newer reader: offer an app update.
- Invalid or expired catalog without a usable cache: show approval unavailable, without reporting the driver itself as defective.

All existing hardware identity, native return-code, buffer, timeout and plausibility checks remain mandatory. Keep ValidateDriverVersion=false only as an advanced local developer escape hatch, label the session unvalidated, and do not expose it as normal onboarding. Proposed refinement: it may bypass a missing approval but not an explicit revocation. Maintainer probing of revoked versions remains isolated in the validation tool.

Record actual driver and catalog revision in diagnostics. A catalog refresh alone must not invalidate reference identity. Driver changes continue to follow reference lifecycle rules; never silently accept or erase a baseline. Any future cross-driver reference reuse needs a separate evidence-backed compatibility policy.

## Installation and app updates

Use a signed Inno Setup installer for the existing self-contained Windows WPF application. Install per user under LocalAppData/Programs/ConnectorWatch, with data/configuration under LocalAppData/ConnectorWatch. Bundle the runtime. Provide Start menu entry, optional desktop shortcut, optional start-at-login, Windows uninstall registration and launch-after-install. No administrator rights should be necessary for ordinary installation.

Provide a stable “Download for Windows” link; GitHub Releases can remain the underlying host. First launch detects supported hardware, checks approvals and starts monitoring, with plain-language status. GitHub terminology stays out of the normal flow.

App updates are offered in-app with release notes. Authenticate update metadata and verify the downloaded installer digest and Authenticode publisher before launching. Ask the user to restart for executable updates; catalog updates apply automatically. The installer coordinates graceful daemon shutdown, flushes data, replaces binaries and restarts the app. Preserve configuration and references; interruption leaves a working installation or a recoverable previous version. Define data-schema rollback constraints before claiming binary rollback is safe.

For current portable users, provide a one-time import flow: select/detect the existing configuration, stop its writer, back up and import data, then verify the new installation. Keep the original directory until the user removes it. Uninstall preserves monitoring data by default, with a separate explicit removal option.

Code-sign the app, installer and uninstaller. Obtaining/configuring a signing identity is a release prerequisite; signing does not guarantee Windows reputation prompts disappear immediately.

## Implementation order and acceptance

1. Catalog schema, verification/cache module and runtime integration. Test valid/invalid signatures, unknown keys, identity mismatch, rollback, expiry, offline cache, revocation, concurrent refresh and required app upgrade. No new private call starts without an applicable decision, except explicit developer mode.
2. Maintainer validation command, evidence generation and protected GitHub publication. Test that failed validation cannot auto-approve, approval scope cannot widen silently, and only the authorized publication path can sign approvals.
3. Installer and portable-data migration. Test a clean standard-user Windows installation, upgrade, uninstall, interrupted install and preserved configuration/history.
4. In-app app-update flow and recovery. Test corrupt/wrong-publisher installers, download interruption, graceful shutdown, restart and incompatible data-schema rollback.
5. End-to-end pilot: an unknown test driver decision becomes approved through a signed catalog refresh without app reinstall; a subsequent revocation pauses its reader. Perform the pilot on controlled fixtures before using real approval records.

## Primary references

- Inno Setup non-admin installation: https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm
- TUF update-security model: https://theupdateframework.io/docs/security/
- TUF metadata and key roles: https://theupdateframework.io/docs/metadata/

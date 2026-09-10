# Driver approval and installer implementation

The 1.5.0 implementation separates driver compatibility data from executable
application updates. It uses a documented ECDSA P-256 signed-envelope protocol
with .NET cryptography. It is not TUF and does not provide TUF's threshold root
rotation or compromise recovery. Trust-root replacement requires a signed app
release. Catalog signing authority cannot sign app updates: the release trust
preparation command rejects shared public keys across those roles.

## Runtime

`DriverApprovalService` verifies exact payload bytes before parsing the catalog,
matches exact hardware/driver/profile/version scope, and manages bounded refresh
and rollback state. `ApprovalRailSource` owns one native session and checks the
decision before acquisition. `DirectNvRails` independently rechecks scope and
installed driver immediately before its private metadata and status calls.
Unknown, unavailable, expired, or revoked approvals pause private readings while
the daemon continues public telemetry. Explicit developer mode is labeled and
does not bypass a known revocation. The legacy diagnostic now uses public identity
calls; unapproved private probing belongs to the isolated maintainer command.

Driver replacement flushes the current session and reloads reference lifecycle
state with the observed driver identity. Catalog revision is recorded separately
from reference identity, so a catalog renewal does not silently replace a baseline.
The dashboard shows the driver, catalog revision, decision, and last check, with a
manual refresh control and a separate application-update action.

## Delivery

The build-time trust preparation command verifies the initial signed catalog,
checks role separation and publisher pins, and emits the catalog key source and
installer assets. Ordinary builds contain no claimed maintainer trust anchor.
Application update metadata and publisher trust arrive in the authenticated
installer; the updater does not learn new trust roots from a download response.

The separate protected app publication workflow prepares a signed installer for
review, binds publication to the reviewed installer and metadata digests, and
updates stable discovery only after publishing the immutable release. It verifies
the prepared artifact's source, publisher, trust inputs, and monotonic revision.
Renewals and explicitly bounded rollbacks reuse the original immutable installer;
each signed app metadata revision is retained separately before stable discovery
changes. The runtime independently enforces the rollback version and schema bounds.

See [catalog contract](DRIVER-CATALOG.md), [maintainer publication](MAINTAINER-APPROVALS.md),
[installation and import](INSTALLATION.md), [application updates](APP-UPDATES.md),
and [protected app releases](APP-RELEASING.md).

## Acceptance evidence

- Core self-tests pass, including the existing storage, reference lifecycle,
  replay, native decoder, and analysis checks.
- Focused approval tests pass, including signature tampering, unknown/revoked
  keys, exact scope, rollback/equivocation, expiry, offline cache, app-version
  requirements, and the fixture unknown → approved → revoked runtime pilot.
  Restart fixtures cover bounded refresh disposal and transient cache-lock overlap;
  developer mode cannot bypass an unread locked approval cache.
  Key-rotation fixtures accept overlapping old/new trust and reject a retired key
  without resetting the revision watermark.
- Maintainer fixtures verify provisional smoke evidence cannot propose approval,
  failed checks reject proposals, and independent comparisons are required.
- Release-trust fixtures verify role separation, initial catalog authentication
  and expiry, and generation of the required bundled assets.
- Both application projects build under .NET 8 with zero warnings.

- Dashboard logic: 115 checks pass. Windows UI: 16 checks pass; the rendered
  dashboard was inspected with the new approval and app-update controls.
- Migration/recovery: 11 checks pass, including interrupted import recovery,
  writer coordination, traversal rejection, hash-verified backups and restore.
- App updates: 26 Windows checks pass, including corrupt and wrong-publisher installers,
  interrupted-download cleanup, expired offers, rollback and schema constraints.
  Windows Authenticode checks include a signed Microsoft binary, a wrong
  publisher pin, unsigned input, disposable self-signed publishers, altered PE
  bytes, a damaged CMS signature, mixed publisher policies, and strict trust-file
  parsing. No certificate stores are changed by the application verifier.
- Inno Setup 7.1 compiles the isolated unsigned fixture. A nonadministrative install
  and upgrade succeed, preserve configuration/history hashes, and create a previous
  application backup. Restoring that backup recovers 498 files after a simulated
  interrupted replacement and retains the replaced directory.
- Silent uninstall removes the fixture application and its Windows registration
  while preserving the exact configuration and history hashes.
- The sandbox-denied installation rolled back its application files after Windows
  registration failed. This tests a controlled failure; it does not simulate power
  loss or prove every Inno interruption point.
- Both publication scripts complete ephemeral-key signing and verification round
  trips, including previous-key authentication followed by a new-key signature.
  Native validation rejects dirty source trees before probing so its recorded
  commit identifies the reader being tested. PowerShell syntax checks pass and action pins were verified against their
  upstream tags. Publication workflows have not run against production secrets.

No physical driver validation is run by these fixtures. Unsigned installer fixtures
use a separate test AppId and repository-local application/state paths with no
shortcuts; they are not release deliverables.

## Release prerequisites

The maintainer changed the initial release policy on 2026-09-10: use a project
self-signed Authenticode certificate while preserving the public CA signing mode.
The bootstrap command creates separate catalog and app metadata keys and keeps
encrypted private material outside Git. Public pins and certificates are in
`release/trust`; the signing policy does not add certificates to Windows trust stores.

The maintainer explicitly approved 616.56 and 616.92 for the existing exact scope.
The revision-1 attestation records 616.92's structural evidence and missing
independent idle/workload comparison. New approvals continue to require the full
validation workflow. Protected publication and real signed-installer acceptance
must still succeed before the release is reported as published.

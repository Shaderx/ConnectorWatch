# Release trust

`trust/` contains public material for the first self-signed installer: separate
ECDSA P-256 keys for driver catalogs and application metadata, and the SHA-256
pin and certificate for the Authenticode publisher. Private keys do not belong
here. Exact JSON bytes are preserved by `.gitattributes`.

`scripts/Initialize-SelfSignedRelease.ps1` creates a new identity once. Supply an
absent private directory outside the repository and an empty public directory.
The private directory has a restricted Windows ACL; the PFX password and metadata
keys are encrypted with Windows current-user DPAPI. Preserve that account and
machine for recovery. Re-running the command never replaces an existing identity.
The script does not install a certificate into a Windows trust store.

`scripts/Configure-GitHubReleaseSigning.ps1` provisions the initial bundle in the
two protected GitHub release environments. It creates reviewer and protected
branch requirements when those environments are absent, preserves existing
protection rules, and transmits secrets through stdin. Run it only with explicit
repository-owner authorization. The 2026-09-10 setup was authorized by Shaderx.

Use [protected application releases](../docs/APP-RELEASING.md) with
`signing_mode: SelfSigned` for the initial installer. The first catalog includes
the [explicit maintainer attestation](../docs/release/driver-approval-attestation.json)
for exactly 616.56 and 616.92 on the documented hardware and reader.

Renew signed catalogs and application metadata before their expiry; both have
a maximum validity of 31 days. Renewal uses the next immutable revision and
preserves existing driver decisions and application binaries. Retain each
previous signing identity until its authenticated replacement has reached users.

For public CA migration, first publish a bridge installer signed by the current
self-signed identity containing both its existing self-signed pin and the new
public leaf certificate pin. Then switch builds to `PublicTrusted`. Existing
clients cannot authenticate an unannounced new certificate. Self-signed
certificate expiry is enforced at verification time, even on timestamped files;
complete the bridge or same-policy certificate rotation before that date.

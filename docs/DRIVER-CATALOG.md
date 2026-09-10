# Signed driver catalog

ConnectorWatch uses a deliberately small signed-metadata protocol for driver compatibility. It is not TUF-compliant and does not claim TUF's delegated roles, threshold signatures, or compromise-recovery model. Its narrower purpose is to authenticate exact driver decisions made by the maintainer and to reject rollback, freeze, and same-revision equivocation on one installation.

The repository contains no maintainer private key or claimed public trust anchor. It does contain the public [initial-catalog maintainer attestation](release/driver-approval-attestation.json) for exact drivers `616.56` and `616.92`; this record is inert until its exact resulting catalog payload is authorized and signed through the protected publication environment. A release build must inject reviewed public keys through the generated implementation of `DriverCatalogReleaseTrust.AddReleaseKeys` and may bundle a catalog signed by one of those keys under `trust/driver-catalog.signed.json`. Key addition, replacement, or revocation is an application-release change. A downloaded response cannot change its own trust roots.

## Distribution separation

The catalog publisher writes immutable revisioned envelopes and then publishes `catalog-current.json` last as a byte-identical signed-envelope alias in the dedicated `driver-catalog` GitHub release track. The client downloads and verifies that alias directly; it does not follow an unsigned catalog pointer:

`https://github.com/Shaderx/ConnectorWatch/releases/download/driver-catalog/catalog-current.json`

Application installers and application update metadata use separate `app-v*` releases and an `app-stable` discovery alias. Catalog JSON cannot name native libraries, code, addresses, layouts, or download locations. A reader or ABI change requires an application update.

## Envelope and verification order

The UTF-8 envelope is at most 384 KiB. It contains exactly these fields:

```json
{
  "envelope_schema": 1,
  "key_id": "maintainer-key-id",
  "algorithm": "ecdsa-p256-sha256",
  "payload_sha256": "64 lowercase hexadecimal characters",
  "payload": "base64 of the exact UTF-8 payload bytes",
  "signature": "base64 of a 64-byte IEEE-P1363 signature"
}
```

The verifier rejects duplicate and unknown envelope fields, unknown or revoked key IDs, algorithms other than ECDSA P-256 with SHA-256, non-P-256 SPKI keys, malformed base64, a digest mismatch, and a signature that is not exactly 64-byte IEEE-P1363 form. It hashes and verifies the exact decoded payload bytes before any payload JSON is parsed. The payload limit is 256 KiB.

.NET's `ECDsa.ImportSubjectPublicKeyInfo` imports the pinned DER SubjectPublicKeyInfo and reports how many bytes were consumed; ConnectorWatch requires the whole input to be consumed. `ECDsa.VerifyHash` accepts an explicit `DSASignatureFormat`, which lets the protocol require the fixed-width IEEE-P1363 representation instead of accepting an ambiguous encoding. These choices follow the [.NET ECDSA verification API](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdsa.verifyhash?view=net-8.0) and [.NET SubjectPublicKeyInfo import API](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecalgorithm.importsubjectpublickeyinfo?view=net-8.0).

`SignedMetadataVerifier`, `SignedMetadataHeader`, and `SignedMetadataPolicy` are reusable by authenticated application update metadata. Each consumer supplies a distinct payload type and revision property and maintains its own rollback watermark.

## Catalog payload

The schema is [driver-catalog.schema.json](driver-catalog.schema.json). A catalog has:

- `payload_type`: exactly `connectorwatch-driver-catalog`.
- `schema_version`: `1`.
- `catalog_revision`: a positive, monotonically increasing integer.
- `issued_utc` and `expires_utc`: whole-second UTC timestamps ending in `Z`. Validity cannot exceed 31 days.
- `entries`: up to 4,096 exact-scope decisions.

Each entry contains an exact PCI vendor, device, and subsystem ID; OS; architecture; driver version; embedded reader profile; compatible reader and application ranges; decision; evidence SHA-256; decision timestamp; and public rationale. IDs use uppercase hexadecimal without `0x`; OS and architecture use lowercase canonical names. Versions use two to four numeric components. Range minima are inclusive and maxima are exclusive.

The embedded private reader scope is profile `A612-A613-v1`, reader version `1.0.0`. Runtime integration passes the actual application assembly version (`1.5.0` for the installer release under development). New approvals require full independent validation. The one-time revision 1 bootstrap may include only exact documented drivers `616.56` and `616.92`; its catalog rationales preserve their different validation levels and limitations.

Entries decide only an exact `DriverIdentity(VendorId, DeviceId, SubsystemId, Os, Architecture, DriverVersion)` and reader profile. There is one entry per exact identity/profile scope. A `revoked` decision blocks that scope even when the cached catalog has expired and even in developer mode. An approval outside its signed reader or application range does not start the reader. Older clients report that a newer compatible application or reader is required; newer untested clients remain outside the approved scope.

`DriverCatalogPayload.Parse` is the strict publication-time parser. `DriverCatalogPayload.ParseVerified` accepts only a `VerifiedSignedMetadata` result for runtime use. Protected publication should additionally enforce evidence ownership, maintainer authorization, revision monotonicity, and that renewal-only changes do not introduce decisions.

## Cache, refresh, and failure behavior

`DriverApprovalService.CreateDefault(dataDirectory)` loads the persistent rollback watermark, then a verified cached catalog, then an optional signed bundled catalog. Before a bundled catalog can authorize private acquisition, its revision/digest watermark and exact envelope are atomically installed in the data directory; a later application rollback therefore cannot revive an older bundled approval. The service starts a refresh at startup and schedules later attempts every six hours with up to 15 minutes of jitter. `RefreshAsync(force: true)` is the manual and driver-change path. Concurrent callers share one request.

Requests require HTTPS, use a 15-second timeout, at most two transport attempts, a 384 KiB response bound enforced both from `Content-Length` and while streaming, and `If-None-Match` when a verified cache has an ETag. RFC 9110 defines ETags as opaque validators and `If-None-Match` as the conditional request used for cache validation; see [RFC 9110 sections 8.8.3 and 13.1.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.2).

An accepted revision and digest are persisted before cache replacement, under a cross-process file lock. A lower revision is rejected even after the cached catalog expires. Reusing a revision with different exact payload bytes is rejected. An invalid download never replaces a verified cache. If writing the new envelope fails after advancing the watermark, the process drops any older in-memory catalog and fails closed.

Offline operation can use a previously verified, unexpired cache. Once it expires, private rail acquisition pauses and public telemetry can continue. The verified expired catalog remains available to enforce explicit revocations. A malformed rollback watermark blocks cache loading and refresh instead of silently resetting rollback protection. Local administrators can still modify the program and its state; this protocol protects delivery from network and repository-asset substitution, not from a hostile local administrator.

## Release checklist

1. Generate and protect an ECDSA P-256 signing key outside the repository and ordinary build jobs.
2. Inject only the reviewed SPKI public key and key ID into the signed application build.
3. Validate the catalog with `DriverCatalogPayload.Parse`, supported profile `A612-A613-v1`, evidence hashes, and a revision greater than the published watermark. The separate bootstrap policy is restricted to revision 1 and the two exact public attestation entries.
4. Review and explicitly authorize the exact payload SHA-256.
5. Sign the exact payload bytes in 64-byte IEEE-P1363 form, construct the immutable envelope, and verify it with the release build before upload.
6. Publish the immutable revisioned catalog asset first and the discovery pointer last. Do not overwrite old revisions.
7. Keep the 30-day catalog renewed weekly. Publish a higher-revision revocation immediately when evidence changes.

The focused offline fixtures in `DriverApprovalTests.Run()` create a temporary P-256 key, then cover signature and payload tampering, unknown and revoked keys, duplicate fields, exact-scope assertions, rollback, same-revision equivocation, expiry, offline cache loading, required application upgrades, concurrent refresh, developer-mode limits, and the unknown → approved → revoked pilot. The fixture private key exists only in process memory and is destroyed when the test completes; it is not a release trust root.

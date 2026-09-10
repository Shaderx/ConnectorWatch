# ConnectorWatch 1.5.0

Driver acceptance now uses authenticated catalogs for exact GPU, subsystem,
operating system, architecture, driver, and embedded reader combinations. Catalog
updates can approve or revoke that scope without replacing the application.
Private readings pause when approval is missing, expired, or withdrawn; public GPU
telemetry and recorded history remain available. The dashboard exposes refresh and
last-check details. The advanced developer option labels unvalidated sessions and
cannot override an explicit revocation.

Maintainers get isolated validation, sanitized evidence bundles, independent
idle/workload comparison checks, and publication bound to an explicitly reviewed
catalog digest. The maintainer approved 616.56 and 616.92 for the existing exact
hardware and reader scope; the revision-1 attestation records 616.92's
structural evidence and missing independent idle/workload comparison.

Per-user installer delivery keeps configuration and data outside executable
directories, supports importing portable recordings, and coordinates graceful
shutdown. Application updates use a separate signed metadata track, verify both
the installer digest and Authenticode publisher, and ask before restarting.

The first protected 1.5.0 application release uses the self-signed publisher
certificate pinned in `self_signed_publisher_certificate_sha256` because the
public CA identity is not yet provisioned. Windows may display “Unknown
publisher”; the shipped application-scoped verifier checks the signed-file
digest and certificate pin without requiring a `Root` or `TrustedPublisher`
import. The release exposes `ConnectorWatch-publisher.cer`, `publisher.json`,
and `SHA256SUMS.txt` for inspection. Its certificate SHA-256 is
`2e7c221f2edfb2057140cb67d24a2ca4502e005069c47c232583ff8f10d9552e`.

Future public-trusted releases keep `PublicTrusted` as the build default. The
migration first requires a bridge application signed by this existing
self-signed certificate with both the old self-signed leaf pin and the future
public leaf pin. After clients receive that bridge, the first public-CA-signed
installer can ship; a later signed release can retire the self-signed pin.
Source and fixture tests do not constitute a new hardware approval or a signed
public release. See the installation, app-update, and maintainer documentation.

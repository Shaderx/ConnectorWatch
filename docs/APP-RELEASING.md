# Protected application releases

Application releases use two distinct signatures: Authenticode signs the
Windows binaries and installer, while the app metadata key signs the exact
installer digest and update policy. Neither key is a driver-catalog key.
The workflow's `signing_mode` input is `PublicTrusted` by default. The first
1.5.0 protected release uses `SelfSigned` while the public CA identity is being
provisioned.

Use the `Prepare or publish protected application release` workflow from a
protected branch. Configure required reviewers on the `app-release-publication`
GitHub Environment. Its secrets are `APP_AUTHENTICODE_PFX_BASE64`,
`APP_AUTHENTICODE_PFX_PASSWORD`, and `APP_RELEASE_SIGNING_KEY_PEM`. Its variables
are `APP_RELEASE_KEY_ID` and `APP_RELEASE_PUBLIC_KEY_SPKI_BASE64`. A future
`PublicTrusted` release may additionally configure an explicit HTTPS RFC 3161
timestamp service through `APP_AUTHENTICODE_TIMESTAMP_URL`; the initial
`SelfSigned` release has no external timestamp-service requirement. Private keys and PFX material never belong
in the repository or ordinary build jobs; public `.cer` identity files may be
published for fingerprint inspection.

App metadata key rotation is staged. First publish a signed application whose
app trust file contains both old and new non-revoked metadata keys. Then switch
the protected current key variables and set `APP_RELEASE_PREVIOUS_KEY_ID` plus
`APP_RELEASE_PREVIOUS_KEY_SPKI_BASE64` to the explicitly pinned old key. The
next publication authenticates current state with the old key and signs the new
envelope only with the current key. After clients receive new-key metadata, a
later signed application may retire or revoke the old key. Clear the optional
previous-key variables after the transition. Publication never accepts a trust
key fetched from the envelope it is trying to verify.

First dispatch `mode: Prepare` and select `signing_mode: SelfSigned` for the
1.5.0 release. Future releases select `PublicTrusted` unless a reviewed bridge
rotation requires both signer pins. Supply the catalog trust, initial signed
catalog, and app trust files embedded by `Build-Installer.ps1`, plus exact
version, metadata revision, whole-second UTC issue/expiry times, and release
notes. The protected job builds and Authenticode-signs
`ConnectorWatch-Setup.exe` before creating metadata. This ordering avoids a
circular claim: app metadata cannot name the installer digest until the final
installer exists. Preparation also requires the supplied app trust file to
contain the protected app metadata public key as one non-revoked key. Its
`publisher_certificate_sha256` array contains public CA signer pins and may
be empty only when `self_signed_publisher_certificate_sha256` contains a pin.
A bridge release may contain both arrays; the selected `signing_mode`
determines which array the build and publication checks use.

`SelfSigned` builds do not require an external timestamp service and omit
timestamping. Their certificate validity is checked at verification time; they
do not import the certificate into `Root` or `TrustedPublisher`.
The build imports the PFX only into the ephemeral maintainer
`Cert:\CurrentUser\My` store, signs the application files, installer, and
Inno uninstaller, and verifies each content signature with the packaged
`ConnectorWatch.exe --verify-release-signature` command. Public-trusted builds
retain strict `Get-AuthenticodeSignature` chain validation. The prepared and
published assets include `ConnectorWatch-publisher.cer`, `publisher.json`, and
`SHA256SUMS.txt` so the signer identity can be inspected without trusting a
root globally.

If `app-v<version>` already exists, Prepare reuses its immutable installer
instead of rebuilding it. The workflow downloads that exact asset and verifies
its embedded numeric file version, Authenticode signature, and current app-trust
publisher pin before producing new metadata. Publish downloads it again and requires its digest to equal the
reviewed prepared installer. This supports same-version metadata renewal and a
bounded rollback to an already published binary without replacing history.

Download the `prepared-app-release` artifact. Review the signed installer,
release notes, trust inputs, `app-release.payload.json`, and
`authorization-request.json`. Record both exact lowercase digests and the
Prepare workflow run id. No publication occurs in Prepare mode.

Then dispatch `mode: Publish` with the same version, revision, timestamps and
notes, the Prepare run id, both reviewed digests, and this exact sentence:

`I authorize these exact installer and app payload digests`

Publish mode downloads the earlier installer instead of rebuilding it. The
artifact must come from a successful Prepare dispatch of this exact workflow,
repository, and protected source commit. Publish revalidates the selected
`signing_mode` (native trusted-chain validation for `PublicTrusted`, or the
application-scoped verifier for `SelfSigned`) and the embedded numeric file
version. It requires the signer certificate SHA-256 in the selected app-trust
publisher pin array. It then recomputes both digests and authenticates the current canonical
`app-stable/app-current.json`, requires a higher revision, and signs only after
the authorization matches. The payload is exact `app-release` schema version 1:
stable version, immutable HTTPS installer URL, SHA-256 and byte size, release
notes, data schema 3 through 3, and an explicit rollback object or `null`.
Routine releases use `null`; a rollback needs a separately reviewed policy that
allows only data schema 3 and names non-null, ordered stable minimum and maximum
source versions. A target version lower than authenticated current metadata is
rejected unless those bounds cover the current version. The optional
`rollback_policy_path` input is supplied identically to Prepare and Publish, so
its exact contents are bound by the reviewed payload digest.
The minimum source version must also be strictly newer than the rollback target.

The generated envelope is round-tripped through `SignedMetadataVerifier`,
`SignedMetadataPolicy`, and `AppReleaseMetadataValidator`, then checked against
the completed installer bytes. The workflow publishes a draft immutable release
tagged `app-v<version>`, uploads the installer and signed metadata, and publishes
that release; self-signed releases are titled with `(self-signed)`. Existing version releases remain untouched. Every signed metadata
payload/envelope is separately preserved under immutable
`app-metadata-r<revision>`. Only after the target binary and metadata revision
are both immutable does the workflow update the `app-stable` release:
the stable `ConnectorWatch-Setup.exe` first and the signed `app-current.json`
discovery alias last. Existing immutable releases are refused and public `v*`
portable releases are untouched.

Prepare artifacts expire after 14 days. If inputs, installer bytes, notes, or
trust material change, run Prepare again and review the new digests. Failed
draft upload or publication leaves stable discovery unchanged. For 1.5.0,
Windows can display an “Unknown publisher” warning because the release uses a
self-signed certificate. Users should inspect the
`ConnectorWatch-publisher.cer` SHA-256 fingerprint and the matching
`self_signed_publisher_certificate_sha256` value in the shipped trust JSON.
The application verifier applies this pin and Authenticode content checks in
application scope; installation into Windows `Root` or `TrustedPublisher` is
neither required nor recommended. To migrate to a public CA, first publish a
bridge application signed by the existing 1.5.0 self-signed certificate with
both the old self-signed leaf pin and the future public leaf pin in its trust
JSON. After clients receive that bridge, publish the first public-CA-signed
installer. A later signed application can then retire the old self-signed pin.

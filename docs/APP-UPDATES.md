# Authenticated application updates

Application releases and driver approval catalogs use separate signing keys and separate discovery locations. Driver catalog authority can approve compatible data profiles; it cannot authorize executable updates. Application update trust and Authenticode publisher certificate pins ship with the signed application.

The app trust JSON keeps public CA pins in `publisher_certificate_sha256` and
may also contain `self_signed_publisher_certificate_sha256`. A public array may
be empty when the self-signed array is populated. During bridge rotation both
arrays can be present, but the release's `SigningMode` selects the signer pin
used by the protected build. ConnectorWatch 1.5.0 starts with its self-signed
pin and therefore Windows may display an “Unknown publisher” prompt.

## Check contract

`AppUpdateService.CreateDefault(HttpClient)` loads the app-specific trust configuration bundled with the installed application. `CheckAsync(currentVersion, currentDataSchema, now)` downloads a bounded signed envelope and uses the shared signed-metadata verifier to authenticate the exact payload bytes before deserializing them.

The version 1 `app-release` payload contains:

- monotonic `revision`, whole-second `issued_utc` and `expires_utc` values;
- canonical stable `X.Y.Z` `version`, HTTPS `installer_url`, exact byte size, and SHA-256;
- bounded plain-text `release_notes` shown before the user decides;
- the minimum and maximum data schema the offered binary can read; and
- for a lower binary version, an explicit rollback policy restricting source versions and data schemas.

The persisted app-release watermark rejects a lower revision and same-revision equivocation. Expired or future metadata is rejected. A signed lower version is still rejected unless the authenticated rollback policy covers the current binary and current stored-data schema. The GUI presents such a result as `RollbackNeedsConfirmation`; it does not silently treat rollback as an ordinary update.

## Download and launch

The GUI passes the opaque `AppUpdateCheckResult` returned by that service to `DownloadInstallerAsync`. A caller-created metadata object cannot authorize a download. One service serializes checks and downloads, applies an absolute timeout, requires the authenticated size, hashes while streaming to a `.partial` file, and atomically renames only a complete installer. Cancellation, network loss, excess bytes, and digest mismatch remove the partial file. A valid cached file is rechecked before reuse.

`LaunchInstaller` accepts the same authenticated result and rechecks size, SHA-256, the Windows Authenticode signature and the selected publisher certificate SHA-256 pin immediately before starting the `.exe`. Public-trusted releases require the native Windows certificate chain. Self-signed releases use the same in-process `WindowsAuthenticodeVerifier` as `ConnectorWatch.exe --verify-release-signature --file PATH --app-trust PATH`; the GUI does not launch a second process. The verifier checks the embedded signature, signed-file digest, and pinned signer in application scope without changing Windows trust stores. It never downloads or launches scripts. The installer then coordinates graceful dashboard and daemon shutdown and preserves state while replacing binaries.

Application updates require a restart. Driver catalog changes apply through the catalog refresh path and do not use this executable-update flow.

## Release order

The application installer digest cannot be signed before the installer exists. A protected application release therefore proceeds in this order:

1. prepare separate driver-catalog and app-update public trust inputs;
2. publish and Authenticode-sign application binaries;
3. compile and Authenticode-sign the installer and uninstaller;
4. hash the completed installer;
5. create and sign the `app-release` metadata with the protected app metadata key;
6. verify the metadata with the shipping app key and verify the installer with the shipping publisher pin;
7. publish immutable versioned assets under the application release track; and
8. update the stable release's `ConnectorWatch-Setup.exe` asset, then update
   the signed `app-current.json` discovery alias last.

The catalog discovery pointer is independent. Neither workflow fetches a new public key from the same response it is trying to authenticate. Private signing keys and certificate material stay outside repository content and ordinary build jobs. The protected workflow imports its PFX only into `Cert:\CurrentUser\My`; it never imports a release certificate into `Root` or `TrustedPublisher`. Each release exposes `ConnectorWatch-publisher.cer`, `publisher.json`, and `SHA256SUMS.txt` for fingerprint and digest review.

## Verification limits

`DeploymentTests.Run()` covers portable import, backups, path containment and preservation. `AppUpdateTests.Run()` uses signed fixtures to cover metadata authentication, digest enforcement, publisher-verifier invocation, launch-time revalidation, and unforgeable check binding. Production acceptance additionally requires a disposable standard-user Windows install using the real release certificate identity. No repository test creates that identity or publishes a release. To migrate to a public CA, first publish a bridge update signed by the existing self-signed certificate whose trust JSON contains both the old self-signed leaf pin and the future public leaf pin. Only after clients can authenticate that bridge should a public-CA-signed installer become the current release; a later signed update can retire the old pin.

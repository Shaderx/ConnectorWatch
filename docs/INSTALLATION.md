# Installing ConnectorWatch on Windows

ConnectorWatch 1.5 and later uses a per-user Windows installer. It does not request administrator rights and installs the application under:

```text
%LOCALAPPDATA%\Programs\ConnectorWatch
```

Configuration, monitoring history, accepted references, incident state, update cache, and migration backups live separately under:

```text
%LOCALAPPDATA%\ConnectorWatch
```

Keeping state outside the program directory lets upgrades replace application files without replacing measurements or configuration.

## Install

Download `ConnectorWatch-Setup.exe` from the project's application release channel and run it as the Windows user who will run ConnectorWatch. The installer creates a Start menu shortcut. Desktop and start-at-login shortcuts are optional and are off by default. The final page can launch the dashboard.

Release installers are Authenticode-signed. ConnectorWatch 1.5.0 is the first
protected release and uses a self-signed publisher certificate, so Windows may
show “Unknown publisher”. The application checks the signed-file digest and
the certificate SHA-256 pin from its bundled app trust JSON in application
scope. Do not import the certificate into Windows `Root` or `TrustedPublisher`;
the application-scoped check does not require a global trust change. Do not
use files named `ConnectorWatch-Setup-UNSIGNED-FIXTURE.exe`; those are local
test artifacts and the release workflow rejects them.

The release includes `ConnectorWatch-publisher.cer` and `publisher.json`.
To inspect the certificate fingerprint before running the installer:

```powershell
$cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path .\ConnectorWatch-publisher.cer))
$cert.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
```

For 1.5.0 this must match the self-signed publisher pin in
`app-update-trust.json`:
`2E7C221F2EDFB2057140CB67D24A2CA4502E005069C47C232583FF8F10D9552E`.

## Import a portable installation

The installer offers an optional import page. Select the `config.json` beside the existing portable executable. Import then:

1. validates that the selected configuration and its relative data path remain below the portable directory and contain no links or reparse points;
2. asks the running portable daemon to stop through its identity-bound current-user control pipe and holds its writer lock;
3. creates a verified backup under `%LOCALAPPDATA%\ConnectorWatch\migration-backups`;
4. copies and hashes every data file through a private staging directory;
5. publishes the data and rewritten installed configuration; and
6. retains the original portable directory unchanged.

An interrupted publish leaves a transaction marker in the staged data. Repeating the import with the same source verifies the copied tree and completes the configuration publish. A populated installed destination is never overwritten. The one exception is the untouched default configuration and empty data directory created on first install.

After the installed dashboard shows the expected history and configuration, keep the portable directory as a recovery copy until you are comfortable removing it yourself.

## Upgrade and recovery

Before replacing files, the installer requests dashboard shutdown and asks the monitor to stop through the existing named-pipe protocol. The monitor flushes state and releases `monitor.lock`; the installer will not continue if an active writer cannot be stopped. Inno Setup keeps rollback copies of replaced program files during installation. User data is never part of that replacement transaction.

If power or the process is interrupted, run the same signed installer again. The existing installed state remains outside the application directory. Do not manually move partial files from the update cache into the program directory.

Before an upgrade replaces files, the installed helper stores a hash manifest and verified copy of the previous program under `%LOCALAPPDATA%\ConnectorWatch\application-backups`. This copy is recovery material for an installer interruption before the upgraded application first runs. After a newer application has opened the data, restoring an older binary is allowed only when its authenticated rollback policy covers the stored data schema.

For pre-launch recovery, run the backed-up `ConnectorWatch.exe` with `--deployment-restore --backup "<backup directory>" --config "%LOCALAPPDATA%\ConnectorWatch\config.json" --confirm-prelaunch-recovery`. It stops both processes, verifies every backup hash, stages the restore on the program volume, and retains the replaced directory beside the installation. The confirmation switch deliberately limits this command to recovery before the upgraded binary has opened the data.

## Uninstall

Uninstall ConnectorWatch from Windows Installed apps or its Start menu entry. Uninstall preserves `%LOCALAPPDATA%\ConnectorWatch` by default. The uninstaller asks separately whether all configuration, history, references, update downloads, and migration backups should be removed. Silent uninstall removes application files only; automation must pass `/REMOVEUSERDATA=1` to request the irreversible user-data removal explicitly.

## Maintainer installer build

Run `scripts/Build-Installer.ps1` from a Windows release environment. A production build requires:

- an initial signed driver catalog and its public trust configuration;
- a separate app-update public trust configuration containing publisher certificate SHA-256 pins;
- the protected release-trust preparation step;
- `signtool.exe` at `C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe`, an installed signing certificate selected by SHA-1 thumbprint, and an HTTPS RFC 3161 timestamp service; and
- `artifacts/build-tools/inno/ISCC.exe` (or an equivalent installed Inno Setup compiler).

The build signs and verifies published binaries, compiles a signed installer
and signed uninstaller, verifies the final installer, and writes its SHA-256.
`-SigningMode PublicTrusted` (the default) requires the native Windows
certificate chain. `-SigningMode SelfSigned` uses the packaged
`--verify-release-signature` CLI and the self-signed app-trust pin. Missing
real signing configuration is a hard failure. `-UnsignedFixture` exists only
to compile isolated installer fixtures and never participates in a release.

A release smoke test must run as a standard Windows user in a disposable account or VM and cover clean install, upgrade, interrupted upgrade, uninstall with preserved state, and explicit state removal. The final Authenticode verification and SmartScreen observation require the real external release identity; fixture certificates cannot satisfy that acceptance check.

Inno Setup documents the [`PrivilegesRequired=lowest`](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm) non-admin mode, the [`SignTool`](https://jrsoftware.org/ishelp/topic_setup_signtool.htm) integration, [signed uninstallers](https://jrsoftware.org/ishelp/topic_setup_signeduninstaller.htm), and the [`ISCC.exe` command line](https://jrsoftware.org/ishelp/topic_compilercmdline.htm).

# Builds and releases

Every branch push, pull request, and manual Actions run builds a fresh Windows package and runs offline tests on Windows and Linux. Windows tests exercise the packaged daemon, GUI data handling, and synthetic UI behavior. No CI job calls the GPU or changes hardware settings. Download the ZIP, checksum and build metadata from the `windows-package` artifact on the Actions run; artifacts expire after 14 days.

## Publish a version

1. Set the version in `Directory.Build.props`. Both applications inherit it. Update release-facing documentation and commit the changes.
2. Push the commit and wait for **Build, test and release** to pass.
3. Tag that commit with the matching version, for example `git tag v1.1.3`, then `git push origin v1.1.3`.
4. The tag run validates the version, rebuilds and tests the package, verifies its SHA-256 checksum, and publishes a GitHub prerelease after both platforms pass.

All automated releases remain **experimental prereleases**, including plain numeric version tags. Promoting a release to stable requires a deliberate maintainer decision and hardware validation; a green CI run does not expand native-reader support. Branch and manual runs never publish releases. Existing published release assets are never overwritten; use a new patch version to replace a shipped binary. Failed uploads leave a draft that the same tag run can finish on retry.

The public ZIP contains a blank UUID configuration, runtime licenses and documentation. `build-info.json` records the source commit, runtime versions, checksum and GUI test counts. No user installation or local data directory is used as packaging input. Published executables are currently unsigned.

## Local verification

On Windows with the .NET 8 SDK installed, run:

```powershell
./scripts/Package.ps1 -OutputDirectory ./artifacts/check
```

The output directory must be empty or absent. To test tag validation, also pass `-ExpectedTag v1.1.2`. `scripts/Publish.ps1` remains available for updating a local installation while preserving configuration; `Package.ps1` is the clean release path.

Actions are pinned to commit hashes. Dependabot proposes weekly action updates as pull requests; updates still need passing checks and maintainer review. Build and test jobs have read-only repository access. Only the tag release job can publish. Runtime packages resolve the current .NET 8 servicing version at build time; builds record that version but are not guaranteed to be byte-for-byte reproducible across time.

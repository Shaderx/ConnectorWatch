[CmdletBinding()]
param([string] $WorkDirectory = (Join-Path $PSScriptRoot '../artifacts/installer-naming-tests'))
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
if ((Test-Path -LiteralPath $work) -and @(Get-ChildItem -LiteralPath $work -Force).Count) {
    throw 'Installer naming test directory must be empty.'
}
New-Item -ItemType Directory -Force $work | Out-Null
$installer = Join-Path $work 'ConnectorWatch-Setup.exe'
$notes = Join-Path $work 'notes.md'
[IO.File]::WriteAllBytes($installer, [byte[]](1, 2, 3, 4, 5))
[IO.File]::WriteAllText($notes, 'Synthetic installer naming test; not an executable release.')
$stage = Join-Path $work 'stage'
& (Join-Path $PSScriptRoot 'Stage-AppInstaller.ps1') -InstallerPath $installer -Version '1.5.2' -OutputDirectory $stage
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$sums = Get-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt')
foreach ($name in @('ConnectorWatch-Setup-1.5.2.exe', 'ConnectorWatch-Setup.exe')) {
    if ((Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash -or
        "$hash  $name" -cnotin $sums) { throw "Installer bytes or checksum missing for $name" }
}
foreach ($version in @('1.5.0', '1.5.1', '1.5.2', '1.6.0')) {
    $output = Join-Path $work $version
    & (Join-Path $PSScriptRoot 'Publish-AppRelease.ps1') -Mode Prepare -InstallerPath $installer -ReleaseNotesPath $notes -Version $version -Revision 1 -IssuedUtc '2026-09-15T00:00:00Z' -ExpiresUtc '2026-10-15T00:00:00Z' -Repository 'Shaderx/ConnectorWatch' -OutputDirectory $output
    $payloadPath = Join-Path $output 'app-release.payload.json'
    $payload = Get-Content -LiteralPath $payloadPath -Raw | ConvertFrom-Json
    $request = Get-Content -LiteralPath (Join-Path $output 'authorization-request.json') -Raw | ConvertFrom-Json
    $expectedName = if ($version -in @('1.5.0', '1.5.1')) { 'ConnectorWatch-Setup.exe' } else { "ConnectorWatch-Setup-$version.exe" }
    if ($payload.installer_url -cne "https://github.com/Shaderx/ConnectorWatch/releases/download/app-v$version/$expectedName" -or
        $payload.installer_sha256 -cne $hash -or $payload.installer_size -ne 5 -or
        $request.payload_sha256 -cne (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw "Incorrect installer metadata or authorization binding for $version"
    }
}
Write-Output 'PASS: versioned installer bytes, checksum aliases, metadata binding, and legacy renewal filenames.'

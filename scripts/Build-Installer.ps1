param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/installer'),
    [string]$ExpectedTag = '',
    [string]$IsccPath = '',
    [string]$CatalogTrustPath = '',
    [string]$InitialCatalogPath = '',
    [string]$AppTrustPath = '',
    [string]$SignToolPath = '',
    [string]$SignCertificateThumbprint = '',
    [string]$TimestampUrl = '',
    [ValidateSet('PublicTrusted', 'SelfSigned')]
    [string]$SigningMode = 'PublicTrusted',
    [switch]$UnsignedFixture
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Directory.Build.props contains an invalid release version.' }
if (-not $UnsignedFixture -and $version -notmatch '^\d+\.\d+\.\d+$') { throw 'Production app delivery requires a stable X.Y.Z version.' }
if ($ExpectedTag -and $ExpectedTag -cne "v$version") { throw "Tag must match Directory.Build.props: v$version" }
if ((Test-Path $output) -and @(Get-ChildItem $output -Force).Count) { throw 'Installer output must be empty.' }
New-Item -ItemType Directory -Force $output | Out-Null

if (-not $IsccPath) {
    $candidates = @(
        (Join-Path $root 'artifacts/build-tools/inno/ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($candidates.Count -gt 0) { $IsccPath = $candidates[0] }
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) { throw 'ISCC.exe was not found. Install Inno Setup or pass -IsccPath.' }

$stage = Join-Path $output 'stage'
$trustStage = Join-Path $output 'release-trust'
$catalogSource = ''
if (-not $UnsignedFixture) {
    foreach ($required in @($CatalogTrustPath, $InitialCatalogPath, $AppTrustPath, $SignToolPath)) {
        if (-not $required -or -not (Test-Path $required)) { throw 'A production installer requires catalog trust, initial signed catalog, app trust, and signtool paths.' }
    }
    if ($SignCertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'SignCertificateThumbprint must be a SHA-1 certificate thumbprint.' }
    if (-not [Uri]::IsWellFormedUriString($TimestampUrl, [UriKind]::Absolute) -or -not $TimestampUrl.StartsWith('https://')) { throw 'TimestampUrl must be HTTPS.' }
    $signingCertificate = @(Get-ChildItem 'Cert:\CurrentUser\My' | Where-Object Thumbprint -eq $SignCertificateThumbprint)
    if ($signingCertificate.Count -ne 1) { throw 'The selected signing certificate must be present exactly once in CurrentUser\\My.' }
    Export-Certificate -Cert $signingCertificate[0] -FilePath (Join-Path $output 'ConnectorWatch-publisher.cer') -Type CERT | Out-Null
    $publisher = $signingCertificate[0]
    [ordered]@{
        schema_version = 1; signing_mode = $SigningMode; subject = $publisher.Subject
        certificate_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publisher.RawData)).ToLowerInvariant()
        certificate_thumbprint_sha1 = $publisher.Thumbprint
        not_before_utc = $publisher.NotBefore.ToUniversalTime().ToString('o')
        not_after_utc = $publisher.NotAfter.ToUniversalTime().ToString('o')
        certificate_file = 'ConnectorWatch-publisher.cer'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'publisher.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'Prepare-ReleaseTrust.ps1') -CatalogTrustPath $CatalogTrustPath -InitialCatalogPath $InitialCatalogPath -AppTrustPath $AppTrustPath -OutputDirectory $trustStage
    if ($LASTEXITCODE -ne 0) { throw 'Release trust preparation failed.' }
    $catalogSource = Join-Path $trustStage 'DriverCatalogReleaseTrust.Generated.cs'
    & (Join-Path $PSScriptRoot 'Publish.ps1') -OutputDirectory $stage -DriverCatalogTrustSource $catalogSource -ReleaseTrustDirectory $trustStage
} else {
    & (Join-Path $PSScriptRoot 'Publish.ps1') -OutputDirectory $stage
}
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }

foreach ($publicTrustAsset in @('ConnectorWatch-publisher.cer', 'publisher.json')) {
    $asset = Join-Path $output $publicTrustAsset
    if (Test-Path $asset) { Copy-Item $asset (Join-Path $stage $publicTrustAsset) }
}

function Read-PublisherPins([object]$trust, [string]$propertyName) {
    $property = $trust.PSObject.Properties[$propertyName]
    if ($null -eq $property) { return @() }
    $values = @($property.Value)
    foreach ($value in $values) {
        if ($value -isnot [string] -or $value -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "$propertyName must contain SHA-256 certificate fingerprints."
        }
    }
    return @($values | ForEach-Object { $_.ToUpperInvariant() })
}

function Get-SignerPin([string]$path, [string]$label) {
    try {
        if ($SigningMode -eq 'SelfSigned') {
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                [Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($path))
        } else {
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            if ($null -eq $signature.SignerCertificate) { throw 'Signer certificate is missing.' }
            $certificate = $signature.SignerCertificate
        }
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData))
    } catch {
        throw "$label does not contain a readable Authenticode signer certificate: $($_.Exception.Message)"
    }
}

$selectedPins = @()
$verificationTrustPath = Join-Path $stage 'app-update-trust.json'
if (-not $UnsignedFixture) {
    $appTrust = Get-Content $verificationTrustPath -Raw | ConvertFrom-Json -Depth 20
    $publicPins = Read-PublisherPins $appTrust 'publisher_certificate_sha256'
    $selfSignedPins = Read-PublisherPins $appTrust 'self_signed_publisher_certificate_sha256'
    $selectedPins = if ($SigningMode -eq 'SelfSigned') { $selfSignedPins } else { $publicPins }
    if ($selectedPins.Count -eq 0) {
        throw "App update trust has no publisher pin for signing mode $SigningMode."
    }
}

function Verify-ProductionSignature([string]$path, [string]$label) {
    if ($SigningMode -eq 'SelfSigned') {
        if (-not (Test-Path $verificationTrustPath)) { throw 'Self-signed verification trust input is missing.' }
        & (Join-Path $stage 'ConnectorWatch.exe') '--verify-release-signature' '--file' $path '--app-trust' $verificationTrustPath
        if ($LASTEXITCODE -ne 0) { throw "Self-signed release signature verification failed: $label" }
    } else {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne 'Valid') { throw "Authenticode verification failed: ${label}: $($signature.StatusMessage)" }
    }
    $pin = Get-SignerPin $path $label
    if ($pin -notin $selectedPins) { throw "Signer for $label is absent from the $SigningMode app-update publisher pins." }
}

if (-not $UnsignedFixture) {
    $ownedBinaries = @('ConnectorWatch.exe', 'ConnectorWatch.dll', 'ConnectorWatch.Gui.exe', 'ConnectorWatch.Gui.dll')
    foreach ($name in $ownedBinaries) {
        $binary = Join-Path $stage $name
        if (-not (Test-Path $binary)) { throw "Published binary is missing: $name" }
        & $SignToolPath sign /sha1 $SignCertificateThumbprint /fd SHA256 /td SHA256 /tr $TimestampUrl /d ConnectorWatch $binary
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $name" }
        Verify-ProductionSignature $binary $name
    }
}

& (Join-Path $stage 'ConnectorWatch.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw 'Published daemon tests failed.' }
$guiReport = Join-Path $output 'installer-gui-tests.json'
$uiReport = Join-Path $output 'installer-ui-tests.json'
foreach ($test in @(
    @{ Arguments = '--self-test'; Report = $guiReport },
    @{ Arguments = '--demo --ui-self-test'; Report = $uiReport }
)) {
    $arguments = $test.Arguments + ' --test-output "' + $test.Report + '"'
    $process = Start-Process (Join-Path $stage 'ConnectorWatch.Gui.exe') -ArgumentList $arguments -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Published GUI test timed out.' }
        if ($process.ExitCode -ne 0 -or -not (Test-Path $test.Report)) { throw 'Published GUI tests failed.' }
        $report = Get-Content $test.Report -Raw | ConvertFrom-Json
        if ($report.passed -lt 1) { throw 'Published GUI test report contains no passing checks.' }
    } finally { $process.Dispose() }
}

$defines = @(
    "--define=SourceDir=$stage",
    "--define=AppVersion=$version",
    "--define=OutputDir=$output"
)
if ($UnsignedFixture) {
    $defines += '--define=OutputName=ConnectorWatch-Setup-UNSIGNED-FIXTURE'
    $defines += '--define=UnsignedFixture=1'
    $defines += "--define=FixtureInstallDir=$(Join-Path $output 'fixture-install')"
    $defines += "--define=FixtureStateRoot=$(Join-Path $output 'fixture-state')"
    & $IsccPath --no-ide-signtools @defines (Join-Path $root 'installer/ConnectorWatch.iss')
} else {
    $signCommand = '"' + $SignToolPath + '" sign /sha1 ' + $SignCertificateThumbprint + ' /fd SHA256 /td SHA256 /tr "' + $TimestampUrl + '" /d "ConnectorWatch" $f'
    $defines += '--define=OutputName=ConnectorWatch-Setup'
    $defines += '--define=SignedBuild=1'
    & $IsccPath --no-ide-signtools "--signtool=connectorwatch=$signCommand" @defines (Join-Path $root 'installer/ConnectorWatch.iss')
}
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$installer = if ($UnsignedFixture) { Join-Path $output 'ConnectorWatch-Setup-UNSIGNED-FIXTURE.exe' } else { Join-Path $output 'ConnectorWatch-Setup.exe' }
if (-not (Test-Path $installer)) { throw 'Inno Setup did not produce the expected installer.' }
if (-not $UnsignedFixture) {
    Verify-ProductionSignature $installer 'installer'
}
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($installer))`n")
@{ version = $version; sha256 = $hash; signing_mode = if ($UnsignedFixture) { 'UnsignedFixture' } else { $SigningMode }; unsigned_fixture = [bool]$UnsignedFixture; gui_checks = (Get-Content $guiReport -Raw | ConvertFrom-Json).passed; ui_checks = (Get-Content $uiReport -Raw | ConvertFrom-Json).passed; built_at_utc = [DateTimeOffset]::UtcNow } |
    ConvertTo-Json | Set-Content (Join-Path $output 'installer-build-info.json') -Encoding utf8
Write-Output "Installer verified: $installer"

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Prepare', 'Sign')] [string] $Mode,
    [Parameter(Mandatory)] [string] $InstallerPath,
    [Parameter(Mandatory)] [string] $ReleaseNotesPath,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [Parameter(Mandatory)] [long] $Revision,
    [Parameter(Mandatory)] [DateTimeOffset] $IssuedUtc,
    [Parameter(Mandatory)] [DateTimeOffset] $ExpiresUtc,
    [string] $Repository = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'Shaderx/ConnectorWatch' }),
    [string] $PreviousEnvelopePath,
    [string] $PreviousKeySpkiBase64 = $(if ($env:APP_RELEASE_PREVIOUS_KEY_SPKI_BASE64) { $env:APP_RELEASE_PREVIOUS_KEY_SPKI_BASE64 } else { $env:APP_RELEASE_PUBLIC_KEY_SPKI_BASE64 }),
    [string] $PreviousKeyId = $(if ($env:APP_RELEASE_PREVIOUS_KEY_ID) { $env:APP_RELEASE_PREVIOUS_KEY_ID } else { $env:APP_RELEASE_KEY_ID }),
    [string] $RollbackPolicyPath,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/app-release'),
    [string] $AuthorizedInstallerSha256,
    [string] $AuthorizedPayloadSha256,
    [string] $MaintainerAuthorization
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$hasPreviousKeyId = -not [string]::IsNullOrWhiteSpace($PreviousKeyId)
$hasPreviousKey = -not [string]::IsNullOrWhiteSpace($PreviousKeySpkiBase64)
if ($hasPreviousKeyId -ne $hasPreviousKey) { throw 'Previous app key id and public key must be provided together.' }

function Hash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
function ReadJson([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -Depth 40 }
function VerifyEnvelope([string]$Path, [string]$PublicKeyBase64, [string]$ExpectedKeyId) {
    $envelope = ReadJson $Path
    if ($envelope.envelope_schema -ne 1 -or $envelope.algorithm -ne 'ecdsa-p256-sha256' -or $envelope.key_id -cne $ExpectedKeyId) { throw 'Previous app envelope has an unsupported identity.' }
    $payload = [Convert]::FromBase64String($envelope.payload); $signature = [Convert]::FromBase64String($envelope.signature)
    if ($signature.Length -ne 64 -or (Hash $payload) -cne $envelope.payload_sha256) { throw 'Previous app envelope digest is invalid.' }
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $spki = [Convert]::FromBase64String($PublicKeyBase64); $read = 0; $key.ImportSubjectPublicKeyInfo($spki, [ref]$read)
        if ($read -ne $spki.Length -or -not $key.VerifyData($payload, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Previous app envelope signature is invalid.' }
    } finally { $key.Dispose() }
    return [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json -Depth 40
}

$installer = Get-Item -LiteralPath ([IO.Path]::GetFullPath($InstallerPath))
if ($installer.Name -cne 'ConnectorWatch-Setup.exe' -or $installer.Length -le 0) { throw 'Expected completed ConnectorWatch-Setup.exe.' }
if (-not (Test-Path -LiteralPath $ReleaseNotesPath -PathType Leaf)) { throw 'Release notes file is missing.' }
$notes = Get-Content -Raw -LiteralPath $ReleaseNotesPath
if ([Text.Encoding]::UTF8.GetByteCount($notes) -gt 32768) { throw 'Release notes exceed 32 KiB.' }
if ($Revision -lt 1 -or $IssuedUtc.Offset -ne [TimeSpan]::Zero -or $ExpiresUtc.Offset -ne [TimeSpan]::Zero -or
    $ExpiresUtc -le $IssuedUtc -or ($ExpiresUtc - $IssuedUtc) -gt [TimeSpan]::FromDays(31)) { throw 'Invalid app revision or validity interval.' }

$publicKey = $env:APP_RELEASE_PUBLIC_KEY_SPKI_BASE64; $keyId = $env:APP_RELEASE_KEY_ID
$previous = $null
if ($PreviousEnvelopePath) {
    if ([string]::IsNullOrWhiteSpace($publicKey) -or [string]::IsNullOrWhiteSpace($keyId)) { throw 'Pinned app publication key is required for previous state.' }
    $previous = VerifyEnvelope ([IO.Path]::GetFullPath($PreviousEnvelopePath)) $PreviousKeySpkiBase64 $PreviousKeyId
    if ($previous.payload_type -ne 'app-release' -or [long]$previous.revision -ge $Revision) { throw 'App release revision must increase from authenticated current state.' }
} elseif ($Revision -ne 1) { throw 'Previous authenticated app metadata is required after revision 1.' }

$installerBytes = [IO.File]::ReadAllBytes($installer.FullName); $installerHash = Hash $installerBytes
$offeredVersion = [version]::Parse($Version)
$rollback = $null
if ($RollbackPolicyPath) {
    $rollback = ReadJson ([IO.Path]::GetFullPath($RollbackPolicyPath))
    $expectedPolicyFields = @('allowed','minimum_source_version','maximum_source_version','minimum_data_schema','maximum_data_schema') | Sort-Object
    $actualPolicyFields = @($rollback.PSObject.Properties.Name | Sort-Object)
    if (Compare-Object $actualPolicyFields $expectedPolicyFields) { throw 'Rollback policy has missing or unknown fields.' }
    if ($rollback.allowed -isnot [bool] -or -not $rollback.allowed -or
        $rollback.minimum_source_version -notmatch '^\d+\.\d+\.\d+$' -or
        $rollback.maximum_source_version -notmatch '^\d+\.\d+\.\d+$' -or
        $rollback.minimum_data_schema -ne 3 -or $rollback.maximum_data_schema -ne 3) {
        throw 'Rollback policy must explicitly allow bounded stable source versions and only data schema 3.'
    }
    $minimumSource = [version]::Parse($rollback.minimum_source_version)
    $maximumSource = [version]::Parse($rollback.maximum_source_version)
    if ($minimumSource -gt $maximumSource) { throw 'Rollback source-version bounds are reversed.' }
    if ($minimumSource -le $offeredVersion) { throw 'Rollback minimum source version must be strictly newer than the offered target version.' }
}
if ($null -ne $previous) {
    if ($previous.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Authenticated previous app version is not stable canonical SemVer.' }
    $currentVersion = [version]::Parse($previous.version)
    if ($offeredVersion -lt $currentVersion) {
        if ($null -eq $rollback) { throw 'A lower app version requires an explicit bounded rollback policy.' }
        if ($currentVersion -lt $minimumSource -or $currentVersion -gt $maximumSource) {
            throw 'Rollback policy source-version bounds do not cover the authenticated current version.'
        }
    }
}
$payload = [ordered]@{
    payload_type = 'app-release'; schema_version = 1; revision = $Revision
    issued_utc = $IssuedUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    expires_utc = $ExpiresUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    version = $Version
    installer_url = "https://github.com/$Repository/releases/download/app-v$Version/ConnectorWatch-Setup.exe"
    installer_sha256 = $installerHash; installer_size = $installer.Length; release_notes = $notes
    minimum_data_schema = 3; maximum_data_schema = 3; rollback = $rollback
}
$output = [IO.Path]::GetFullPath($OutputDirectory); New-Item -ItemType Directory -Force $output | Out-Null
$payloadBytes = [Text.UTF8Encoding]::new($false).GetBytes(($payload | ConvertTo-Json -Depth 20 -Compress)); $payloadHash = Hash $payloadBytes
$payloadPath = Join-Path $output 'app-release.payload.json'; [IO.File]::WriteAllBytes($payloadPath, $payloadBytes)
$request = [ordered]@{ schema_version = 1; revision = $Revision; version = $Version; installer_sha256 = $installerHash; payload_sha256 = $payloadHash; authorization_required = 'Review and authorize both exact digests.' }
[IO.File]::WriteAllText((Join-Path $output 'authorization-request.json'), ($request | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
if ($Mode -eq 'Prepare') { Write-Output "Prepared app $Version installer $installerHash payload $payloadHash"; return }

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:CONNECTORWATCH_APP_PUBLICATION -ne 'protected-app-release-environment') { throw 'App signing is restricted to the protected app-release environment.' }
if ($AuthorizedInstallerSha256 -cne $installerHash -or $AuthorizedPayloadSha256 -cne $payloadHash -or
    $MaintainerAuthorization -ne 'I authorize these exact installer and app payload digests') { throw 'Maintainer app-release digest authorization does not match.' }
if ([string]::IsNullOrWhiteSpace($env:APP_RELEASE_SIGNING_KEY_PEM) -or [string]::IsNullOrWhiteSpace($keyId) -or [string]::IsNullOrWhiteSpace($publicKey)) { throw 'Protected app metadata signing material is unavailable.' }
$signatureKey = [Security.Cryptography.ECDsa]::Create()
try {
    $signatureKey.ImportFromPem($env:APP_RELEASE_SIGNING_KEY_PEM)
    if ([Convert]::ToBase64String($signatureKey.ExportSubjectPublicKeyInfo()) -cne $publicKey) { throw 'App signing key does not match pinned app public key.' }
    $signature = $signatureKey.SignData($payloadBytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
} finally { $signatureKey.Dispose() }
if ($signature.Length -ne 64) { throw 'Unexpected app metadata signature length.' }
$envelope = [ordered]@{ envelope_schema = 1; key_id = $keyId; algorithm = 'ecdsa-p256-sha256'; payload_sha256 = $payloadHash; payload = [Convert]::ToBase64String($payloadBytes); signature = [Convert]::ToBase64String($signature) }
$envelopePath = Join-Path $output 'app-release.envelope.json'
[IO.File]::WriteAllText($envelopePath, ($envelope | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/ConnectorWatch/ConnectorWatch.csproj'))
& dotnet run --project $project -c Release --no-launch-profile --no-restore -- --validate-app-release-publication $envelopePath --installer $installer.FullName --key-id $keyId --public-key-spki-base64 $publicKey --expected-version $Version
if ($LASTEXITCODE -ne 0) { throw 'Shipping app verifier rejected the generated release envelope.' }
Copy-Item -LiteralPath $envelopePath -Destination (Join-Path $output 'app-current.json')
Write-Output "Signed authorized app $Version installer $installerHash payload $payloadHash"

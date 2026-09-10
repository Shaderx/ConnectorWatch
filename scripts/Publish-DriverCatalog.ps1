[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Prepare', 'Sign')] [string] $Mode,
    [string[]] $ProposalPaths = @(),
    [string] $BootstrapAttestationPath,
    [Parameter(Mandatory)] [long] $CatalogRevision,
    [Parameter(Mandatory)] [DateTimeOffset] $IssuedUtc,
    [Parameter(Mandatory)] [DateTimeOffset] $ExpiresUtc,
    [string] $PreviousEnvelopePath,
    [string] $TrustedPublicKeySpkiBase64 = $env:DRIVER_CATALOG_PUBLIC_KEY_SPKI_BASE64,
    [string] $TrustedKeyId = $env:DRIVER_CATALOG_KEY_ID,
    [string] $PreviousKeySpkiBase64 = $(if ($env:DRIVER_CATALOG_PREVIOUS_KEY_SPKI_BASE64) { $env:DRIVER_CATALOG_PREVIOUS_KEY_SPKI_BASE64 } else { $env:DRIVER_CATALOG_PUBLIC_KEY_SPKI_BASE64 }),
    [string] $PreviousKeyId = $(if ($env:DRIVER_CATALOG_PREVIOUS_KEY_ID) { $env:DRIVER_CATALOG_PREVIOUS_KEY_ID } else { $env:DRIVER_CATALOG_KEY_ID }),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/driver-catalog'),
    [string] $AuthorizedPayloadSha256,
    [string] $MaintainerAuthorization
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$hasPreviousKeyId = -not [string]::IsNullOrWhiteSpace($PreviousKeyId)
$hasPreviousKey = -not [string]::IsNullOrWhiteSpace($PreviousKeySpkiBase64)
if ($hasPreviousKeyId -ne $hasPreviousKey) { throw 'Previous catalog key id and public key must be provided together.' }
if (-not $hasPreviousKey) {
    $PreviousKeyId = $TrustedKeyId
    $PreviousKeySpkiBase64 = $TrustedPublicKeySpkiBase64
}

function Get-Sha256Hex([byte[]] $Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-UtcTimestamp($Value) {
    if ($Value -is [DateTime] -or $Value -is [DateTimeOffset]) {
        return ([DateTimeOffset]$Value).ToUniversalTime()
    }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

function Read-JsonObject([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing JSON file: $Path" }
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -Depth 40
}

function Assert-ExactProperties($Object, [string[]] $Names, [string] $Label) {
    $actual = @($Object.PSObject.Properties.Name | Sort-Object)
    $expected = @($Names | Sort-Object)
    if (Compare-Object $actual $expected) { throw "$Label has missing or unknown fields." }
}

function Get-ScopeKey($Entry) {
    return @($Entry.vendor_id, $Entry.device_id, $Entry.subsystem_id, $Entry.os,
        $Entry.architecture, $Entry.driver_version, $Entry.reader_profile) -join '|'
}

function Read-VerifiedPreviousPayload([string] $EnvelopePath, [string] $PublicKeyBase64, [string] $KeyId) {
    if ([string]::IsNullOrWhiteSpace($PublicKeyBase64)) { throw 'The pinned catalog public key is required to verify previous state.' }
    $envelope = Read-JsonObject $EnvelopePath
    Assert-ExactProperties $envelope @('envelope_schema', 'key_id', 'algorithm', 'payload_sha256', 'payload', 'signature') 'Previous envelope'
    if ($envelope.envelope_schema -ne 1 -or $envelope.algorithm -ne 'ecdsa-p256-sha256') { throw 'Previous envelope schema or algorithm is unsupported.' }
    if (-not [string]::IsNullOrWhiteSpace($KeyId) -and $envelope.key_id -cne $KeyId) { throw 'Previous envelope key id does not match the pinned publication key.' }
    try {
        $payloadBytes = [Convert]::FromBase64String($envelope.payload)
        $signature = [Convert]::FromBase64String($envelope.signature)
        $publicKey = [Convert]::FromBase64String($PublicKeyBase64)
    } catch { throw 'Previous envelope contains malformed base64.' }
    if ($signature.Length -ne 64 -or (Get-Sha256Hex $payloadBytes) -cne $envelope.payload_sha256) {
        throw 'Previous envelope payload digest or signature length is invalid.'
    }
    $verifier = [Security.Cryptography.ECDsa]::Create()
    try {
        $bytesRead = 0
        $verifier.ImportSubjectPublicKeyInfo($publicKey, [ref]$bytesRead)
        if ($bytesRead -ne $publicKey.Length -or -not $verifier.VerifyData($payloadBytes, $signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
            throw 'Previous catalog signature is not valid under the pinned publication key.'
        }
    } finally { $verifier.Dispose() }
    return [Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json -Depth 40
}

if ($CatalogRevision -lt 1) { throw 'Catalog revision must be positive.' }
if ($IssuedUtc.Offset -ne [TimeSpan]::Zero -or $ExpiresUtc.Offset -ne [TimeSpan]::Zero) {
    throw 'Issue and expiry timestamps must be explicit UTC values.'
}
if ($ExpiresUtc -le $IssuedUtc -or ($ExpiresUtc - $IssuedUtc) -gt [TimeSpan]::FromDays(31)) {
    throw 'Catalog expiry must follow issue time and be no more than 31 days later.'
}

if ($BootstrapAttestationPath) {
    if ($CatalogRevision -ne 1 -or $PreviousEnvelopePath -or $ProposalPaths.Count -ne 0) {
        throw 'Bootstrap attestation is allowed only for revision 1 with no previous catalog or proposal paths.'
    }
}

$entriesByScope = [ordered]@{}
if ($PreviousEnvelopePath) {
    $previous = Read-VerifiedPreviousPayload ([IO.Path]::GetFullPath($PreviousEnvelopePath)) $PreviousKeySpkiBase64 $PreviousKeyId
    if ($previous.payload_type -ne 'connectorwatch-driver-catalog' -or $previous.schema_version -ne 1) {
        throw 'Previous catalog payload has an unsupported schema.'
    }
    if ([long]$previous.catalog_revision -ge $CatalogRevision) {
        throw 'Catalog revision must increase monotonically.'
    }
    foreach ($entry in $previous.entries) { $entriesByScope[(Get-ScopeKey $entry)] = $entry }
} elseif ($CatalogRevision -ne 1) {
    throw 'A previous payload is required after revision 1 so history cannot be silently dropped.'
}

$proposalPropertyNames = @('schema_version', 'proposal_status', 'evidence_sha256', 'scope_sha256', 'proposed_entry')
$entryPropertyNames = @('vendor_id', 'device_id', 'subsystem_id', 'os', 'architecture', 'driver_version',
    'reader_profile', 'minimum_reader_version', 'maximum_reader_version_exclusive', 'minimum_app_version',
    'maximum_app_version_exclusive', 'decision', 'evidence_sha256', 'decision_utc', 'rationale')

if ($BootstrapAttestationPath) {
    $attestationPath = [IO.Path]::GetFullPath($BootstrapAttestationPath)
    $attestationBytes = [IO.File]::ReadAllBytes($attestationPath)
    $projectPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/ConnectorWatch/ConnectorWatch.csproj'))
    $attestationSnapshot = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllBytes($attestationSnapshot, $attestationBytes)
        & dotnet run --project $projectPath -c Release --no-launch-profile --no-restore -- --validate-driver-approval-attestation $attestationSnapshot
        if ($LASTEXITCODE -ne 0) { throw 'The fixed bootstrap attestation policy rejected the approval record.' }
    } finally {
        Remove-Item -LiteralPath $attestationSnapshot -Force -ErrorAction SilentlyContinue
    }
    $attestation = [Text.Encoding]::UTF8.GetString($attestationBytes) | ConvertFrom-Json -Depth 40
    $attestationHash = Get-Sha256Hex $attestationBytes
    # ConvertFrom-Json can materialize UTC strings as DateTime. Parsing its
    # culture-formatted string would discard Kind and apply the host zone twice.
    $approvalUtc = Get-UtcTimestamp $attestation.approval_utc
    if ($approvalUtc -gt $IssuedUtc) { throw 'Bootstrap approval timestamp is newer than catalog issue time.' }
    foreach ($approval in $attestation.approvals) {
        $entry = [ordered]@{
            vendor_id = $approval.vendor_id
            device_id = $approval.device_id
            subsystem_id = $approval.subsystem_id
            os = $approval.os
            architecture = $approval.architecture
            driver_version = $approval.driver_version
            reader_profile = $approval.reader_profile
            minimum_reader_version = $approval.minimum_reader_version
            maximum_reader_version_exclusive = $approval.maximum_reader_version_exclusive
            minimum_app_version = $approval.minimum_app_version
            maximum_app_version_exclusive = $approval.maximum_app_version_exclusive
            decision = 'approved'
            evidence_sha256 = $attestationHash
            decision_utc = $approvalUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
            rationale = $approval.public_rationale
        }
        $entriesByScope[(Get-ScopeKey $entry)] = $entry
    }
}

foreach ($proposalInput in $ProposalPaths) {
    $proposalPath = [IO.Path]::GetFullPath($proposalInput)
    $proposal = Read-JsonObject $proposalPath
    Assert-ExactProperties $proposal $proposalPropertyNames "Proposal $proposalPath"
    if ($proposal.schema_version -ne 1 -or $proposal.proposal_status -ne 'maintainer_review_required') {
        throw "Proposal is not a full maintainer-review proposal: $proposalPath"
    }
    if ($proposal.evidence_sha256 -notmatch '^[0-9a-f]{64}$') { throw "Proposal evidence digest is invalid: $proposalPath" }
    $evidencePath = Join-Path (Split-Path -Parent $proposalPath) 'evidence.json'
    $evidenceBytes = [IO.File]::ReadAllBytes($evidencePath)
    if ((Get-Sha256Hex $evidenceBytes) -cne $proposal.evidence_sha256) {
        throw "Evidence digest mismatch for proposal: $proposalPath"
    }
    $evidence = [Text.Encoding]::UTF8.GetString($evidenceBytes) | ConvertFrom-Json -Depth 40
    $entry = $proposal.proposed_entry
    Assert-ExactProperties $entry $entryPropertyNames "Proposed entry in $proposalPath"
    if ($entry.decision -notin @('approved', 'revoked') -or $entry.reader_profile -ne 'A612-A613-v1' -or
        $entry.minimum_reader_version -ne '1.0.0' -or $entry.maximum_reader_version_exclusive -ne '1.1.0' -or
        $entry.evidence_sha256 -cne $proposal.evidence_sha256) {
        throw "Proposal does not target the supported shipping reader profile: $proposalPath"
    }
    if ($entry.decision -eq 'approved') {
        if ($evidence.schema_version -ne 1 -or $evidence.evidence_type -ne 'connectorwatch-driver-validation' -or
            $evidence.outcome -ne 'full_validation_passed' -or @($evidence.checks | Where-Object { -not $_.passed }).Count -ne 0 -or
            @($evidence.independent_comparisons).Count -ne 4 -or
            @($evidence.independent_comparisons | Where-Object { -not $_.passed }).Count -ne 0) {
            throw "Approval evidence is not a complete passing idle/workload validation: $evidencePath"
        }
    } elseif ($evidence.schema_version -ne 1 -or $evidence.evidence_type -ne 'connectorwatch-driver-revocation' -or
        $evidence.outcome -ne 'revocation_supported' -or [string]::IsNullOrWhiteSpace($entry.rationale)) {
        throw "Revocation requires a reason and a connectorwatch-driver-revocation evidence record: $evidencePath"
    }
    foreach ($field in @('vendor_id', 'device_id', 'subsystem_id', 'os', 'architecture', 'driver_version', 'reader_profile')) {
        if ($entry.$field -cne $evidence.scope.$field) { throw "Proposal widened or changed evidence scope field '$field'." }
    }
    $scopeText = @($evidence.scope.vendor_id, $evidence.scope.device_id, $evidence.scope.subsystem_id,
        $evidence.scope.os, $evidence.scope.architecture, $evidence.scope.driver_version,
        $evidence.scope.reader_profile, $evidence.scope.reader_version, $evidence.scope.app_version,
        $evidence.scope.tool_commit) -join '|'
    $scopeDigest = Get-Sha256Hex ([Text.Encoding]::UTF8.GetBytes($scopeText))
    if ($proposal.scope_sha256 -cne $scopeDigest) { throw "Proposal scope digest mismatch: $proposalPath" }
    if ((Get-UtcTimestamp $entry.decision_utc) -gt $IssuedUtc) {
        throw "Proposal decision timestamp is newer than catalog issue time: $proposalPath"
    }
    $entriesByScope[(Get-ScopeKey $entry)] = $entry
}

$payload = [ordered]@{
    payload_type = 'connectorwatch-driver-catalog'
    schema_version = 1
    catalog_revision = $CatalogRevision
    issued_utc = $IssuedUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    expires_utc = $ExpiresUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    entries = @($entriesByScope.Values | Sort-Object { Get-ScopeKey $_ })
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
if ($BootstrapAttestationPath) {
    [IO.File]::WriteAllBytes((Join-Path $outputRoot 'driver-approval-attestation.json'), $attestationBytes)
}
$payloadPath = Join-Path $outputRoot 'catalog.payload.json'
$payloadBytes = [Text.UTF8Encoding]::new($false).GetBytes(($payload | ConvertTo-Json -Depth 40 -Compress))
[IO.File]::WriteAllBytes($payloadPath, $payloadBytes)
$payloadSha256 = Get-Sha256Hex $payloadBytes
$projectPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/ConnectorWatch/ConnectorWatch.csproj'))
& dotnet run --project $projectPath -c Release --no-launch-profile --no-restore -- --validate-driver-catalog-payload $payloadPath
if ($LASTEXITCODE -ne 0) { throw 'The shipping catalog parser rejected the proposed payload.' }

$authorizationRequest = [ordered]@{
    schema_version = 1
    catalog_revision = $CatalogRevision
    payload_sha256 = $payloadSha256
    authorization_required = 'A maintainer must review this exact payload digest and authorize the protected environment run.'
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'authorization-request.json'),
    ($authorizationRequest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))

if ($Mode -eq 'Prepare') {
    Write-Output "Prepared catalog revision $CatalogRevision with SHA256 $payloadSha256"
    return
}

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:CONNECTORWATCH_CATALOG_PUBLICATION -ne 'protected-maintainer-environment') {
    throw 'Signing is restricted to the protected driver-catalog publication environment.'
}
if ($AuthorizedPayloadSha256 -cne $payloadSha256 -or $MaintainerAuthorization -ne 'I authorize this exact catalog payload digest') {
    throw 'Maintainer digest authorization does not match this exact payload.'
}
if ([string]::IsNullOrWhiteSpace($env:DRIVER_CATALOG_SIGNING_KEY_PEM) -or
    [string]::IsNullOrWhiteSpace($env:DRIVER_CATALOG_KEY_ID)) {
    throw 'Protected catalog signing material is unavailable.'
}

$ecdsa = [Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.ImportFromPem($env:DRIVER_CATALOG_SIGNING_KEY_PEM)
    $signature = $ecdsa.SignData($payloadBytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $publicKeyBase64 = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
} finally {
    $ecdsa.Dispose()
}
if ($signature.Length -ne 64) { throw 'Unexpected catalog signature length.' }
if ([string]::IsNullOrWhiteSpace($TrustedPublicKeySpkiBase64) -or $publicKeyBase64 -cne $TrustedPublicKeySpkiBase64) {
    throw 'The signing key does not match the pinned catalog publication public key.'
}
$envelope = [ordered]@{
    envelope_schema = 1
    key_id = $env:DRIVER_CATALOG_KEY_ID
    algorithm = 'ecdsa-p256-sha256'
    payload_sha256 = $payloadSha256
    payload = [Convert]::ToBase64String($payloadBytes)
    signature = [Convert]::ToBase64String($signature)
}
$envelopePath = Join-Path $outputRoot 'catalog.envelope.json'
[IO.File]::WriteAllText($envelopePath, ($envelope | ConvertTo-Json -Depth 10 -Compress),
    [Text.UTF8Encoding]::new($false))
& dotnet run --project $projectPath -c Release --no-launch-profile --no-restore -- --validate-driver-catalog-envelope $envelopePath --key-id $env:DRIVER_CATALOG_KEY_ID --public-key-spki-base64 $publicKeyBase64
if ($LASTEXITCODE -ne 0) { throw 'The shipping verifier rejected the generated signed catalog envelope.' }
Copy-Item -LiteralPath $envelopePath -Destination (Join-Path $outputRoot 'catalog-current.json')
Write-Output "Signed authorized catalog revision $CatalogRevision with SHA256 $payloadSha256"

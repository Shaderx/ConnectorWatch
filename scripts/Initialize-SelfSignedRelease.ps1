[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PrivateDirectory,
    [Parameter(Mandatory)][string]$PublicDirectory,
    [string]$Repository = 'Shaderx/ConnectorWatch'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows is required for the current-user encrypted signing backup.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository name.' }
$private = [IO.Path]::GetFullPath($PrivateDirectory)
$public = [IO.Path]::GetFullPath($PublicDirectory)
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\','/')
if ($private.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $private -eq $repo) {
    throw 'Private signing material must be stored outside the repository, including ignored directories.'
}
if (Test-Path -LiteralPath $private) { throw 'Private signing directory already exists; this command never replaces release identities.' }
if ((Test-Path -LiteralPath $public) -and (Get-ChildItem -LiteralPath $public -Force | Select-Object -First 1)) {
    throw 'Public output directory must be empty.'
}
[void][IO.Directory]::CreateDirectory($private)
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
foreach ($sid in @($identity, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
    [void]$acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $private -AclObject $acl
[void][IO.Directory]::CreateDirectory($public)
function Write-PublicJson([string]$Name, $Value) {
    $json = ($Value | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $public $Name), $json, [Text.UTF8Encoding]::new($false))
}
$rsa = [Security.Cryptography.RSA]::Create(3072)
$catalogKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$appKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$certificate = $null
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=ConnectorWatch (Self-Signed), O=ConnectorWatch Project', $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
    $eku = [Security.Cryptography.OidCollection]::new()
    [void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $true))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))
    $now = [DateTimeOffset]::UtcNow
    $certificate = $request.CreateSelfSigned($now.AddDays(-1), $now.AddYears(3))
    $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $pfx = $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)
    [IO.File]::WriteAllBytes((Join-Path $private 'authenticode.pfx'), $pfx)
    $bundle = [pscustomobject]@{
        SchemaVersion = 1
        PfxPassword = (ConvertTo-SecureString -String $password -AsPlainText -Force)
        CatalogSigningKey = (ConvertTo-SecureString -String $catalogKey.ExportPkcs8PrivateKeyPem() -AsPlainText -Force)
        AppSigningKey = (ConvertTo-SecureString -String $appKey.ExportPkcs8PrivateKeyPem() -AsPlainText -Force)
    }
    $bundle | Export-Clixml -LiteralPath (Join-Path $private 'signing-secrets.clixml')
    $der = $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    [IO.File]::WriteAllBytes((Join-Path $public 'ConnectorWatch-SelfSigned-2026.cer'), $der)
    $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($der)).ToLowerInvariant()
    $catalogId = 'connectorwatch-catalog-2026-01'
    $appId = 'connectorwatch-app-metadata-2026-01'
    $catalogTrust = [ordered]@{ schema_version = 1; keys = @([ordered]@{
        key_id = $catalogId; subject_public_key_info_base64 = [Convert]::ToBase64String($catalogKey.ExportSubjectPublicKeyInfo()); revoked = $false
    }) }
    $appTrust = [ordered]@{
        schema_version = 1; metadata_url = "https://github.com/$Repository/releases/download/app-stable/app-current.json"
        metadata_keys = @([ordered]@{ key_id = $appId; subject_public_key_info_base64 = [Convert]::ToBase64String($appKey.ExportSubjectPublicKeyInfo()); revoked = $false })
        publisher_certificate_sha256 = @(); self_signed_publisher_certificate_sha256 = @($fingerprint)
    }
    Write-PublicJson 'driver-catalog-trust.json' $catalogTrust
    Write-PublicJson 'app-update-trust.json' $appTrust
    Write-PublicJson 'publisher.json' ([ordered]@{
        schema_version = 1; signing_mode = 'SelfSigned'; subject = $certificate.Subject
        certificate_sha256 = $fingerprint; certificate_thumbprint_sha1 = $certificate.Thumbprint
        not_before_utc = $certificate.NotBefore.ToUniversalTime().ToString('o'); not_after_utc = $certificate.NotAfter.ToUniversalTime().ToString('o')
        certificate_file = 'ConnectorWatch-SelfSigned-2026.cer'
    })
    Write-Output "Created self-signed publisher: $fingerprint"
    Write-Output "Public trust: $public"
    Write-Output "Private encrypted backup: $private (Windows current-user DPAPI; preserve this account and machine for recovery)."
    Write-Output 'No certificate was installed into Windows trust stores.'
} finally {
    if ($certificate) { $certificate.Dispose() }
    $rsa.Dispose(); $catalogKey.Dispose(); $appKey.Dispose()
    $password = $null; $bundle = $null; $pfx = $null
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PrivateDirectory,
    [Parameter(Mandatory)][string]$PublicDirectory,
    [string]$Repository = 'Shaderx/ConnectorWatch',
    [string]$TimestampUrl = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository name.' }
$timestampUri = $null
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -ne 'https' -or $timestampUri.UserInfo) { throw 'TimestampUrl must be an HTTPS RFC 3161 service URL.' }
}
$private = [IO.Path]::GetFullPath($PrivateDirectory)
$public = [IO.Path]::GetFullPath($PublicDirectory)
$catalog = Get-Content -Raw -LiteralPath (Join-Path $public 'driver-catalog-trust.json') | ConvertFrom-Json
$app = Get-Content -Raw -LiteralPath (Join-Path $public 'app-update-trust.json') | ConvertFrom-Json
$secrets = Import-Clixml -LiteralPath (Join-Path $private 'signing-secrets.clixml')
if ($secrets.SchemaVersion -ne 1 -or @($catalog.keys).Count -ne 1 -or @($app.metadata_keys).Count -ne 1) {
    throw 'This bootstrap command requires the initial signing bundle and one key per role.'
}
function Invoke-GhJson([string[]]$Arguments) {
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub operation failed: $($Arguments[0])" }
    return ($result | ConvertFrom-Json)
}
function Write-ApiJson([string]$Endpoint, $Body) {
    $file = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($file, ($Body | ConvertTo-Json -Depth 20))
        & gh api --method PUT $Endpoint --input $file | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not configure $Endpoint" }
    } finally { Remove-Item -LiteralPath $file -Force }
}
function Set-Secret([string]$Environment, [string]$Name, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { throw "Missing signing secret $Name" }
    $Value | & gh secret set $Name --env $Environment --repo $Repository
    if ($LASTEXITCODE -ne 0) { throw "Could not store $Name in $Environment" }
}
function Set-Variable([string]$Environment, [string]$Name, [string]$Value) {
    & gh variable set $Name --env $Environment --repo $Repository --body $Value
    if ($LASTEXITCODE -ne 0) { throw "Could not store public variable $Name" }
}
$account = Invoke-GhJson @('api','user')
$repoInfo = Invoke-GhJson @('api',"repos/$Repository")
if (-not $repoInfo.permissions.admin) { throw 'Repository administration permission is required for protected release setup.' }
$branch = $repoInfo.default_branch
$branchInfo = Invoke-GhJson @('api',"repos/$Repository/branches/$branch")
if (-not $branchInfo.protected) {
    Write-ApiJson "repos/$Repository/branches/$branch/protection" @{
        required_status_checks = $null; enforce_admins = $true; required_pull_request_reviews = $null; restrictions = $null
        allow_force_pushes = $false; allow_deletions = $false
    }
}
$environments = Invoke-GhJson @('api',"repos/$Repository/environments")
foreach ($name in @('driver-catalog-publication', 'app-release-publication')) {
    $existing = @($environments.environments | Where-Object name -eq $name)
    if ($existing.Count -gt 0) {
        $details = Invoke-GhJson @('api',"repos/$Repository/environments/$name")
        if (@($details.protection_rules | Where-Object type -eq 'required_reviewers').Count -eq 0 -or
            -not $details.deployment_branch_policy.protected_branches) {
            throw "Existing $name policy is not protected; review it explicitly before adding signing secrets."
        }
    } else {
        Write-ApiJson "repos/$Repository/environments/$name" @{
            wait_timer = 0; prevent_self_review = $false
            reviewers = @(@{ type = 'User'; id = $account.id })
            deployment_branch_policy = @{ protected_branches = $true; custom_branch_policies = $false }
        }
    }
}
Set-Secret 'driver-catalog-publication' 'DRIVER_CATALOG_SIGNING_KEY_PEM' ([Net.NetworkCredential]::new('', $secrets.CatalogSigningKey).Password)
Set-Variable 'driver-catalog-publication' 'DRIVER_CATALOG_KEY_ID' $catalog.keys[0].key_id
Set-Variable 'driver-catalog-publication' 'DRIVER_CATALOG_PUBLIC_KEY_SPKI_BASE64' $catalog.keys[0].subject_public_key_info_base64
Set-Secret 'app-release-publication' 'APP_RELEASE_SIGNING_KEY_PEM' ([Net.NetworkCredential]::new('', $secrets.AppSigningKey).Password)
Set-Secret 'app-release-publication' 'APP_AUTHENTICODE_PFX_PASSWORD' ([Net.NetworkCredential]::new('', $secrets.PfxPassword).Password)
Set-Secret 'app-release-publication' 'APP_AUTHENTICODE_PFX_BASE64' ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $private 'authenticode.pfx'))))
Set-Variable 'app-release-publication' 'APP_RELEASE_KEY_ID' $app.metadata_keys[0].key_id
Set-Variable 'app-release-publication' 'APP_RELEASE_PUBLIC_KEY_SPKI_BASE64' $app.metadata_keys[0].subject_public_key_info_base64
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    Set-Variable 'app-release-publication' 'APP_AUTHENTICODE_TIMESTAMP_URL' $TimestampUrl
}
$secrets = $null
Write-Output "Configured protected release environments for $Repository; reviewer $($account.login); private keys were sent through stdin."

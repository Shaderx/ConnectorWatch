[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $GpuUuid,
    [Parameter(Mandatory)] [switch] $AcknowledgePrivateProbe,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $ApprovedMetadataResponseSha256,
    [string] $OracleInput,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/driver-validation'),
    [ValidateRange(2, 100)] [int] $Samples = 3,
    [ValidateRange(0, 10000)] [int] $IntervalMilliseconds = 250,
    [ValidateRange(10, 600)] [int] $ChildTimeoutSeconds = 120,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AcknowledgePrivateProbe) {
    throw 'This command invokes private read-only driver interfaces in an isolated child. Pass -AcknowledgePrivateProbe explicitly.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $repoRoot 'src/ConnectorWatch/ConnectorWatch.csproj'
$workingChanges = @(& git -c "safe.directory=$($repoRoot.Replace('\', '/'))" -C $repoRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Could not verify the validation source tree state.' }
if ($workingChanges.Count -ne 0) {
    throw 'Driver validation requires a clean committed source tree. Commit the exact shipping reader before collecting evidence.'
}
$toolCommit = (& git -c "safe.directory=$($repoRoot.Replace('\', '/'))" -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $toolCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Could not resolve the exact tool commit.'
}

$arguments = @(
    'run', '--project', $project, '-c', $Configuration, '--no-launch-profile', '--',
    '--maintainer-validate',
    '--gpu-uuid', $GpuUuid,
    '--output', $outputRoot,
    '--tool-commit', $toolCommit,
    '--approved-metadata-sha256', $ApprovedMetadataResponseSha256.ToLowerInvariant(),
    '--reader-profile', 'A612-A613-v1',
    '--reader-version', '1.0.0',
    '--samples', $Samples.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--interval-ms', $IntervalMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    '--child-timeout-seconds', $ChildTimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
)
if ($OracleInput) {
    $oraclePath = [IO.Path]::GetFullPath($OracleInput)
    if (-not (Test-Path -LiteralPath $oraclePath -PathType Leaf)) { throw "Oracle input not found: $oraclePath" }
    $arguments += @('--oracle', $oraclePath)
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Driver validation failed with exit code $LASTEXITCODE. No approval was produced."
}

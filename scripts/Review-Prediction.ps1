[CmdletBinding()]
param(
    [string]$ConfigPath = (Join-Path $env:LOCALAPPDATA 'ConnectorWatch/config.json'),
    [string]$DataDirectory = '',
    [string]$OutputDirectory = '',
    [string]$AsOf = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packagedExecutable = Join-Path $PSScriptRoot 'ConnectorWatch.exe'
$packagedAssembly = Join-Path $PSScriptRoot 'ConnectorWatch.dll'
$isPackaged = Test-Path -LiteralPath $packagedExecutable -PathType Leaf
if (-not $OutputDirectory) {
    $OutputDirectory = if ($isPackaged) {
        Join-Path $env:LOCALAPPDATA 'ConnectorWatch/prediction-reviews'
    } else {
        Join-Path $repoRoot 'artifacts/prediction-reviews'
    }
}
if (-not $DataDirectory) {
    $resolvedConfig = [IO.Path]::GetFullPath($ConfigPath)
    $config = Get-Content -LiteralPath $resolvedConfig -Raw | ConvertFrom-Json
    if (-not $config.DataDirectory) { throw 'Configuration has no DataDirectory.' }
    $DataDirectory = if ([IO.Path]::IsPathRooted($config.DataDirectory)) { $config.DataDirectory }
        else { Join-Path ([IO.Path]::GetDirectoryName($resolvedConfig)) $config.DataDirectory }
}
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
if (-not (Test-Path -LiteralPath $DataDirectory -PathType Container)) { throw "Telemetry directory missing: $DataDirectory" }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$dataPrefix = $DataDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($destination.Equals($DataDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($dataPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Review output must be outside the telemetry directory.'
}
if ($isPackaged) {
    if (-not (Test-Path -LiteralPath $packagedAssembly -PathType Leaf)) {
        throw 'Packaged evaluator assembly is missing beside ConnectorWatch.exe.'
    }
    $evaluator = $packagedAssembly
} else {
    $project = Join-Path $repoRoot 'src/ConnectorWatch/ConnectorWatch.csproj'
    if (-not $SkipBuild) {
        $assets = Join-Path $repoRoot 'src/ConnectorWatch/obj/project.assets.json'
        if (-not (Test-Path -LiteralPath $assets)) {
            & dotnet restore $project --configfile (Join-Path $repoRoot 'src/ConnectorWatch/NuGet.Config') --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Initial offline restore failed; prepare the checkout before scheduling reviews.' }
        }
        & dotnet build $project -c Release --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Prediction evaluator build failed.' }
    }
    $evaluator = Join-Path $repoRoot 'src/ConnectorWatch/bin/Release/net8.0/ConnectorWatch.dll'
}
if (-not (Test-Path -LiteralPath $evaluator -PathType Leaf)) {
    throw 'Build the evaluator before using -SkipBuild.'
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$previous = Get-ChildItem -LiteralPath $destination -Filter 'prediction-*.json' -File |
    Sort-Object Name | Select-Object -Last 1
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$reportPath = Join-Path $destination "prediction-$stamp.json"
$arguments = @('--evaluate-prediction', $DataDirectory, '--output', $reportPath)
if ($AsOf) { $arguments += @('--as-of', $AsOf) }
if ($isPackaged) { & $packagedExecutable @arguments }
else { & dotnet $evaluator @arguments }
if ($LASTEXITCODE -ne 0) { throw 'Prediction evaluation failed. Previous reports were retained.' }
$review = [ordered]@{
    generated_utc = [DateTimeOffset]::UtcNow.ToString('o')
    report = $reportPath
    summary = [IO.Path]::ChangeExtension($reportPath, '.md')
    previous_report = if ($previous) { $previous.FullName } else { $null }
    telemetry_directory = $DataDirectory
    evaluator = $evaluator
    evaluator_sha256 = (Get-FileHash -LiteralPath $evaluator -Algorithm SHA256).Hash.ToLowerInvariant()
    issue = 'https://github.com/Shaderx/ConnectorWatch/issues/19'
}
$manifestPath = Join-Path $destination "review-$stamp.json"
$review | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output "Review manifest: $manifestPath"

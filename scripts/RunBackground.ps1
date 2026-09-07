param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'config.json')
)

$ErrorActionPreference = 'Stop'
$configPath = [IO.Path]::GetFullPath($ConfigPath)
$appRoot = $PSScriptRoot
$root = Split-Path -Parent $configPath
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$dataSetting = [string]$config.DataDirectory
if ([string]::IsNullOrWhiteSpace($dataSetting)) { $dataSetting = 'data' }
$dataPath = if ([IO.Path]::IsPathRooted($dataSetting)) { $dataSetting } else { Join-Path $root $dataSetting }
New-Item -ItemType Directory -Force -Path $dataPath | Out-Null
$stdoutPath = Join-Path $dataPath 'connectorwatch.stdout.log'
$stderrPath = Join-Path $dataPath 'connectorwatch.stderr.log'

$quotedConfig = '"' + $configPath + '"'
$published = Join-Path $appRoot 'ConnectorWatch.exe'
$publishedInBin = Join-Path $appRoot 'bin\Release\net8.0\ConnectorWatch.exe'
if (Test-Path -LiteralPath $published) {
    $file = $published
    $arguments = @('--config', $quotedConfig)
} elseif (Test-Path -LiteralPath $publishedInBin) {
    $file = $publishedInBin
    $arguments = @('--config', $quotedConfig)
} else {
    $runtime = (Get-Command dotnet.exe -ErrorAction Stop).Source
    $file = $runtime
    $dll = Join-Path $appRoot 'bin\Release\net8.0\ConnectorWatch.dll'
    if (!(Test-Path -LiteralPath $dll)) { throw 'Build or publish the project first.' }
    $arguments = @('"' + $dll + '"', '--config', $quotedConfig)
}

$process = Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $appRoot `
    -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru -Wait
exit $process.ExitCode

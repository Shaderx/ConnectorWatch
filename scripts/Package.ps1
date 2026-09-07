param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/package'),
    [string]$ExpectedTag = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = ([xml](Get-Content "$root/Directory.Build.props" -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version' }
if ($ExpectedTag -and $ExpectedTag -cne "v$version") { throw "Tag must match Directory.Build.props: v$version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path $output) -and @(Get-ChildItem $output -Force).Count) { throw 'Package output must be empty; never package an existing user installation.' }
New-Item -ItemType Directory -Force $output | Out-Null
$stage = Join-Path $output 'stage'
& "$PSScriptRoot/Publish.ps1" -OutputDirectory $stage
$config = Get-Content "$stage/config.json" -Raw | ConvertFrom-Json
if ($config.GpuUuid -ne '' -or $config.DataDirectory -ne 'data') { throw 'Release configuration must be unconfigured.' }
if (Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in @('.pdb','.log') -or $_.Name -in @('status.json','baseline.json','events.csv','monitor.lock') -or $_.Name -like 'telemetry-*' }) { throw 'Runtime data or debug files found in release' }
& "$stage/ConnectorWatch.exe" --self-test
if ($LASTEXITCODE -ne 0) { throw 'Packaged daemon tests failed' }
$guiReport = Join-Path $output 'gui-tests.json'
$uiReport = Join-Path $output 'ui-tests.json'
foreach ($test in @(@('--self-test', $guiReport), @('--demo --ui-self-test', $uiReport))) {
    $arguments = $test[0] + ' --test-output "' + $test[1] + '"'
    $process = Start-Process "$stage/ConnectorWatch.Gui.exe" -ArgumentList $arguments -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Packaged GUI test timed out' }
        if ($process.ExitCode -ne 0 -or -not (Test-Path $test[1])) { throw 'Packaged GUI tests failed' }
        $report = Get-Content $test[1] -Raw | ConvertFrom-Json
        if ($report.passed -lt 1) { throw 'GUI test report contains no passing checks' }
    } finally { $process.Dispose() }
}
$archive = Join-Path $output "ConnectorWatch-v$version-win-x64.zip"
Compress-Archive -Path "$stage/*" -DestinationPath $archive
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($archive))`n")
$commit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Unable to identify source commit' }
@{ version = $version; commit = $commit; sha256 = $hash; runtime = (Get-Content "$stage/ConnectorWatch.Gui.runtimeconfig.json" -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks; guiChecks = (Get-Content $guiReport -Raw | ConvertFrom-Json).passed; uiChecks = (Get-Content $uiReport -Raw | ConvertFrom-Json).passed } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'build-info.json') -Encoding utf8
Write-Output "Release package verified: $archive"

param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../dist/ConnectorWatch-win-x64'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($project in @('ConnectorWatch', 'ConnectorWatch.Gui')) {
    dotnet publish "$root/src/$project/$project.csproj" -c Release -r win-x64 --self-contained true --source https://api.nuget.org/v3/index.json -p:DebugType=None -p:DebugSymbols=false -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
if (-not (Test-Path "$destination/config.json")) {
    Copy-Item "$root/src/ConnectorWatch/config.json" "$destination/config.json"
}
foreach ($file in @('Start.cmd', 'Start-GUI.cmd', 'RunBackground.ps1', 'Configure-Startup.ps1')) {
    Copy-Item "$PSScriptRoot/$file" $destination
}
Copy-Item "$root/README.md", "$root/LICENSE" $destination
$runtime = Get-Content "$destination/ConnectorWatch.Gui.runtimeconfig.json" -Raw | ConvertFrom-Json
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $package = Join-Path $cache ($framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version)
    $notices = Join-Path $destination ('licenses/' + $framework.name)
    New-Item -ItemType Directory -Force $notices | Out-Null
    $licenseFiles = @(Get-ChildItem $package -File | Where-Object { $_.Name -like 'LICENSE*' -or $_.Name -like '*THIRD*PARTY*' })
    if ($licenseFiles.Count -eq 0) { throw "Runtime license missing: $package" }
    $licenseFiles | Copy-Item -Destination $notices
}
Write-Output "Published to $destination"

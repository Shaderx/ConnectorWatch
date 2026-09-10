[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$VerifierPath,
    [Parameter(Mandatory)][string]$AppTrustPath,
    [Parameter(Mandatory)][ValidateSet('PublicTrusted','SelfSigned')][string]$SigningMode,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$installer = [IO.Path]::GetFullPath($InstallerPath)
$verifier = [IO.Path]::GetFullPath($VerifierPath)
$trust = [IO.Path]::GetFullPath($AppTrustPath)
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'Installer pilot needs an absent work directory.' }
$state = Join-Path $env:LOCALAPPDATA 'ConnectorWatch'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4D6D9B24-7449-4FF8-B70B-5D9514629D23}_is1'
if ((Test-Path -LiteralPath $registration) -or (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs/ConnectorWatch'))) {
    throw 'Installer pilot refuses an existing ConnectorWatch installation.'
}
foreach ($name in @('config.json','data','application-backups')) {
    if (Test-Path -LiteralPath (Join-Path $state $name)) { throw "Installer pilot refuses existing user state: $name" }
}
if (Get-Process ConnectorWatch,ConnectorWatch.Gui -ErrorAction SilentlyContinue) { throw 'Installer pilot refuses a running application.' }
[void][IO.Directory]::CreateDirectory($work)
$selectedTrust = Get-Content -Raw -LiteralPath $trust | ConvertFrom-Json -Depth 20
if ($SigningMode -eq 'SelfSigned') {
    $selectedTrust.publisher_certificate_sha256 = @()
} else {
    $selectedTrust | Add-Member -NotePropertyName self_signed_publisher_certificate_sha256 -NotePropertyValue @() -Force
}
$trust = Join-Path $work 'selected-signing-policy.json'
$selectedTrust | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $trust -Encoding utf8
$install = Join-Path $work 'installed'
$checks = [Collections.Generic.List[string]]::new()
function Assert([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "Installer pilot failed: $Label" }
    $checks.Add($Label)
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Run-Process([string]$Path, [string]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Installer pilot process timed out.' }
        if ($process.ExitCode -ne 0) { throw "Installer pilot process exited $($process.ExitCode): $Path" }
    } finally { $process.Dispose() }
}
function Verify-Signature([string]$Path) {
    & $verifier --verify-release-signature --file $Path --app-trust $trust
    if ($LASTEXITCODE -ne 0) { throw "Installer pilot signature check failed: $Path" }
}
function Trust-Store-Fingerprints {
    @('Cert:\CurrentUser\Root','Cert:\CurrentUser\TrustedPublisher','Cert:\LocalMachine\Root','Cert:\LocalMachine\TrustedPublisher') | ForEach-Object {
        $store = $_
        Get-ChildItem -LiteralPath $store | ForEach-Object { $store + ':' + $_.Thumbprint }
    } | Sort-Object -Unique
}
$originalTrust = @(Trust-Store-Fingerprints)
$originalFiles = @{}
if (Test-Path -LiteralPath $state) {
    Get-ChildItem -LiteralPath $state -File -Recurse | ForEach-Object { $originalFiles[$_.FullName] = Hash $_.FullName }
}
$config = Join-Path $state 'config.json'
$history = Join-Path $state 'data/pilot-history.txt'
try {
    Verify-Signature $installer
    Run-Process $installer ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="" /DIR="' + $install + '" /LOG="' + (Join-Path $work 'install.log') + '"')
    Assert (Test-Path -LiteralPath $registration) 'Per-user uninstall registration created'
    Assert ((Test-Path -LiteralPath (Join-Path $install 'ConnectorWatch.exe')) -and (Test-Path -LiteralPath $config)) 'Application and per-user configuration installed'
    $uninstaller = Join-Path $install 'unins000.exe'
    Verify-Signature $uninstaller
    $checks.Add('Installed uninstaller signature verified')
    foreach ($name in @('ConnectorWatch.exe','ConnectorWatch.dll','ConnectorWatch.Gui.exe','ConnectorWatch.Gui.dll')) {
        Verify-Signature (Join-Path $install $name)
    }
    $checks.Add('All four installed application binary signatures verified')
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $history))
    [IO.File]::WriteAllText($history, 'Synthetic installer-pilot history; no hardware was probed.')
    $configHash = Hash $config
    $historyHash = Hash $history
    $readme = Join-Path $install 'README.md'
    $readmeHash = Hash $readme
    Run-Process $installer ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1 /TASKS="" /DIR="' + $install + '" /LOG="' + (Join-Path $work 'upgrade.log') + '"')
    Assert ((Hash $config) -ceq $configHash -and (Hash $history) -ceq $historyHash) 'Upgrade preserved configuration and history bytes'
    $backups = @(Get-ChildItem -LiteralPath (Join-Path $state 'application-backups') -Directory)
    Assert ($backups.Count -eq 1) 'Upgrade created one application recovery backup'
    [IO.File]::WriteAllText($readme, 'Synthetic interrupted replacement for recovery verification.')
    & $verifier --deployment-restore --backup $backups[0].FullName --config $config --confirm-prelaunch-recovery
    if ($LASTEXITCODE -ne 0) { throw 'Signed installer pilot recovery failed.' }
    Assert ((Hash $readme) -ceq $readmeHash) 'Recovery restored original application bytes'
    Assert ((Hash $config) -ceq $configHash -and (Hash $history) -ceq $historyHash) 'Recovery preserved configuration and history bytes'
    Verify-Signature $uninstaller
    Run-Process $uninstaller ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + (Join-Path $work 'uninstall.log') + '"')
    Assert (-not (Test-Path -LiteralPath $registration)) 'Uninstall removed per-user registration'
    Assert (-not (Test-Path -LiteralPath (Join-Path $install 'ConnectorWatch.exe'))) 'Uninstall removed application binary'
    Assert ((Hash $config) -ceq $configHash -and (Hash $history) -ceq $historyHash) 'Uninstall preserved configuration and history bytes'
    foreach ($path in $originalFiles.Keys) {
        Assert ((Test-Path -LiteralPath $path) -and (Hash $path) -ceq $originalFiles[$path]) 'Existing diagnostic state remained unchanged'
    }
    Assert (-not (Compare-Object $originalTrust @(Trust-Store-Fingerprints))) 'CurrentUser and LocalMachine Root and TrustedPublisher stores remained unchanged'
    [ordered]@{
        schema_version = 1; installer_sha256 = (Hash $installer).ToLowerInvariant()
        signing_mode = $SigningMode
        passed = $checks.Count; checks = $checks.ToArray(); hardware_probed = $false
        profile = 'current Windows user; clean application configuration and registration'
        state_preserved = $state; completed_utc = [DateTimeOffset]::UtcNow.ToString('o')
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'signed-installer-pilot.json') -Encoding utf8
    Write-Output "Signed installer pilot passed $($checks.Count) checks. Synthetic state is preserved at $state."
} catch {
    Write-Error "Signed installer pilot stopped; retained diagnostics and any installation at $work. $($_.Exception.Message)" -ErrorAction Continue
    throw
}

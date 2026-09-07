param(
    [ValidateSet('Gui','Monitor','Remove')][string]$Mode = 'Gui',
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'config.json')
)
$ErrorActionPreference = 'Stop'
$taskName = 'ConnectorWatch'
if ($Mode -eq 'Remove') {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    return
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (-not (Test-Path -LiteralPath $ConfigPath)) { throw 'Configuration does not exist.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
if ($Mode -eq 'Gui') {
    $executable = Join-Path $PSScriptRoot 'ConnectorWatch.Gui.exe'
    $arguments = '--tray --config "' + $ConfigPath + '"'
} else {
    $executable = (Get-Command powershell.exe -ErrorAction Stop).Source
    $wrapper = Join-Path $PSScriptRoot 'RunBackground.ps1'
    if (-not (Test-Path -LiteralPath $wrapper)) { throw 'RunBackground.ps1 is missing.' }
    $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $wrapper + '" -ConfigPath "' + $ConfigPath + '"'
}
if (-not (Test-Path -LiteralPath $executable)) { throw 'Publish the complete GUI package first.' }
$action = New-ScheduledTaskAction -Execute $executable -Argument $arguments -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force
Write-Output "ConnectorWatch startup configured: $Mode. It will start at your next logon."

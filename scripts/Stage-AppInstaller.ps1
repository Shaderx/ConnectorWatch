[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InstallerPath,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [Parameter(Mandatory)] [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.Name -cne 'ConnectorWatch-Setup.exe' -or $installer.Length -le 0) {
    throw 'Expected the verified canonical installer from Prepare.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$names = @("ConnectorWatch-Setup-$Version.exe", 'ConnectorWatch-Setup.exe')
$hash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
foreach ($name in $names) {
    $destination = Join-Path $output $name
    Copy-Item -LiteralPath $installer.FullName -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) {
        throw 'Staged installer differs from the verified Prepare artifact.'
    }
}
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'),
    (($names | ForEach-Object { "$hash  $_" }) -join "`n") + "`n", [Text.UTF8Encoding]::new($false))

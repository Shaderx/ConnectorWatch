[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CatalogTrustPath,
    [Parameter(Mandatory)][string]$InitialCatalogPath,
    [Parameter(Mandatory)][string]$AppTrustPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/ConnectorWatch/ConnectorWatch.csproj'))
& dotnet restore $project --configfile (Join-Path (Split-Path -Parent $project) 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Unable to restore the release trust validator.' }
& dotnet run --project $project -c Release --no-restore --no-launch-profile -- --prepare-release-trust --catalog-trust ([IO.Path]::GetFullPath($CatalogTrustPath)) --initial-catalog ([IO.Path]::GetFullPath($InitialCatalogPath)) --app-trust ([IO.Path]::GetFullPath($AppTrustPath)) --output ([IO.Path]::GetFullPath($OutputDirectory))
if ($LASTEXITCODE -ne 0) { throw 'Release trust validation failed; no production package can be built.' }

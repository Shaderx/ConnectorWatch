@echo off
cd /d "%~dp0"
if exist "%~dp0ConnectorWatch.exe" (
  "%~dp0ConnectorWatch.exe" --config "%~dp0config.json"
) else if exist "%~dp0bin\Release\net8.0\ConnectorWatch.exe" (
  "%~dp0bin\Release\net8.0\ConnectorWatch.exe" --config "%~dp0config.json"
) else (
  dotnet "%~dp0bin\Release\net8.0\ConnectorWatch.dll" --config "%~dp0config.json"
)
pause

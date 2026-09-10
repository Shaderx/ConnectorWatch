#ifndef SourceDir
  #error SourceDir must point to a verified self-contained publish directory.
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef OutputName
  #define OutputName "ConnectorWatch-Setup"
#endif

[Setup]
#ifdef UnsignedFixture
AppId={{B33AC83E-94F4-4571-8192-0C751522E8A4}
#else
AppId={{4D6D9B24-7449-4FF8-B70B-5D9514629D23}
#endif
AppName=ConnectorWatch
AppVersion={#AppVersion}
AppPublisher=ConnectorWatch
AppPublisherURL=https://github.com/Shaderx/ConnectorWatch
AppSupportURL=https://github.com/Shaderx/ConnectorWatch/issues
#ifdef UnsignedFixture
DefaultDirName={#FixtureInstallDir}
#else
DefaultDirName={localappdata}\Programs\ConnectorWatch
#endif
#ifdef UnsignedFixture
DefaultGroupName=ConnectorWatch Installer Fixture
#else
DefaultGroupName=ConnectorWatch
#endif
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter=ConnectorWatch.exe,ConnectorWatch.Gui.exe
UninstallDisplayIcon={app}\ConnectorWatch.Gui.exe
UninstallDisplayName=ConnectorWatch
VersionInfoVersion={#AppVersion}
VersionInfoDescription=ConnectorWatch per-user installer
MinVersion=10.0.17763
#ifdef SignedBuild
SignTool=connectorwatch
SignedUninstaller=yes
SignToolRetryCount=3
SignToolMinimumTimeBetween=1000
#else
SignedUninstaller=no
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start ConnectorWatch when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "config.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\config.json"; DestDir: "{app}"; DestName: "config.template.json"; Flags: ignoreversion

[Icons]
#ifndef UnsignedFixture
Name: "{group}\ConnectorWatch"; Filename: "{app}\ConnectorWatch.Gui.exe"; Parameters: "--config ""{code:StateConfigPath}"""
Name: "{autodesktop}\ConnectorWatch"; Filename: "{app}\ConnectorWatch.Gui.exe"; Parameters: "--config ""{code:StateConfigPath}"""; Tasks: desktopicon
Name: "{userstartup}\ConnectorWatch"; Filename: "{app}\ConnectorWatch.Gui.exe"; Parameters: "--tray --config ""{code:StateConfigPath}"""; Tasks: startup
#endif

[Run]
Filename: "{app}\ConnectorWatch.Gui.exe"; Parameters: "--config ""{code:GetLaunchConfig}"""; Flags: nowait skipifsilent; Check: IsUpdateInstall
Filename: "{app}\ConnectorWatch.Gui.exe"; Parameters: "--config ""{code:GetLaunchConfig}"""; Description: "Launch ConnectorWatch"; Flags: nowait postinstall skipifsilent; Check: not IsUpdateInstall

[Code]
var
  ImportChoicePage: TInputOptionWizardPage;
  ImportFilePage: TInputFileWizardPage;
  RemoveUserData: Boolean;

function DefaultConfigPath: String;
begin
#ifdef UnsignedFixture
  Result := '{#FixtureStateRoot}\config.json';
#else
  Result := ExpandConstant('{localappdata}\ConnectorWatch\config.json');
#endif
end;

function StateConfigPath(Param: String): String;
begin
  Result := DefaultConfigPath;
end;

function StateDataPath: String;
begin
#ifdef UnsignedFixture
  Result := '{#FixtureStateRoot}\data';
#else
  Result := ExpandConstant('{localappdata}\ConnectorWatch\data');
#endif
end;

function StateRootPath: String;
begin
#ifdef UnsignedFixture
  Result := '{#FixtureStateRoot}';
#else
  Result := ExpandConstant('{localappdata}\ConnectorWatch');
#endif
end;

function GetLaunchConfig(Param: String): String;
var
  I: Integer;
  Prefix: String;
begin
  Result := DefaultConfigPath;
  Prefix := '/CONFIG=';
  for I := 1 to ParamCount do
    if CompareText(Copy(ParamStr(I), 1, Length(Prefix)), Prefix) = 0 then begin
      Result := Copy(ParamStr(I), Length(Prefix) + 1, MaxInt);
      Exit;
    end;
end;

function IsUpdateInstall: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE=1') = 0 then begin Result := True; Exit; end;
end;

function HasParameter(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then begin Result := True; Exit; end;
end;

procedure InitializeWizard;
begin
  ImportChoicePage := CreateInputOptionPage(wpSelectTasks,
    'Import a portable installation',
    'ConnectorWatch can copy an existing portable configuration and history.',
    'The portable directory is backed up and left in place. Import is optional.', True, False);
  ImportChoicePage.Add('Import an existing portable installation');
  ImportChoicePage.Values[0] := False;
  ImportFilePage := CreateInputFilePage(ImportChoicePage.ID,
    'Select the portable configuration',
    'Choose config.json from the existing portable ConnectorWatch directory.',
    'The data directory named by this file must remain below the selected portable directory.');
  ImportFilePage.Add('Portable config.json:', 'JSON files|*.json|All files|*.*', '.json');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = ImportFilePage.ID) and (not ImportChoicePage.Values[0]);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ImportFilePage.ID) and
     ((ImportFilePage.Values[0] = '') or (not FileExists(ImportFilePage.Values[0]))) then begin
    MsgBox('Select an existing portable config.json file.', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  ExistingHelper: String;
begin
  Result := '';
  ExistingHelper := ExpandConstant('{app}\ConnectorWatch.exe');
  if FileExists(ExistingHelper) and FileExists(GetLaunchConfig('')) then begin
    if (not Exec(ExistingHelper,
      '--deployment-stop --config "' + GetLaunchConfig('') + '" --wait-seconds 30',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then
      begin Result := 'ConnectorWatch could not stop cleanly. Close the dashboard and retry the upgrade.'; Exit; end;
    if (not Exec(ExistingHelper,
      '--deployment-backup --source-app "' + ExpandConstant('{app}') +
      '" --backup-root "' + StateRootPath + '\application-backups' +
      '" --restore-app "' + ExpandConstant('{app}') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then
      Result := 'ConnectorWatch could not create and verify its pre-upgrade application backup.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  ForceDirectories(StateRootPath);
  if ImportChoicePage.Values[0] then begin
    if (not Exec(ExpandConstant('{app}\ConnectorWatch.exe'),
      '--import-portable --source-config "' + ImportFilePage.Values[0] +
      '" --destination-config "' + DefaultConfigPath +
      '" --destination-data "' + StateDataPath + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then
      RaiseException('Portable data import failed. The original portable directory was not changed.');
  end else if not FileExists(DefaultConfigPath) then begin
    if not CopyFile(ExpandConstant('{app}\config.template.json'), DefaultConfigPath, False) then
      RaiseException('Unable to initialize the per-user configuration.');
  end;
end;

function InitializeUninstall: Boolean;
var
  ExitCode: Integer;
begin
  Result := True;
  if FileExists(ExpandConstant('{app}\ConnectorWatch.exe')) and FileExists(DefaultConfigPath) then begin
    if (not Exec(ExpandConstant('{app}\ConnectorWatch.exe'),
      '--deployment-stop --config "' + DefaultConfigPath + '" --wait-seconds 30',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode)) or (ExitCode <> 0) then begin
      MsgBox('ConnectorWatch could not flush and stop. Uninstall was cancelled to protect monitoring data.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
  RemoveUserData := HasParameter('/REMOVEUSERDATA=1');
  if (not UninstallSilent) and (not RemoveUserData) then
    RemoveUserData := MsgBox(
      'Keep ConnectorWatch monitoring history, configuration, references, and migration backups?' + #13#10 + #13#10 +
      'Choose Yes to preserve them (recommended). Choose No only to remove all ConnectorWatch user data.',
      mbConfirmation, MB_YESNO) = IDNO;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
    DelTree(StateRootPath, True, True, True);
end;

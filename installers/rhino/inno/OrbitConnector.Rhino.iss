; -----------------------------------------------------------------------------
; ORBIT Connector for Rhino -- Inno Setup script
;
; Compiled by .github/workflows/release.yml on windows-latest with Inno Setup 6
; (pre-installed on the runner; falls back to `choco install innosetup`).
;
; Required compile-time defines:
;
;   /DPayloadDir=<absolute path containing OrbitConnector.Rhino.rhp + dep DLLs>
;   /DConnectorVersion=<x.y.z>
;
; Optional defines:
;
;   /DLicenseFile=<absolute path to LICENCE.txt>  (default: ..\..\..\LICENCE.txt)
;   /DSetupIcon=<absolute path to OrbitConnector.ico>
;
; Wizard:
;   1. Welcome
;   2. Licence (MIT placeholder until the maintainer drops a real LICENCE.txt
;      at repo root or passes /DLicenseFile)
;   3. Rhino version pick (8 default; reserved for future 7 support)
;   4. Install dir picker (defaults to %LOCALAPPDATA%\Programs\OrbitConnector\
;      Rhino\<version>, OUTSIDE any Rhino-managed directory tree).
;   5. Install
;   6. Finish
;
; AppId MUST stay constant across releases so Windows recognises upgrades.
; Do not regenerate this GUID.
;
; -----------------------------------------------------------------------------
; v0.1.4 INSTALLER PATH HOTFIX
; -----------------------------------------------------------------------------
; Earlier v0.1.x installers (0.1.0 - 0.1.3) wrote the payload to
;   %APPDATA%\McNeel\Rhinoceros\packages\8.0\OrbitConnector\<version>\
; That path is Rhino's YAK Package Manager managed root. Rhino scans it on
; every launch, compares each subfolder against its internal package registry,
; and any folder Rhino didn't install itself is treated as an "uninstalled
; package" and wiped (Rhino's startup log shows the smoking gun:
;   [PackageManager] Cleaning up uninstalled packages...
;   [PackageManager] Removing OrbitConnector
; ).
;
; Net effect on v0.1.3: the .rhp got deleted before Rhino's plug-in loader
; could find it, the auto-register registry key pointed at a nonexistent file,
; and the Start Menu shortcut for "Install in Rhino 8" became a broken link.
;
; v0.1.4 moves the payload OUT of any Rhino-managed path. The HKCU plug-in
; registry entry still points Rhino's separate plug-in loader at the .rhp's
; full path, so auto-load on startup keeps working as designed in v0.1.3.
; A post-install [Code] hook also tidies up any orphan v0.1.x folder still
; sitting in the YAK-managed dir from a prior install.
; -----------------------------------------------------------------------------

#define AppName       "ORBIT Connector for Rhino"
#define AppPublisher  "REBUS-ORBIT"
#define AppId         "{{D7E4A9C2-3F8B-4A11-9E07-1B5C6D8E2F40}"

; PluginGuid MUST match the [assembly: Guid(...)] attribute on the Rhino
; plug-in assembly (see src/OrbitConnector.Rhino/Properties/AssemblyInfo.cs).
; Rhino uses this GUID as the plug-in's persistent identity under
;   HKCU\Software\McNeel\Rhinoceros\8.0\Plug-ins\{<PluginGuid>}.
; Update both together; never regenerate independently.
#define PluginGuid    "4F3A2B1C-8E5D-4A9F-B6C2-1D7E3F4A5B6C"

#ifndef ConnectorVersion
  #define ConnectorVersion "0.0.0"
#endif

#ifndef PayloadDir
  #error PayloadDir is required.  Pass /DPayloadDir=<absolute path> to ISCC.
#endif

#ifndef LicenseFile
  #define LicenseFile "..\..\..\LICENCE.txt"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#ConnectorVersion}
AppVerName={#AppName} {#ConnectorVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/REBUS-ORBIT/orbit-connectors
AppSupportURL=https://github.com/REBUS-ORBIT/orbit-connectors/issues
AppUpdatesURL=https://github.com/REBUS-ORBIT/orbit-connectors/releases/latest
; Per-user Programs folder, OUTSIDE any Rhino-managed directory. Resolves to
; C:\Users\<user>\AppData\Local\Programs\OrbitConnector\Rhino\<version>\
; with PrivilegesRequired=lowest below. Rhino's YAK Package Manager does not
; touch this path, so the .rhp survives every Rhino startup.
DefaultDirName={localappdata}\Programs\OrbitConnector\Rhino\{#ConnectorVersion}
DefaultGroupName=ORBIT
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes
; Per-user install: writes only to %APPDATA% + %LOCALAPPDATA%, no admin needed.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
LicenseFile={#LicenseFile}
OutputDir=.
OutputBaseFilename=OrbitConnector-Rhino-Setup-v{#ConnectorVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName={#AppName}
VersionInfoVersion={#ConnectorVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=ORBIT Connector for Rhino installer
VersionInfoProductName={#AppName}

; Optional brand icon -- the build script passes /DSetupIcon when it exists.
#ifdef SetupIcon
SetupIconFile={#SetupIcon}
UninstallDisplayIcon={#SetupIcon}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Drop the entire payload (rhp + dep DLLs) into the per-version package folder.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; \
        Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; -----------------------------------------------------------------------------
; Rhino 8 per-user plug-in registration.
;
; Writes the discovery keys Rhino reads on startup, so the plug-in loads
; automatically on the next Rhino launch without the user needing to
; drag-drop the .rhp first or run the "Install in Rhino 8" Start Menu
; shortcut. Per-user (HKCU) matches PrivilegesRequired=lowest above.
;
; The GUID is written as a bare hex string with no enclosing braces --
; this matches the convention Rhino itself uses when it registers a
; plug-in from a drag-drop or `_PluginManager` install, and matches every
; existing key under `HKCU\Software\McNeel\Rhinoceros\8.0\Plug-ins\` on a
; standard Rhino 8 install. Both forms parse as a valid System.Guid, but
; no-braces is the form Rhino's reader emits and matches.
;
; Flags: uninsdeletekey on the first entry so uninstall removes the GUID
; subkey (and every value under it) cleanly. The second entry shares the
; same key, no separate flag needed.
;
; On upgrade (e.g. 0.1.3 -> 0.1.4), Inno Setup overwrites the FileName
; value with the current {app} path -- so the registry always points at
; the version that's actually installed.
; -----------------------------------------------------------------------------
Root: HKCU; Subkey: "Software\McNeel\Rhinoceros\8.0\Plug-ins\{#PluginGuid}"; \
  ValueType: string; ValueName: "Name"; ValueData: "{#AppName}"; \
  Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\McNeel\Rhinoceros\8.0\Plug-ins\{#PluginGuid}"; \
  ValueType: string; ValueName: "FileName"; ValueData: "{app}\OrbitConnector.Rhino.rhp"

[Icons]
; Manual fallback Start Menu entry pointing at the .rhp file. The installer
; already registers the plug-in automatically (see [Registry] above) so this
; shortcut is rarely needed -- it's kept for the edge cases where Rhino was
; running during install, the user wants to register against a different
; Rhino version, or some other manual reset is required.
Name: "{group}\Install in Rhino 8"; Filename: "{app}\OrbitConnector.Rhino.rhp"; \
  Comment: "Manually re-register the connector with Rhino (usually not needed -- installer registers automatically)"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
; URL shortcut to the GitHub releases page. This is intentionally added
; because its target is a URL rather than a file -- if the .rhp ever gets
; deleted (for whatever reason), this shortcut still resolves and keeps
; the "ORBIT" Start Menu folder visible to the user as a marker that the
; install completed.
Name: "{group}\ORBIT Connector Updates"; \
  Filename: "https://github.com/REBUS-ORBIT/orbit-connectors/releases/latest"; \
  Comment: "Open the ORBIT Connectors release page on GitHub"

[Messages]
FinishedLabel=ORBIT Connector for Rhino has been installed and registered with Rhino 8.%n%nThe plug-in will load automatically the next time you start Rhino. If Rhino is currently running, restart it to load the connector.

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; -----------------------------------------------------------------------------
[Code]
var
  RhinoVersionPage: TInputOptionWizardPage;

procedure InitializeWizard;
begin
  // Reserved for future Rhino 7 / 8 split. For v0.1.x we only ship rh8 but
  // surface the page so the wizard already looks like the multi-version
  // installer it will become.
  RhinoVersionPage := CreateInputOptionPage(wpWelcome,
    'Choose Rhino version',
    'Select the Rhino major version to install the connector for.',
    'ORBIT Connector currently supports Rhino 8 only. Rhino 7 support is planned.',
    True, False);
  RhinoVersionPage.Add('Rhino 8 (recommended)');
  RhinoVersionPage.Add('Rhino 7 (not yet supported -- will warn)');
  RhinoVersionPage.SelectedValueIndex := 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = RhinoVersionPage.ID then
  begin
    if RhinoVersionPage.SelectedValueIndex = 1 then
    begin
      if MsgBox('Rhino 7 is not yet supported by this build. Continue anyway?',
                mbConfirmation, MB_YESNO) = IDNO then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;
end;

// ---------------------------------------------------------------------------
// v0.1.4 post-install cleanup of orphan YAK-managed-dir leftovers from
// v0.1.0 - v0.1.3. See header comment for the full root-cause writeup.
//
// Path we want to remove:
//   %APPDATA%\McNeel\Rhinoceros\packages\8.0\OrbitConnector\
//
// We DO NOT publish this connector through McNeel's YAK package registry,
// so the only way that directory exists on the user's machine is leftover
// from an earlier (broken) Inno install. Even so, we belt-and-brace it:
//   1. Skip cleanup if Rhino is currently running (file locks would fail
//      the delete and the user would think the new install was broken).
//   2. Use Inno's built-in DelTree which already silently no-ops on
//      missing paths, so the cleanup runs harmlessly on a fresh machine.
// ---------------------------------------------------------------------------

function GetOrphanYakDir(): String;
begin
  Result := ExpandConstant('{userappdata}\McNeel\Rhinoceros\packages\8.0\OrbitConnector');
end;

function IsRhinoRunning(): Boolean;
var
  ResultCode: Integer;
  TempFile:   String;
  TempLines:  TArrayOfString;
  i:          Integer;
begin
  // Best-effort detection: shell out to tasklist with a Rhino.exe filter
  // and grep the output. If anything in this chain fails (tasklist missing,
  // file write blocked, etc.) we fall back to "not running" so we don't
  // skip cleanup forever.
  Result := False;
  TempFile := ExpandConstant('{tmp}\rhino-running-check.txt');

  if not Exec(ExpandConstant('{cmd}'),
              '/C tasklist /FI "IMAGENAME eq Rhino.exe" /NH > "' + TempFile + '" 2>&1',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;

  if not LoadStringsFromFile(TempFile, TempLines) then
  begin
    DeleteFile(TempFile);
    Exit;
  end;

  for i := 0 to GetArrayLength(TempLines) - 1 do
  begin
    if Pos('Rhino.exe', TempLines[i]) > 0 then
    begin
      Result := True;
      Break;
    end;
  end;

  DeleteFile(TempFile);
end;

procedure CleanupOrphanYakDir();
var
  YakDir: String;
begin
  YakDir := GetOrphanYakDir();

  if not DirExists(YakDir) then
  begin
    Log('CleanupOrphanYakDir: no orphan dir at ' + YakDir + ' (clean machine).');
    Exit;
  end;

  if IsRhinoRunning() then
  begin
    Log('CleanupOrphanYakDir: Rhino is running -- skipping cleanup of ' + YakDir);
    MsgBox(
      'NOTE: A folder from an earlier ORBIT Connector install was detected in' + #13#10 +
      'Rhino''s YAK-managed directory:' + #13#10 + #13#10 +
      YakDir + #13#10 + #13#10 +
      'Rhino is currently running, so cleanup was skipped. Close Rhino and' + #13#10 +
      'delete this folder manually after installation, or simply re-run this' + #13#10 +
      'installer with Rhino closed and it will be cleaned up automatically.',
      mbInformation, MB_OK);
    Exit;
  end;

  Log('CleanupOrphanYakDir: removing orphan dir at ' + YakDir);
  if DelTree(YakDir, True, True, True) then
    Log('CleanupOrphanYakDir: DelTree succeeded.')
  else
    Log('CleanupOrphanYakDir: DelTree failed (non-fatal, continuing).');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    CleanupOrphanYakDir();
end;

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
;   4. Install dir picker (defaults to %APPDATA%\McNeel\Rhinoceros\packages\8.0\
;      OrbitConnector\<version>, the same per-version layout YAK uses)
;   5. Install
;   6. Finish
;
; AppId MUST stay constant across releases so Windows recognises upgrades.
; Do not regenerate this GUID.
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
; Default per-user McNeel package path. Per-version subdir matches YAK's
; %APPDATA%\McNeel\Rhinoceros\packages\<rhino-major>\<package>\<version>\
; layout, which keeps Rhino's plug-in manager happy on a side-by-side install.
DefaultDirName={userappdata}\McNeel\Rhinoceros\packages\8.0\OrbitConnector\{#ConnectorVersion}
DefaultGroupName=ORBIT
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes
; Per-user install: writes only to %APPDATA% + %LOCALAPPDATA%, no admin needed.
; Matches Rhino's own package manager behaviour.
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
; In Inno Setup, a literal `{` inside a Subkey: string must be escaped as
; `{{`. So the {<PluginGuid>} braces become `{{` + PluginGuid + `}` below.
;
; Flags: uninsdeletekey on the first entry so uninstall removes the GUID
; subkey (and every value under it) cleanly. The second entry shares the
; same key, no separate flag needed.
;
; On upgrade (e.g. 0.1.2 -> 0.1.3), Inno Setup overwrites the FileName
; value with the current {app} path -- so the registry always points at
; the version that's actually installed.
; -----------------------------------------------------------------------------
Root: HKCU; Subkey: "Software\McNeel\Rhinoceros\8.0\Plug-ins\{{{#PluginGuid}}"; \
  ValueType: string; ValueName: "Name"; ValueData: "{#AppName}"; \
  Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\McNeel\Rhinoceros\8.0\Plug-ins\{{{#PluginGuid}}"; \
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

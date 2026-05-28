; -----------------------------------------------------------------------------
; ORBIT Connector for Vectorworks -- Inno Setup script
;
; SCAFFOLD ONLY for v0.1.1. The Vectorworks plug-in source does not exist
; yet (see src/OrbitConnector.Vectorworks/README.md). This installer
; compiles cleanly, produces a per-user .exe, and deploys a single
; README.txt explaining the "coming soon" status. The pipeline shape is
; what matters at this stage -- the payload comes later.
;
; Required compile-time defines:
;
;   /DConnectorVersion=<x.y.z>
;   /DPayloadDir=<absolute path containing the placeholder README.txt>
;
; Optional defines:
;
;   /DLicenseFile=<absolute path to LICENCE.txt>
;
; AppId MUST stay constant across releases so Windows recognises upgrades.
; -----------------------------------------------------------------------------

#define AppName       "ORBIT Connector for Vectorworks"
#define AppPublisher  "REBUS-ORBIT"
#define AppId         "{{F2A6C1D9-8E47-4B22-9C30-7A1F8E5D2B61}"

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
DefaultDirName={localappdata}\OrbitConnector\Vectorworks\{#ConnectorVersion}
DefaultGroupName=ORBIT
DisableProgramGroupPage=yes
DisableDirPage=auto
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
LicenseFile={#LicenseFile}
OutputDir=.
OutputBaseFilename=OrbitConnector-Vectorworks-Setup-v{#ConnectorVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName={#AppName}
VersionInfoVersion={#ConnectorVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=ORBIT Connector for Vectorworks installer (scaffold)
VersionInfoProductName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; \
        Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ORBIT Connector for Vectorworks (info)"; Filename: "{app}\README.txt"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Messages]
WelcomeLabel2=This is a placeholder for the Vectorworks connector. The actual plug-in is under development. The installer will drop a README into [name] explaining where to follow the work.%n%nWatch https://github.com/REBUS-ORBIT/orbit-connectors for updates.
FinishedLabel=ORBIT Connector for Vectorworks (scaffold) has been installed.%n%nNo plug-in has been loaded -- this release only ships a placeholder README. Watch https://github.com/REBUS-ORBIT/orbit-connectors for real Vectorworks support.

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

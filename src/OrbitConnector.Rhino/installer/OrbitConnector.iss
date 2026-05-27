; ORBIT Rhino Connector — Inno Setup script
; Run via Build-Installer.ps1 or directly with ISCC.exe

#define MyAppName      "ORBIT Connector for Rhino"
#define MyAppPublisher "REBUS Industries"
#define MyAppVersion   "1.0.0"
#define MyPluginGuid   "A1B2C3D4-ORBIT-0001-0001-000000000001"
#define MyOutputDir    "dist"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={userappdata}\McNeel\Rhinoceros\packages\8.0\OrbitConnector\{#MyAppVersion}
DisableDirPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename=OrbitConnector-Rhino-{#MyAppVersion}-Setup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
UninstallDisplayName={#MyAppName}

[Files]
; Main plugin file
Source: "src\OrbitConnector.Rhino\bin\Release\net8.0-windows\OrbitConnector.Rhino.rhp"; \
        DestDir: "{app}"; Flags: ignoreversion

; Orbit SDK DLLs (not provided by Rhino at runtime)
Source: "src\OrbitConnector.Rhino\bin\Release\net8.0-windows\Orbit.Sdk.dll"; \
        DestDir: "{app}"; Flags: ignoreversion
Source: "src\OrbitConnector.Rhino\bin\Release\net8.0-windows\Orbit.Objects.dll"; \
        DestDir: "{app}"; Flags: ignoreversion
Source: "src\OrbitConnector.Rhino\bin\Release\net8.0-windows\Newtonsoft.Json.dll"; \
        DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{app}\OrbitConnector.Rhino.rhp"; \
          Description: "Register plugin with Rhino"; \
          Flags: shellexec skipifsilent postinstall

[Messages]
FinishedLabel=ORBIT Connector has been installed. Please restart Rhino to activate it.

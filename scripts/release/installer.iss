; Build-Package compiles this source; Test-Package validates the resulting assets.
; ISCC /DPackageRoot="<absolute payload path>" /DArtifactDir="<absolute output>"
;      /DPackageVersion="0.1.0-test.1" installer.iss
; This source does not install or download any runtime.
#ifndef PackageRoot
  #error PackageRoot must point to the validated self-contained payload.
#endif
#ifndef ArtifactDir
  #error ArtifactDir must point to the release artifact directory.
#endif
#ifndef PackageVersion
  #error PackageVersion must be supplied from release-manifest.json.
#endif

[Setup]
AppId={{C0151230-19F6-4CFA-9B24-BBDCF12722E3}
AppName=Corgi Blackboard Test
AppVersion={#PackageVersion}
VersionInfoVersion=0.1.0.0
AppVerName=Corgi Blackboard {#PackageVersion} (Test)
AppPublisher=Corgi Blackboard
DefaultDirName={localappdata}\Programs\CorgiBlackboard-Test
DefaultGroupName=Corgi Blackboard Test
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#ArtifactDir}
OutputBaseFilename=CorgiBlackboard-{#PackageVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\CorgiBlackboard.App.exe
SetupLogging=yes
; Logs live outside {app}. No UninstallDelete/InstallDelete section is used.
; Uninstall removes installed application files and retains user diagnostics.

[Files]
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Corgi Blackboard Test"; Filename: "{app}\CorgiBlackboard.App.exe"
Name: "{group}\Test checklist"; Filename: "{app}\TEST-CHECKLIST.md"
Name: "{group}\Feedback guide"; Filename: "{app}\feedback-guide.md"
Name: "{group}\Release notes"; Filename: "{app}\RELEASE-NOTES.md"

; Deliberately no [Run] section: setup never launches the application.

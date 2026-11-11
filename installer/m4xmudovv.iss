; Inno Setup 6 script: builds a lightweight, self-contained m4xmudovv-Setup.exe.
#define AppName "m4xmudovv"
#define AppVersion "2.1.0"
#define AppExe "m4xmudovv.exe"

[Setup]
AppId={{6B0E5C1A-4D52-4C7B-9C0F-3A8E2D14B7F1}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; First wizard question: install just for me (no admin needed) or for all users (Program Files, asks for admin).
PrivilegesRequiredOverridesAllowed=dialog
; Wizard pages: Welcome -> Install location (Browse...) -> Options -> Ready -> Install -> Finish
DisableWelcomePage=no
DisableDirPage=no
DisableReadyPage=no
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=m4xmudovv-Setup
SetupIconFile=..\src\m4xmudovv\Assets\m4xmudovv.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/fast
SolidCompression=yes
; Classic wizard with a clean black/white palette for a small, predictable installer UI.
WizardStyle=classic
BackColor=clWhite
BackColor2=clWhite
BackColorDirection=lefttoright
WizardImageFile=wizard-image.bmp
WizardSmallImageFile=wizard-small.bmp
WizardImageStretch=no
WizardImageBackColor=clBlack
WizardSmallImageBackColor=clBlack
ShowLanguageDialog=no
AppMutex=Local\m4xmudovv
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the "Start with Windows" entry the app wrote for the current user.
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v m4xmudovv /f"; Flags: runhidden; RunOnceId: "RemoveStartup"

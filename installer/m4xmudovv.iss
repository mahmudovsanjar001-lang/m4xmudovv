; Inno Setup 6 script: builds a lightweight, self-contained m4xmudovv-Setup.exe.
#define AppName "m4xmudovv"
#define AppVersion "3.2.1"
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

[Code]
function InitializeSetup(): Boolean;
var
  UninstallKey: String;
  OldUninstaller: String;
  ResultCode: Integer;
begin
  Result := True;
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6B0E5C1A-4D52-4C7B-9C0F-3A8E2D14B7F1}_is1';
  if RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', OldUninstaller) or
     RegQueryStringValue(HKLM, UninstallKey, 'UninstallString', OldUninstaller) then
  begin
    if MsgBox('An older m4xmudovv version is already installed.' + #13#10 + #13#10 +
              'Choose Yes to remove the old version and continue. Your wallpaper library and settings will be preserved.' + #13#10 +
              'Choose No to keep the old version and cancel this installation.',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      OldUninstaller := RemoveQuotes(OldUninstaller);
      if not Exec(OldUninstaller, '/SILENT /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        MsgBox('The older version could not be removed. Installation was cancelled.', mbError, MB_OK);
        Result := False;
      end;
    end
    else
      Result := False;
  end;
end;

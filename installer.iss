; SPDX-License-Identifier: AGPL-3.0-or-later
; Inno Setup script for TVControlForWin — produces dist\TVControlForWin-<version>-setup.exe
;
; The app is unpackaged and self-contained, so "installing" is really just laying the
; publish output down in Program Files and adding shortcuts + an uninstall entry.
; Build it with:  pwsh -File build.ps1 -Configuration Release -Package

#define AppName "TVControlForWin"
#define AppPublisher "Alex Bolocan"
#define AppUrl "https://github.com/itsAllexB/TVControlForWin"
; What the app was called up to v2.1 — its files are cleared out on upgrade (see below).
#define OldAppName "LGforWin"
#define AppExe "TVControlForWin.exe"

; -DAppVersion=... is passed by build.ps1 so the version lives in one place (the .csproj).
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif

; Where the Release build put its output.
#ifndef SourceDir
  #define SourceDir "bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
#endif

[Setup]
AppId={{8E6F2A41-2C7B-4D19-9F3E-1B7A5C9D4E20}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
; Same AppId as the LGforWin releases, so this upgrades them in place — but into the new
; folder and Start-menu group rather than the ones remembered from the old name. Inno's own
; "use the previous folder" would bring back ...\LGforWin, so InitializeWizard (see [Code])
; does that job instead: it keeps a folder the user chose and only replaces the old default.
; The default stays a plain path because its last part is also the folder name the Browse
; dialog adds to whatever is picked there, and that should always be the app's name.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UsePreviousAppDir=no
UsePreviousGroup=no
DisableProgramGroupPage=yes
LicenseFile=LICENSE
OutputDir=dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; WinUI 3 / Windows App SDK needs 64-bit Windows 10 1809+.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Per-machine when elevated, per-user otherwise — no forced UAC prompt.
PrivilegesRequiredOverridesAllowed=dialog
; A running instance locks the exe; offer to close it instead of failing.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Upgrading from an LGforWin release: remove its program folder and shortcuts, and, where it
; was installed to a folder of the user's choosing (which is kept), its exe and companions.
Type: filesandordirs; Name: "{autopf}\{#OldAppName}"
Type: filesandordirs; Name: "{autoprograms}\{#OldAppName}"
Type: files; Name: "{autodesktop}\{#OldAppName}.lnk"
Type: files; Name: "{app}\{#OldAppName}.*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; No autostart entry here on purpose: a per-machine install runs elevated, so an HKCU Run
; value would land in the installing admin's hive rather than the user's. The app's
; Settings -> "Start with Windows" toggle writes it at runtime, as the right user.

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Where an earlier version is installed, if that's somewhere worth keeping: the folder still
// exists, and it isn't the old default (...\LGforWin), which moves to the new default.
// Empty otherwise. A folder that's gone means the entry is left over from an install that was
// deleted by hand, and proposing its path again would only confuse.
function FolderToKeep(): String;
var
  Key, Previous: String;
begin
  Result := '';
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E6F2A41-2C7B-4D19-9F3E-1B7A5C9D4E20}_is1';
  if not RegQueryStringValue(HKA, Key, 'Inno Setup: App Path', Previous) then
    if not RegQueryStringValue(HKLM, Key, 'Inno Setup: App Path', Previous) then
      if not RegQueryStringValue(HKCU, Key, 'Inno Setup: App Path', Previous) then
        Exit;
  Previous := RemoveBackslashUnlessRoot(Previous);
  if (Previous <> '') and DirExists(Previous)
     and (CompareText(ExtractFileName(Previous), '{#OldAppName}') <> 0) then
    Result := Previous;
end;

// An upgrade goes on top of the copy that's there (this is also what a background update
// relies on), unless a folder was named on the command line.
procedure InitializeWizard();
var
  Keep: String;
begin
  if ExpandConstant('{param:DIR|}') <> '' then Exit;
  Keep := FolderToKeep();
  if Keep <> '' then WizardForm.DirEdit.Text := Keep;
end;

[UninstallDelete]
; Settings live in %LOCALAPPDATA%\TVControlForWin and are deliberately left behind, so
; reinstalling keeps your TVs and their pairing keys.
; The uninstaller removes the files it installed. The folder itself goes only if that left
; it empty: someone may have installed into a folder that holds other things.
Type: dirifempty; Name: "{app}"

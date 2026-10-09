; Copyright (c) 2026 Rausch IT
; SPDX-License-Identifier: MIT

; ===========================================================================
; DisplayProfiles Installer
; Requires: Inno Setup 6.3+ (resolved by build-installer.ps1 from the Tools.InnoSetup NuGet package)
; Build:    Run build-installer.ps1 (publishes the apps, then compiles this file)
; ===========================================================================

#define AppName      "DisplayProfiles"
#define AppPublisher "Rausch IT"
#define AppExeName   "DisplayProfiles.exe"
#ifndef PublishDir
  #define PublishDir "..\build\publish"
#endif
#define IconFile     "..\src\DisplayProfiles.Tray\displayprofiles.ico"

#define FileHandle   FileOpen(AddBackslash(SourcePath) + "..\VERSION")
#define RawVersion   Trim(FileRead(FileHandle))
#expr                FileClose(FileHandle)
#define AppVersion   RawVersion

; Numeric version for Windows file version info (strip pre-release suffix)
#if Pos("-", RawVersion) > 0
  #define NumericVersion Copy(RawVersion, 1, Pos("-", RawVersion) - 1)
#else
  #define NumericVersion RawVersion
#endif

; ===========================================================================
[Setup]
; Stable GUID — must never change between versions
AppId={{7E55166C-196D-4792-8CC0-A8DE8882CBA5}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#NumericVersion}.0
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\DisplayProfiles
DefaultGroupName={#AppName}
OutputDir=..\Releases\{#AppVersion}
OutputBaseFilename=DisplayProfiles-{#AppVersion}-setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; Autostart is a per-user Run entry (HKCU) of the user running setup.
UsedUserAreasWarning=no
WizardStyle=modern
MinVersion=10.0
CloseApplications=force
CloseApplicationsFilter=DisplayProfiles.exe,DisplayProfiles.Cli.exe
RestartApplications=no

; ===========================================================================
[Languages]
; English only, like the app (not localized yet). A single language also skips the language dialog.
Name: "english"; MessagesFile: "compiler:Default.isl"

; ===========================================================================
[Tasks]
Name: "autostart";   Description: "Start DisplayProfiles with Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}";      GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; ===========================================================================
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

; ===========================================================================
[Registry]
; Same value the app writes itself (Autostart.cs) — keep both in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DisplayProfiles"; \
  ValueData: """{app}\{#AppExeName}"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

; ===========================================================================
[Icons]
Name: "{group}\{#AppName}";                       Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";                 Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

; ===========================================================================
[Run]
Filename: "{app}\{#AppExeName}"; \
  Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent

; ===========================================================================
[UninstallRun]
; The tray app has no window to close; stop it so its files can be removed. Profiles in
; %LOCALAPPDATA%\DisplayProfiles are user data and stay.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; Flags: runhidden; RunOnceId: "StopTray"

; ===========================================================================
[Code]

// ---- .NET 8 runtimes (Windows Desktop + ASP.NET Core) ----------------------

const
  DESKTOP_RUNTIME_URL = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  DESKTOP_RUNTIME_EXE = 'windowsdesktop-runtime-8-x64.exe';
  ASPNET_RUNTIME_URL  = 'https://aka.ms/dotnet/8.0/aspnetcore-runtime-win-x64.exe';
  ASPNET_RUNTIME_EXE  = 'aspnetcore-runtime-8-x64.exe';

function HasVersion8(const RootKey: Integer; const Name: String): Boolean;
var
  ValueNames: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey,
       'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + Name,
       ValueNames) then
  begin
    for I := 0 to GetArrayLength(ValueNames) - 1 do
    begin
      if (Length(ValueNames[I]) >= 2) and (Copy(ValueNames[I], 1, 2) = '8.') then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

// The .NET installers register in the 32-bit registry view (WOW6432Node), which a 64-bit setup does not
// see under plain HKLM (verified on the target machine). Check both views.
function IsSharedFx8Installed(const Name: String): Boolean;
begin
  Result := HasVersion8(HKLM32, Name) or HasVersion8(HKLM64, Name);
end;

function OnDownloadProgress(const Url, FileName: string;
  const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax <> 0 then
    Log(Format('Downloading %s: %d of %d bytes', [FileName, Progress, ProgressMax]))
  else
    Log(Format('Downloading %s: %d bytes', [FileName, Progress]));
  Result := True;
end;

// Downloads and runs a runtime installer. Returns '' on success, otherwise an error message.
function InstallRuntime(const DisplayName, Url, ExeName: String; var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Log(DisplayName + ': not found, downloading...');
  try
    DownloadTemporaryFile(Url, ExeName, '', @OnDownloadProgress);
  except
    Result := 'Failed to download ' + DisplayName + '.'#13#10
            + 'Please install it manually from https://dotnet.microsoft.com/download/dotnet/8.0'#13#10
            + 'and run this installer again.';
    Exit;
  end;

  if not Exec(ExpandConstant('{tmp}\') + ExeName, '/install /quiet /norestart', '', SW_SHOW,
              ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Failed to start the ' + DisplayName + ' installer.';
    Exit;
  end;

  case ResultCode of
    0:    Log(DisplayName + ' installed successfully.');
    1641,
    3010: begin
            Log(DisplayName + ' installed, reboot required.');
            NeedsRestart := True;
          end;
  else
    Result := Format('The %s installer exited with code %d.'#13#10
                   + 'Please install it manually and run this installer again.', [DisplayName, ResultCode]);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  if IsSharedFx8Installed('Microsoft.WindowsDesktop.App') then
    Log('.NET 8 Desktop Runtime: already installed.')
  else
  begin
    Result := InstallRuntime('.NET 8 Desktop Runtime', DESKTOP_RUNTIME_URL, DESKTOP_RUNTIME_EXE, NeedsRestart);
    if Result <> '' then Exit;
  end;

  if IsSharedFx8Installed('Microsoft.AspNetCore.App') then
    Log('ASP.NET Core 8 Runtime: already installed.')
  else
    Result := InstallRuntime('ASP.NET Core 8 Runtime', ASPNET_RUNTIME_URL, ASPNET_RUNTIME_EXE, NeedsRestart);
end;

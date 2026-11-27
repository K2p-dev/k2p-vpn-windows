#ifndef MyAppVersion
  #define MyAppVersion "0.3.7-beta"
#endif

[Setup]
AppId={{8F3D2A1B-9C4E-4B7A-9D1E-2C3A4B5D6E7F}
AppName=Как 2 пальца (K2P VPN BETA)
AppVersion={#MyAppVersion}
AppPublisher=K2P Dev
AppPublisherURL=https://lk.proksbro.space
DefaultDirName={autopf}\K2pVpn
DefaultGroupName=Как 2 пальца
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
OutputDir=release_assets
OutputBaseFilename=K2pVpn-Setup-v{#MyAppVersion}
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\K2P-VPN-Windows.exe
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "publish\K2P-VPN-Windows.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\Core\*"; DestDir: "{app}\Core"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Как 2 пальца (K2P VPN)"; Filename: "{app}\K2P-VPN-Windows.exe"
Name: "{autodesktop}\Как 2 пальца (K2P VPN)"; Filename: "{app}\K2P-VPN-Windows.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\K2P-VPN-Windows.exe"; Description: "{cm:LaunchProgram,Как 2 пальца}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM K2P-VPN-Windows.exe /T"; Flags: runhidden; RunOnceId: "KillK2pApp"
Filename: "taskkill.exe"; Parameters: "/F /IM xray.exe /T"; Flags: runhidden; RunOnceId: "KillXray"
Filename: "taskkill.exe"; Parameters: "/F /IM sing-box.exe /T"; Flags: runhidden; RunOnceId: "KillSingBox"
Filename: "taskkill.exe"; Parameters: "/F /IM hev-socks5-tunnel-win64.exe /T"; Flags: runhidden; RunOnceId: "KillHevTun"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM K2P-VPN-Windows.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM xray.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM sing-box.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM hev-socks5-tunnel-win64.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

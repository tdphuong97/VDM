; VDM installer (Inno Setup 6). Built by build.ps1 from dist\app.
; Installs per user (no admin prompt), so yt-dlp can update itself inside the app folder.
#define AppVersion GetEnv("VDM_VERSION")
#if AppVersion == ""
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6E2B9B0E-3C4A-4F0B-9A3E-5D1C7B2A8F41}
AppName=VDM
AppVersion={#AppVersion}
AppVerName=VDM {#AppVersion}
AppPublisher=VDM
AppComments=Trình tải xuống miễn phí, không thương mại
DefaultDirName={localappdata}\Programs\VDM
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=VDM-Setup-{#AppVersion}
SetupIconFile=..\src\VDM.App\Assets\vdm.ico
UninstallDisplayIcon={app}\VDM.exe
UninstallDisplayName=VDM
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=2
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "vi"; MessagesFile: "Vietnamese.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài màn hình"; GroupDescription: "Thêm:"
Name: "startup"; Description: "Chạy VDM cùng Windows (để luôn bắt được link từ trình duyệt)"; GroupDescription: "Thêm:"

[InstallDelete]
; Up to 1.3.1 the extension icons sat in a subfolder, the only thing Chrome's "Load unpacked" picker
; showed inside the extension folder, so people chose it and got "manifest missing". Now at the root.
Type: filesandordirs; Name: "{app}\extension\chromium\icons"
Type: filesandordirs; Name: "{app}\extension\firefox\icons"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\VDM"; Filename: "{app}\VDM.exe"
Name: "{autodesktop}\VDM"; Filename: "{app}\VDM.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VDM"; ValueData: """{app}\VDM.exe"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\VDM.exe"; Description: "Mở VDM ngay"; Flags: nowait postinstall skipifsilent
; An update started from inside VDM runs silently: open VDM again when it is done.
Filename: "{app}\VDM.exe"; Parameters: "--updated"; Flags: nowait; Check: IsUpdate

[Code]
// VDM starts its own update with /UPDATE=1 and then closes.
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;

// Give VDM up to 30 seconds to save the download list and close before files are replaced.
function InitializeSetup: Boolean;
var
  i: Integer;
begin
  if IsUpdate then
    for i := 1 to 60 do
    begin
      if not CheckForMutexes('Local\VDM-' + GetUserNameString) then Break;
      Sleep(500);
    end;
  Result := True;
end;

[UninstallDelete]
; yt-dlp updates itself in place and may leave its old copy behind.
Type: filesandordirs; Name: "{app}\tools"

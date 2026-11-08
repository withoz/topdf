#ifndef PackageDir
 #error PackageDir must point to a prepared, private-data-free publish directory
#endif
#ifndef OutputDir
 #define OutputDir "..\artifacts\installer"
#endif
#ifndef Bootstrapper
 #error Bootstrapper must point to the signed Microsoft WebView2 bootstrapper
#endif
#ifndef AppVersion
 #define AppVersion "0.3.5"
#endif
[Setup]
AppId={{7BB8D838-71D7-4CA3-A7A4-DB997AFCACB9}
AppName=TOPDF
AppVersion={#AppVersion}
AppPublisher=withoz
AppPublisherURL=https://github.com/withoz/topdf
DefaultDirName={localappdata}\Programs\TOPDF
DefaultGroupName=TOPDF
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
#ifdef ReleaseSigned
OutputBaseFilename=TOPDF-Setup-{#AppVersion}-win-x64
SignTool=topdf
SignedUninstaller=yes
#else
OutputBaseFilename=TOPDF-Setup-{#AppVersion}-win-x64-preview
#endif
SetupIconFile=..\src\Topdf\Assets\topdf.ico
UninstallDisplayIcon={app}\Topdf.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "바탕 화면 바로가기 만들기"; Flags: unchecked
Name: "associations"; Description: "연결 프로그램 목록에 TOPDF 추가 (기본 앱 유지)"; Flags: unchecked
[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Bootstrapper}"; DestDir: "{tmp}"; Flags: deleteafterinstall
[Icons]
Name: "{group}\TOPDF"; Filename: "{app}\Topdf.exe"
Name: "{autodesktop}\TOPDF"; Filename: "{app}\Topdf.exe"; Tasks: desktopicon
[Registry]
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "TOPDF"; Flags: uninsdeletekey; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe\shell\open\command"; ValueType: string; ValueData: """{app}\Topdf.exe"" ""%1"""; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\TOPDF.Document"; ValueType: string; ValueData: "TOPDF 문서 미리보기"; Flags: uninsdeletekey; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\TOPDF.Document\DefaultIcon"; ValueType: string; ValueData: """{app}\Topdf.exe"",0"; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\TOPDF.Document\shell\open\command"; ValueType: string; ValueData: """{app}\Topdf.exe"" ""%1"""; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\.hwp\OpenWithProgids"; ValueType: string; ValueName: "TOPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe\SupportedTypes"; ValueType: string; ValueName: ".hwp"; ValueData: ""; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\.hwpx\OpenWithProgids"; ValueType: string; ValueName: "TOPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe\SupportedTypes"; ValueType: string; ValueName: ".hwpx"; ValueData: ""; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\.docx\OpenWithProgids"; ValueType: string; ValueName: "TOPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe\SupportedTypes"; ValueType: string; ValueName: ".docx"; ValueData: ""; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\.xls\OpenWithProgids"; ValueType: string; ValueName: "TOPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe\SupportedTypes"; ValueType: string; ValueName: ".xls"; ValueData: ""; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\.xlsx\OpenWithProgids"; ValueType: string; ValueName: "TOPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe\SupportedTypes"; ValueType: string; ValueName: ".xlsx"; ValueData: ""; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\.dwg\OpenWithProgids"; ValueType: string; ValueName: "TOPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe\SupportedTypes"; ValueType: string; ValueName: ".dwg"; ValueData: ""; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\.dxf\OpenWithProgids"; ValueType: string; ValueName: "TOPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe\SupportedTypes"; ValueType: string; ValueName: ".dxf"; ValueData: ""; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "TOPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associations
Root: HKCU; Subkey: "Software\Classes\Applications\Topdf.exe\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: associations
[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "PDF 미리보기 구성 요소 준비 중 (인터넷 필요)…"; Check: NeedsWebView2; Flags: waituntilterminated
Filename: "{app}\Topdf.exe"; Description: "TOPDF 실행"; Flags: nowait postinstall skipifsilent
[Code]
function NeedsWebView2: Boolean;
var Version: String;
begin
  Result := not ((RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM32, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')));
end;
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and NeedsWebView2 then
    Result := MsgBox('PDF 미리보기에 WebView2가 필요합니다. 설치 중 Microsoft에서 다운로드합니다. 인터넷에 연결해 주세요.', mbInformation, MB_OKCANCEL) = IDOK;
end;
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssDone) and NeedsWebView2 then
    MsgBox('WebView2 설치가 완료되지 않았습니다. 인터넷 연결을 확인하고 Microsoft WebView2 Runtime 설치 후 TOPDF를 실행해 주세요.', mbError, MB_OK);
end;

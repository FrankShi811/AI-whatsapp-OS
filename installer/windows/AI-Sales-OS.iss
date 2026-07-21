#ifndef MyAppVersion
  #define MyAppVersion "1.16.1"
#endif
#ifndef VelopackSetupPath
  #error VelopackSetupPath is required
#endif
#ifndef ChineseMessagesFile
  #error ChineseMessagesFile is required
#endif

#define MyAppName "AI Sales OS"
#define MyAppExeName "AISalesOS.exe"
#define ProjectRoot AddBackslash(SourcePath) + "..\..\"

[Setup]
AppId={{7E66411B-545A-4EF0-9E48-28678E65726B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=AI Sales OS
AppComments=AI 驱动的 WhatsApp 商机管理与销售自动化系统
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=AI Sales OS 中文安装程序
DefaultDirName={code:GetRecommendedInstallDir}
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
DisableFinishedPage=no
PrivilegesRequired=lowest
OutputDir={#ProjectRoot}dist\installers
OutputBaseFilename=AI Sales OS Setup
SetupIconFile={#ProjectRoot}desktop\WAFlow.Desktop\Assets\AI-Sales-OS.ico
Compression=none
SolidCompression=no
WizardStyle=modern dynamic
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
ChangesAssociations=no
ChangesEnvironment=no
Uninstallable=no
CreateUninstallRegKey=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#ChineseMessagesFile}"

[Files]
Source: "{#VelopackSetupPath}"; DestDir: "{tmp}"; DestName: "AI-Sales-OS-Velopack-Setup.exe"; Flags: deleteafterinstall

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 AI Sales OS"; Flags: nowait postinstall skipifsilent

[CustomMessages]
chinesesimplified.InstallingNative=正在安装 AI Sales OS 原生 Windows 客户端和自动更新组件，请稍候…
chinesesimplified.InstallFailed=AI Sales OS 安装失败（错误代码：%1）。请保留安装包并联系开发者。

[Code]
var
  VelopackInstalled: Boolean;

function GetRecommendedInstallDir(Param: String): String;
var
  DriveCode: Integer;
  CandidateRoot: String;
  WindowsDrive: String;
begin
  WindowsDrive := UpperCase(Copy(ExpandConstant('{win}'), 1, 2));
  for DriveCode := Ord('D') to Ord('Z') do
  begin
    CandidateRoot := Chr(DriveCode) + ':\';
    if (UpperCase(Copy(CandidateRoot, 1, 2)) <> WindowsDrive) and DirExists(CandidateRoot) then
    begin
      Result := CandidateRoot + '{#MyAppName}';
      Exit;
    end;
  end;
  Result := ExpandConstant('{localappdata}\Programs\{#MyAppName}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  SetupPath: String;
  Parameters: String;
begin
  if (CurStep = ssPostInstall) and (not VelopackInstalled) then
  begin
    WizardForm.StatusLabel.Caption := ExpandConstant('{cm:InstallingNative}');
    SetupPath := ExpandConstant('{tmp}\AI-Sales-OS-Velopack-Setup.exe');
    Parameters := '--silent --installto "' + ExpandConstant('{app}') + '"';
    if (not Exec(SetupPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
      RaiseException(FmtMessage(ExpandConstant('{cm:InstallFailed}'), [IntToStr(ResultCode)]));
    VelopackInstalled := True;
  end;
end;

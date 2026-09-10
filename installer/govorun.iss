; Govorun installer — Inno Setup 6 script.
; Prerequisite: run tools\publish.ps1 first (creates publish\ with the app
; and models\ with the ONNX weights).

#define AppName "Govorun"
#define AppVersion "0.1.1"
#define AppExe "Govorun.App.exe"

[Setup]
AppId={{7A2E7C41-2F0B-4F79-9C39-6C3A4C0B9D11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Govorun
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=GovorunSetup-{#AppVersion}
; int8-веса почти несжимаемы — fast даёт тот же размер на порядок быстрее max.
Compression=lzma2/fast
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
SetupIconFile=..\src\Govorun.App\govorun.ico
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Запускать Govorun при входе в Windows"; GroupDescription: "Дополнительно:"
Name: "blocknet"; Description: "Запретить Govorun доступ в сеть (правило брандмауэра, требует прав администратора)"; GroupDescription: "Конфиденциальность:"; Flags: unchecked

[Files]
; Self-contained publish output (app + .NET runtime + onnxruntime).
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; App-local VC++ runtime: onnxruntime.dll не загрузится на машинах без vc_redist.
Source: "..\assets\vcruntime\*.dll"; DestDir: "{app}"; Flags: ignoreversion
; Parakeet TDT 0.6B V3 weights (~670 MB int8).
Source: "..\models\nemo128.onnx"; DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\models\vocab.txt"; DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\models\config.json"; DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\models\encoder-model.int8.onnx"; DestDir: "{app}\models"; Flags: ignoreversion
Source: "..\models\decoder_joint-model.int8.onnx"; DestDir: "{app}\models"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "Govorun"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[UninstallDelete]
; "Установил и забыл": подчищаем настройки и логи при удалении.
Type: filesandordirs; Name: "{userappdata}\Govorun"
; Пустые каталоги сателлитных ресурсов (ru и т.п.) и вся папка приложения.
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""Govorun block outbound"" dir=out action=block program=""{app}\{#AppExe}"" enable=yes"; \
    Flags: runhidden shellexec; Verb: runas; Tasks: blocknet
; Без skipifsilent: тихое обновление поверх (например, при переустановке живого
; приложения) не должно оставлять пользователя без работающего Govorun в трее.
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName}"; Flags: nowait postinstall shellexec

[Code]
// Закрыть работающий Govorun перед установкой поверх, чтобы файлы не были заблокированы.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;

[UninstallRun]
; Первым шагом деинсталляции закрываем приложение — иначе exe и лог остаются заблокированными.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""Govorun block outbound"""; \
    Flags: runhidden shellexec; Verb: runas; RunOnceId: "RemoveFirewallRule"; Tasks: blocknet

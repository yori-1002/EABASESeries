; ============================================================
; EA_CostManager インストーラースクリプト
; Inno Setup 6.x 用
;
; 【v1.0.0 リリース版】Beta卒業
;
; 使い方：
;   1. dotnet publish で単一exeを生成
;      cd "D:\00_MyFile\00_Developer\04_Earth Analyzer Business Administration System\01_EABASE Series\01_EA_CostManager\publish"
;      dotnet clean -c Release
;      dotnet publish -c Release -r win-x64 --self-contained true `
;        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
;        -p:DebugType=embedded -o .\publish
;   2. このスクリプトを Inno Setup Compiler で開いてビルド
;   3. 生成された EA_CostManager_setup_v1.0.0.exe を
;      \\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\02_Updates\01_Cost Manager\
;      に配置
;   4. 同フォルダの 01_version.txt に "1.0.0" と書く（自動アップデート発火）
; ============================================================

; ▼ 修正：表示名は CostManager（要件：ソフト名称はCostManager）
#define MyAppName      "CostManager"
#define MyAppVersion   "1.0.3"
#define MyAppPublisher "EABASE Series"

; ▼ 修正：exe名は CostManager.exe（csproj の AssemblyName と一致）
#define MyAppExeName   "CostManager.exe"

; ▼ 修正：publish フォルダの正確なパス（dotnet publish -o .\publish の出力先）
;   ※ ローカル開発フォルダパス。フォルダ整理時はここも要修正
#define MySourceDir    "D:\00_MyFile\00_Developer\04_Earth Analyzer Business Administration System\01_EABASE Series\01_EA_CostManager\publish"

[Setup]
; ▼ アプリ識別子（絶対変更禁止）
;   AppIdが変わるとInno Setupが「別アプリ」と認識して、
;   既存版（Beta含む）がアンインストールされずに両方残る原因になる
AppId={{EA-COST-MANAGER-2024-EABASE}}

AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=

; ▼ 修正：インストール先を EABASE Series\EA_CostManager 階層に変更
;   理由：将来 EA_DailyReport 等の他ソフトと整理するため、
;        Program Files 配下を「EABASE Series」フォルダで統一する。
;
;   将来の構造例：
;     C:\Program Files\EABASE Series\
;     ├── EA_CostManager\          ← 本ソフト
;     │   └── CostManager.exe
;     ├── EA_DailyReport\          ← 将来追加予定
;     └── ...
;
;   注意：Beta版（v0.9.x）は C:\Program Files\EA_CostManager\ にあるが、
;        AppIdが同じなので [Code] セクションの自動アンインストール処理が
;        旧版を削除してから新パス（EABASE Series\EA_CostManager）にインストールする
DefaultDirName={autopf}\EABASE Series\EA_CostManager

DefaultGroupName={#MyAppName}
OutputDir=installer_output
OutputBaseFilename=EA_CostManager_setup_v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; ▼ 管理者権限でインストール（Program Filesへの書き込みに必要）
PrivilegesRequired=admin

; ▼ Windows Vista以降が対象
MinVersion=6.1

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
; ▼ 修正：両方ともデフォルトONで作成（Inno Setup の Tasks はデフォルトON動作）
;   `Flags: checked` は [Tasks] では無効構文のため削除
;   Flags を指定しない＝デフォルトでチェックON
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; GroupDescription: "追加タスク:"
Name: "startmenuicon"; Description: "スタートメニューにショートカットを作成する"; GroupDescription: "追加タスク:"

[Files]
; メインの実行ファイル
Source: "{#MySourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; ▼ 修正：ショートカットのカーソル時ツールチップを「CostManager Ver1.0.0」表記に
;   Comment 設定でショートカットのプロパティ「コメント」欄に説明文を追加
;   Windowsエクスプローラがこれをカーソルホバー時のツールチップに使用する

; デスクトップショートカット
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName} Ver{#MyAppVersion}"; Tasks: desktopicon

; スタートメニューショートカット
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName} Ver{#MyAppVersion}"; Tasks: startmenuicon
Name: "{group}\アンインストール"; Filename: "{uninstallexe}"

[Registry]
; ▼▼▼ インストールフォルダをレジストリに書き込む ▼▼▼
;   UserSettingsManager.cs がここを読んでJSONの保存先を決定する
;   既存コードとの互換性のため、レジストリサブキー名は EA_CostManager のまま維持
;   （コード側を変更すると影響範囲が大きいため、内部キー名のみ旧運用を維持）
;
; ▼ 修正：Flagsを安全な構文に書き直し
;   uninsdeletekey はサブキー全体を削除するFlag
;   InstallDir 行に1回だけ書けば、Version 等の他の値も同時に消える
;   （`SOFTWARE\EA_CostManager` 配下が全削除される）
Root: "HKLM"; Subkey: "SOFTWARE\EA_CostManager"; ValueType: "string"; ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey
Root: "HKLM"; Subkey: "SOFTWARE\EA_CostManager"; ValueType: "string"; ValueName: "Version"; ValueData: "{#MyAppVersion}"

[Run]
; インストール完了後にアプリを起動するオプション
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} を起動する"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; アンインストール時に設定ファイルも削除（任意）
; コメントを外すとアンインストール時にJSONも削除される
; Type: files; Name: "{app}\user_settings.json"

[Code]
// ============================================================
// ▼▼▼ 既存バージョンの確認・アップグレード処理 ▼▼▼
//   Inno Setup は AppId が同じインストーラーを「同じアプリ」と認識する。
//   インストール開始時に既存版を検出してサイレントアンインストールしてから
//   新バージョンをインストールすることで、ファイルの上書き・古い設定の競合を防ぐ。
//
//   既存ユーザー（Beta含む）も AppId が同じなので、この機構で正しく上書きされる。
// ============================================================

function GetUninstallString(): String;
var
  sUnInstPath: String;
  sUnInstallString: String;
begin
  // ▼ 修正：ExpandConstant 内の AppId を {{ でエスケープ
  //   {EA-COST-MANAGER-2024-EABASE} のままだと
  //   ExpandConstant が「EA-COST-MANAGER-2024-EABASE」という定数名として解釈してエラー：
  //   「Runtime error: Unknown constant "EA-COST-MANAGER-2024-EABASE"」
  //   {{ にエスケープすることでリテラルの { として展開される
  sUnInstPath := ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{{EA-COST-MANAGER-2024-EABASE}_is1');
  sUnInstallString := '';
  if not RegQueryStringValue(HKLM, sUnInstPath, 'UninstallString', sUnInstallString) then
    RegQueryStringValue(HKCU, sUnInstPath, 'UninstallString', sUnInstallString);
  Result := sUnInstallString;
end;

function IsUpgrade(): Boolean;
begin
  Result := (GetUninstallString() <> '');
end;

// 既存バージョンがある場合はアンインストールしてから新規インストール
function UnInstallOldVersion(): Integer;
var
  sUnInstallString: String;
  iResultCode: Integer;
begin
  Result := 0;
  sUnInstallString := GetUninstallString();
  if sUnInstallString <> '' then begin
    sUnInstallString := RemoveQuotes(sUnInstallString);
    if Exec(sUnInstallString, '/SILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, iResultCode) then
      Result := 3
    else
      Result := 2;
  end else
    Result := 1;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep=ssInstall) then
  begin
    if (IsUpgrade()) then
      UnInstallOldVersion();
  end;
end;

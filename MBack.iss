; --- MBack 完全版 インストーラー作成スクリプト ---

#define MyAppName "MBack"
#define MyAppVersion "2.0.1"
#define MyAppPublisher "My Company"
#define MyAppExeName "MBack.Config.exe"
#define MyServiceExeName "MBack.Service.exe"
#define MyServiceName "MBackService"
#define MyIconFile "app.ico"

[Setup]
; アプリケーション情報
AppId={{A8F93641-7D22-4E90-95D9-3C5798725964}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}

; ★出力先: 実行しているユーザーのデスクトップに保存
OutputDir={#GetEnv('USERPROFILE')}\Desktop
OutputBaseFilename=MBackSetup

; ★圧縮設定
Compression=lzma
SolidCompression=yes

; ★アーキテクチャ設定
; SelfContained win-x64 ビルドのため、64bit環境専用であることを明示する
; 32bit環境へのインストールをブロックして「動かない」トラブルを未然に防ぐ
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

; ★管理者権限を要求（サービス登録のため必須）
PrivilegesRequired=admin

; ★インストーラー自体のアイコン設定
SetupIconFile=C:\MBackRelease\{#MyIconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Files]
; 1. 設定ファイル以外をすべてコピー（上書きOK）
;    ★追加: .pdbファイル（デバッグ情報）は配布不要なので除外する
Source: "C:\MBackRelease\*"; DestDir: "{app}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; \
  Excludes: "appsettings.json,*.pdb"

; 2. 設定ファイルだけは「ファイルがない時だけ」コピーする（上書き禁止！）
;    ユーザーが設定した内容をアップデート時に消さないための重要な設定
Source: "C:\MBackRelease\appsettings.json"; DestDir: "{app}"; \
  Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
; スタートメニューにショートカット作成
Name: "{group}\{#MyAppName} 設定"; \
  Filename: "{app}\{#MyAppExeName}"; \
  IconFilename: "{app}\{#MyIconFile}"

; デスクトップにショートカット作成（タスクで選択した場合のみ）
Name: "{autodesktop}\{#MyAppName} 設定"; \
  Filename: "{app}\{#MyAppExeName}"; \
  IconFilename: "{app}\{#MyIconFile}"; \
  Tasks: desktopicon

[Tasks]
Name: "desktopicon"; \
  Description: "デスクトップにアイコンを作成する"; \
  GroupDescription: "追加タスク"; \
  Flags: unchecked

[Run]
; --- インストール後の処理 ---
; 1. サービスを登録する
Filename: "sc.exe"; \
  Parameters: "create {#MyServiceName} binPath= ""{app}\{#MyServiceExeName}"" start= auto"; \
  Flags: runhidden

; 2. サービスの説明文を設定する
Filename: "sc.exe"; \
  Parameters: "description {#MyServiceName} ""MBack 自動バックアップサービス"""; \
  Flags: runhidden

; 3. サービスを開始する
Filename: "sc.exe"; \
  Parameters: "start {#MyServiceName}"; \
  Flags: runhidden

; 4. 設定ツールを起動するか聞く
Filename: "{app}\{#MyAppExeName}"; \
  Description: "MBack 設定ツールを起動する"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; --- アンインストール前の処理 ---
; 1. サービスを停止する
Filename: "sc.exe"; \
  Parameters: "stop {#MyServiceName}"; \
  Flags: runhidden; \
  RunOnceId: "StopService"

; 2. サービスを削除する
Filename: "sc.exe"; \
  Parameters: "delete {#MyServiceName}"; \
  Flags: runhidden; \
  RunOnceId: "DeleteService"

; 3. 念のため少し待つ（競合対策済み）
Filename: "timeout"; \
  Parameters: "/t 2"; \
  Flags: runhidden; \
  RunOnceId: "WaitTimeout"

[UninstallDelete]
; --- アンインストール時のゴミ掃除 ---
; 設定ファイルもきれいに消す
Type: files; Name: "{app}\appsettings.json"
Type: files; Name: "{app}\backup.trigger"
; フォルダごと削除（空の場合のみ）
Type: dirifempty; Name: "{app}"

; =====================================================================
; ★ここから下はプログラムコード（上書きインストール対応用）
; =====================================================================
[Code]

// インストール作業が始まる直前に呼ばれる処理
// 目的: 既存のサービスが動いているとファイルが上書きできないため、先に停止させる
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // サービス停止コマンド (sc stop MBackService) を実行する
  // ※サービスが存在しない場合のエラーは無視して続行させる
  Exec('sc.exe', 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 停止処理が完了するまで少し待つ（1秒）
  Sleep(1000);

  // 正常に処理を続行する
  Result := '';
end;

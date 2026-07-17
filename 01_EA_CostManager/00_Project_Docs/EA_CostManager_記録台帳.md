# EA_CostManager 記録台帳

> 旧名称: `EA_CostManager_プロジェクト全体まとめ.md`。2026-07-15 の yori 指示により、本MDの名称・役割を「記録台帳」として扱う。

## ★ ドキュメント運用ルール（yori 指示・2026-07-15／最優先で遵守）

| # | ルール | 内容 |
| --- | --- | --- |
| 1 | **本 md への随時記載** | プログラムの修正・追加・削除を行ったら、**その都度**本 md に反映する。①「12. 更新ログ」に日付・対象ファイル・変更内容を追記 ②該当セクション（3. アーキテクチャ／6. 主要機能／7. DBテーブル 等）の記述・行数も同時に最新化する。作業完了＝md 反映完了とする。 |
| 2 | **設計書・要件定義書の定期更新** | **基本設計書・要件定義書は定期的に、変更点・追加分を織り込んだ「最新版」として書き換える**。差分メモではなく完全版として出力し、バージョンを上げて変更履歴テーブルに記録する（書式は EATAP フォーマット準拠）。出力先は **`01_EA_CostManager/00_Project_Docs/`** に一本化（2026-07-15 に `EA_CostManager/doc/` から移動・改称）。 |
| 3 | **「続きを進める」＝ md ＋設計書2点を読む** | ユーザーが**「続きを進める」**と言ってコーディングを開始する場合、**毎回まず以下3点を読む**こと。省略不可。①本 md（最初から。更新ログ・要確認点で現況把握）②`00_Project_Docs/EA_CostManager_基本設計書_v*.docx`（最新版）③`00_Project_Docs/EA_CostManager_要件定義書_v*.docx`（最新版）。3点を読んで現況と仕様を把握してから着手する。**docx はバイナリのため直接読めない**。zip 展開して `word/document.xml` からテキスト抽出する（本文＋表を Markdown 風に落とす手順は 12章 2026-07-15 のログ参照）。 |
| 4 | **正確性重視** | 断定と推測を必ず区別して書く。行数・バージョン・パッケージ等の数値は実ファイルで確認した確定値のみ記載する。 |

> 上記は本プロジェクトにおける恒久ルール。以降のセクションを更新する際も必ずこのルールに従う。

---

> **本記録台帳の位置づけ**
> `00_Project_Docs/` 内の基本設計書 v1.2.1・要件定義書 v1.2.1 と、実際のソースコード（`EA_CostManager/` 配下）を突き合わせて整理した俯瞰ドキュメント。
> チャット切替時の状況把握・新規参加時のキャッチアップを目的とする。
> 作成: ジェイ（正確性重視モード）／突き合わせ基準日: 2026-07-14
>
> **注記（正確性について）**
> 本まとめは「設計書2点」＋「ソースのファイル構成・行数・命名・csproj」を根拠に作成している。
> 各ファイルの内部ロジックまで全行を精査したわけではないため、ファイル名・設計書から確度高く言える役割は断定し、
> それ以外は「推測」と明記する。数値（行数・バージョン・パッケージ）は実ファイルで確認済みの確定値。

---

## 0. 一目でわかる現況サマリ

| 項目 | 状態 | 根拠 |
| --- | --- | --- |
| 製品名 | EA_CostManager（EA 原価集計管理システム） | 設計書表紙 |
| 種別 | 社内業務システム（Windowsデスクトップ） | 設計書 1.1 |
| 技術 | C# / WPF / .NET 10（self-contained 単一exe配布） | csproj・設計書 1.2 |
| 設計書バージョン | **v1.2.1**（2026/07/15・作成者: 仲 良智）。v1.2.0 の単価キー記述の誤りを訂正した版（6.4 参照）。**※ 本体は v1.3.1 のため設計書が遅れている**（10章 #10） | docx 表紙 |
| コミット済み最新 | **v1.3.1**（コミット `bdffa52`・develop） | git log |
| csproj バージョン | **v1.3.1**（`<Version>1.3.1`） | csproj 34–36行 |
| インストーラ版数 | **v1.3.1**（`EA_CostManager_setup.iss` の `MyAppVersion`） | .iss 29行 |
| v1.3.1 の実装状態 | **develop にコミット済み・リリースビルド＋インストーラ作成済み**（`installer_output/EA_CostManager_setup_v1.3.1.exe`）。**main へは未反映**・**画面での動作確認が未実施** | git log / publish・installer_output |

> ⚠️ **リリース番号の経緯（2026-07-15）**: 前回リリースは v1.1.0。開発中に付番した **v1.2.1 / v1.2.2 は develop 内のみで一度もリリースしていない**。
> 工数表という新機能の追加のためマイナーを上げ、**v1.3.0** としてリリース版を確定（その後の折りたたみ修正で v1.3.1）。
>
> ⚠️ **バージョン表記は2箇所ある**: `csproj` の `<Version>`（画面左下・バナー・publish の `01_version.txt` はすべてここから自動連動）と、
> `EA_CostManager_setup.iss` の `MyAppVersion`（**自動連動しない**）。実際に .iss が v1.1.0 のまま取り残されており、
> 中身は新版なのにインストーラー名・レジストリ・「プログラムと機能」の表示だけ旧版になる状態だった（2026-07-15 に修正）。**片方だけ変えないこと**。

---

## 1. システム概要

| 項目 | 内容 |
| --- | --- |
| 目的 | 日報データを Excel から読み込み、**現場・月度・作業者別の原価を自動集計**する社内業務システム |
| 共有方式 | **NAS 上の SQLite DB を共有**し、複数PCから同時利用 |
| 対象環境 | Windows 10/11 |
| 配布形態 | Inno Setup 製 `.exe` インストーラ（.NET 10 ランタイム同梱の self-contained） |

### 1.1 技術スタック（実測値）

| 項目 | 内容 | 備考 |
| --- | --- | --- |
| 言語・FW | C# / WPF / .NET 10（`net10.0-windows`） | csproj |
| MVVM基盤 | **CommunityToolkit.Mvvm 8.3.2** | csproj（※設計書に記載なし） |
| DB | SQLite 3（Microsoft.Data.Sqlite **8.0.0** + Dapper **2.1.35**） | csproj |
| Excel入出力 | **ClosedXML 0.102.3** | csproj |
| 配布 | Inno Setup（`EA_CostManager_setup.iss`） | ルート |
| exe化オプション | PublishSingleFile / IncludeNativeLibrariesForSelfExtract / EnableCompressionInSingleFile | csproj 74–76行 |

---

## 2. システム方式設計（EATAP 6項目）

### 2.1 ハードウェア構成
| 項目 | 内容 |
| --- | --- |
| クライアントPC | Windows 10/11 業務PC。複数台が並列実行 |
| NASサーバ | NAS7E6AA6（共有ファイルサーバ）。DB・バックアップ・アップデート・お知らせを集約 |
| ネットワーク | 社内LAN。SMB でNASアクセス |
| ディスプレイ | 推奨 1920x1080 以上・高DPI（150〜200%）対応 |

### 2.2 ソフトウェア構成
| 項目 | 内容 |
| --- | --- |
| 言語・FW | C# / WPF / .NET 10（self-contained） |
| DBエンジン | SQLite 3 系（Microsoft.Data.Sqlite + Dapper） |
| 主要ライブラリ | Dapper（軽量ORM）・ClosedXML（Excel）・Microsoft.Data.Sqlite・CommunityToolkit.Mvvm |
| インストーラ | Inno Setup（AppId で既存版自動アンインストール） |
| テーマ | 実行時切替可（cyan/blue/green/purple/gray/pink の6テーマ） |
| フォントサイズ | 実行時切替可（small/medium/large） |

### 2.3 連携方式
| 項目 | 内容 |
| --- | --- |
| 通信 | SMB（NASファイルアクセス）／SQLiteファイルロック |
| プロトコル・ポート | SMB / TCP 445 |
| DB接続方式 | **ローカルDB → NAS DB の二段構成**。ローカルDBにNASパスを保存し起動時にNASへ切替 |
| バージョン共有 | NAS の `01_version.txt` を参照し新版検出時にバナー通知 |

### 2.4 データフロー
| 項目 | 内容 |
| --- | --- |
| 業務データ保存先 | **NAS DB**（複数PC共有） |
| ユーザー設定保存先 | `%LOCALAPPDATA%`（PC個別） |
| バックアップ | 起動毎にNASへ自動コピー。1ヶ月以上前は自動削除 |
| ローカルDB | NAS切断時の最小起動用にスキーマ保持（フォールバック） |
| 同期タイミング | 起動時にローカル→NASマイグレーション、終了時にセッション情報をNAS DBへ更新 |
| 設定JSON書込 | `FileStream + Flush(true)` でOSバッファまで強制書込（v1.0.3 強化） |

### 2.5 障害時の挙動
| 事象 | 挙動 |
| --- | --- |
| NAS切断 | NAS DBをスキップしローカルDBのみで起動継続 |
| NAS DB ヘッダー破損 | 起動時16バイト検証で検知 → 復旧ダイアログ → バックアップ復元 → 再起動 |
| 設定JSON破損 | `.broken_日時` に退避 + Debug出力 → デフォルト値で再構築 |
| 再起動時DB競合 | `Application.Exit` 内で `Process.Start` → 現プロセス終了後に新プロセス起動（競合が原理的に発生しない） |
| バックアップなし | エラーダイアログでパス案内 → 異常終了 |
| ローカルDB破損 | `initialize_database()` で再構築 |
| セッション残留 | 起動時に5分以上更新のない他PCセッションを自動 inactive 化 |
| 未処理例外 | `App.xaml.cs` のハンドラで捕捉 → NAS DB の `error_logs` へ記録 |

### 2.6 配布形態
| 項目 | 内容 |
| --- | --- |
| 配布方式 | Inno Setup 生成の `.exe` インストーラ（自己完結型） |
| 依存 | .NET 10 ランタイムはexe同梱。別途インストール不要 |
| インストーラ内訳 | `CostManager.exe` + `icon_fix.ico`（v1.0.3 でアイコン同梱を修正） |
| インストール先 | `C:\Program Files (x86)\EABASE\01_CostManager`（既定） |
| 自動アップデート | 起動時にNASの `01_version.txt` を確認し新版をバナー通知＋DL |

---

## 3. アーキテクチャ（実ソース構成）

MVVM構成。`.cs` 実ソース合計 **約22,094行**（obj/bin除く）。以下は実在ファイルと行数（確定値）＋役割。
役割のうち★は設計書で明示、無印はファイル名からの確度の高い推定、（推測）は中身未精査の推定。

### 3.1 エントリ・基盤
| ファイル | 行 | 役割 |
| --- | --- | --- |
| `App.xaml.cs` | 725 | ★起動フロー中核（例外ハンドラ・DB初期化・各Migration・NAS切替・破損検知・復旧ダイアログ・バックアップ・バージョンチェック） |
| `AppVersionInfo.cs` | 61 | ★バージョン情報・NASパス定数 |
| `App_restart_helper.cs` | 77 | ★自動再起動（`Application.Exit` 内 `Process.Start` 方式） |
| `UserSettingsManager.cs` | 259 | ★ローカル設定JSON読み書き（堅牢化＋破損退避） |
| `ThemeManager.cs` | 140 | ★カラーテーマ適用（DynamicResource即時反映） |
| `UserSession.cs` | 74 | ★ログインユーザー情報（静的） |
| `AssemblyInfo.cs` | 10 | アセンブリ属性 |

### 3.2 Data（DB管理・マイグレーション）
| ファイル | 行 | 役割 |
| --- | --- | --- |
| `Data/database_manager.cs` | 811 | ★DB接続・スキーマ作成・バックアップ・破損検証・復元（`has_active_wal`/`restore_from_file`） |
| `Data/V2Migration.cs` | 296 | スキーマ移行（V2）。**ローカルDBにしか走らない**（NAS DB には別途対応が必要） |
| `Data/WorkloadMigration.cs` | 206 | ★工数表6テーブルの冪等作成（**v1.2.0 新規**／v1.3.0 で `workload_subgroup_links` 追加＋旧構造からの一度きりの自動変換 `backfill_subgroup_links`） |
| `Data/ViewStateMigration.cs` | 105 | ★折りたたみ状態2テーブルの冪等作成（**v1.3.0 新規**）。`month_collapse_states`（DDL欠落の是正）/ `workload_collapse_states`。定数 `NO_CATEGORY` / `NO_SUBGROUP` = -1 |
| `Data/ErrorLogMigration.cs` | 64 | error_logs 移行 |
| `Data/CategoryGroupMigration.cs` | 32 | 区分結合設定移行 |

### 3.3 Services（業務ロジック）
| ファイル | 行 | 役割 |
| --- | --- | --- |
| `Services/ExcelImportService.cs` | 1208 | Excel日報の取込・バリデーション（最大ファイル） |
| `Services/CostAggregationService.cs` | 455 | ★原価集計エンジン（日単位集計）。単価は `parse_m` で `engineer_daily_rate` / `assistant_daily_rate` を参照（`parse_m_any` は**実在しない**／6.4 参照） |
| `Services/WorkloadAggregationService.cs` | 426 | ★工数表の集計エンジン（**v1.2.0 新規**・業務単位集計と同一計算＋キーワード振り分け）。単価キー優先順を 2026-07-15 に修正（`[9A-fix4]`・6.4 参照） |
| `Services/PrintService.cs` | 568 | 印刷（プレビュー・列選択） |
| `Services/Excelexportservice.cs` | 501 | Excel書き出し |
| `Services/EditHistoryService.cs` | 178 | Undo/Redo履歴 |
| `Services/ErrorLogService.cs` | 167 | エラーログ記録（NAS `error_logs`） |
| `Services/ProjectGroupService.cs` | 174 | ★大区分（会社）構築の共通化（**v1.2.0 新規**・原価集計/工数表で共用） |
| `Services/CategoryGroupService.cs` | 155 | 区分結合設定 |
| `Services/UpdateCheckerService.cs` | 145 | 自動アップデート確認 |
| `Services/UserSessionService.cs` | 35 | セッション登録・ハートビート（推測） |

### 3.4 ViewModels（MVVM）
| ファイル | 行 | 役割 |
| --- | --- | --- |
| `ViewModels/filter_tab_view_model.cs` | 1483 | ★絞り込みタブ（集計モード・業務単位集計 `load_task_mode_async`・ⓘ生成）。単価は `get_rate` で `engineer_daily_rate` / `assistant_daily_rate` を参照（工数表もこのキーに統一済み／6.4 参照） |
| `ViewModels/workload_classify_view_model.cs` | 914 | 工数表の分類設定ダイアログVM（**未コミット新規**・設計書に個別記載なし）。保存反映方式の編集用モデル3種＋9コマンド＋該当件数プレビュー＋`save_async`（1トランザクション）。詳細は「12.1」 |
| `ViewModels/project_cost_view_model.cs` | 729 | ★案件別原価VM（モード適用・別モード複製） |
| `ViewModels/workload_view_model.cs` | 790 | ★工数表画面VM（**v1.2.0 新規**・大区分/現場タブ/区分タブ/分類設定への導線）。サブ分類グループの開閉は**「現場タブ × 区分タブ」別**に保持（2026-07-15 `[9D-2]` 修正・12章参照） |
| `ViewModels/settings_view_model.cs` | 710 | ★設定画面VM（DB設定・管理メニュー・startup_page） |
| `ViewModels/cost_view_model.cs` | 599 | 原価集計画面の親VM（推測） |
| `ViewModels/import_view_model.cs` | 491 | 日報取込VM |
| `ViewModels/main_view_model.cs` | 358 | ★メインVM（ページ遷移・`navigate_to_workload_command`・`is_workload`） |
| `ViewModels/Dashboard_view_model.cs` | 343 | ダッシュボードVM（お知らせ・履歴・自動更新） |
| `ViewModels/master_employees_view_model.cs` | 301 | 人員マスタVM |
| `ViewModels/user_management_view_model.cs` | 278 | ユーザー管理VM |
| `ViewModels/BackupRestoreViewModel.cs` | 280 | バックアップ復元VM |
| `ViewModels/category_group_view_model.cs` | 250 | 区分結合設定VM |
| `ViewModels/ErrorLogViewModel.cs` | 185 | エラーログ参照VM |
| `ViewModels/master_archived_projects_view_model.cs` | 170 | アーカイブ案件VM |
| `ViewModels/master_equipment_view_model.cs` | 172 | 機材マスタVM |
| `ViewModels/master_amount_view_model.cs` | 145 | 単価マスタVM（推測） |
| `ViewModels/master_legacy_name_view_model.cs` | 131 | 旧氏名マップVM |
| `ViewModels/master_vehicles_view_model.cs` | 127 | 車両マスタVM |
| `ViewModels/relay_command.cs` | 34 | RelayCommand実装 |
| `ViewModels/base_view_model.cs` | 23 | VM基底 |

### 3.5 Views（XAML画面・コードビハインド）
| ファイル | 行(.cs) | 役割 |
| --- | --- | --- |
| `Views/CostPage.xaml(.cs)` | 1805 | ★原価集計画面（現場タブ・折りたたみ・ドラッグ並べ替え・変更▾メニュー） |
| `Views/MainWindow.xaml(.cs)` | 699 | ★メイン（サイドバー＋ヘッダ＋コンテンツ・全ショートカット・startup_page反映・工数表ボタン追加） |
| `Views/PrintOptionsDialog.xaml(.cs)` | 308 | 印刷設定ダイアログ |
| `Views/ExcelExportDialog.xaml(.cs)` | 302 | Excel書き出しダイアログ |
| `Views/ProjectRatesDialog.xaml(.cs)` | 287 | 現場別単価ダイアログ |
| `Views/FilterTabDialog.xaml(.cs)` | 277 | 絞り込みタブ追加ダイアログ（集計モード継承） |
| `Views/WorkloadKeywordDialog.xaml(.cs)` | 264 | 工数表キーワード登録ダイアログ（**未コミット新規**・設計書個別記載なし）。明細右クリックから起動・**即時DB反映**（「12.1」参照） |
| `Views/DashboardPage.xaml(.cs)` | 234 | ダッシュボード |
| `Views/WorkloadCopyDialog.xaml(.cs)` | 220 | 他案件から業務区分をコピーするダイアログ（**未コミット新規**）。**DB書込なし**＝選択分は編集中リストへ渡り分類設定の保存で確定（「12.1」参照） |
| `Views/MonthRangeCalendar.xaml(.cs)` | 203 | 月度範囲カレンダー |
| `Views/SplashWindow.xaml(.cs)` | 193 | 起動スプラッシュ |
| `Views/FirstLoginDialog.xaml(.cs)` | 176 | 初回ユーザー登録 |
| `Views/Settingspage.xaml(.cs)` | 167 | 設定画面 |
| `Views/Archivedtabsdialog.xaml(.cs)` | 119 | アーカイブタブ一覧 |
| `Views/ProjectInfoDialog.xaml(.cs)` | 117 | 現場情報ダイアログ |
| `Views/ProjectEditDialog.xaml(.cs)` | 105 | 現場編集ダイアログ（F2） |
| `Views/ImportPage.xaml(.cs)` | 98 | 日報取込ページ |
| `Views/WorkloadPage.xaml(.cs)` | 87 | ★工数表ページ（**v1.2.0 新規**・原価集計と同一の現場タブUI） |
| `Views/BulkAttributeDialog.xaml(.cs)` | 80 | 一括属性変更 |
| `Views/WorkloadClassifyDialog.xaml(.cs)` | 72 | 工数表分類設定ダイアログ（**未コミット新規**・設計書個別記載なし）。3ペイン構成・保存反映方式（「12.1」参照） |
| `Views/TabNameInputDialog.xaml(.cs)` | 59 | タブ名入力 |
| `Views/rates_action_dialog.xaml(.cs)` | 57 | 単価アクション |
| `Views/PlaceholderPage.xaml(.cs)` | 46 | 空ページ |
| `Views/PrintPreviewWindow.xaml(.cs)` | 38 | 印刷プレビュー |
| `Views/HelpDialog.xaml(.cs)` | 18 | ヘルプ（F1） |
| `Views/ErrorLogPage.xaml` | — | エラーログ参照ページ（.xaml のみ） |

### 3.6 Converters / Assets / Models
- **Converters/**: BoolToVisibility / IntEqual / Inversebool / StringEqual / StringToColorBrush（XAMLバインド変換）
- **Assets/Styles.xaml**: カラーパレット・スタイル定義
- **Models/**: `project` `Daily_report`(+`Daily_report_row`) `Daily_equipment` `Daily_transport` `Cost_record` `Cost_row_item`(187行・小計行) `Cost_filter_tab` `employee` `operation_log` `App_setting` `UserSession` `ImportResult`(113行)、**`workload_models.cs`(166行・v1.2.0 新規)**

---

## 4. 起動フロー（設計書 3.2）

| 順序 | 処理 |
| --- | --- |
| ① | `App.xaml.cs` OnStartup → 例外ハンドラ登録 → SplashWindow 表示 |
| ② | `UserSettingsManager.apply_immediate()` でテーマ・フォント適用 |
| ③ | ローカルDB初期化 → V2 → ErrorLog → CategoryGroup → **Workload** → migrate_indexes |
| ④ | ローカルDBから `nas_path` 読込 → NAS切替 |
| ⑤ | NAS DB ヘッダー検証。破損時は**復旧ダイアログ（3択）** → 復元 → 再起動/終了 |
| ⑥ | NAS DBにも各Migration → migrate_indexes(NAS) |
| ⑦ | 5分以上更新なしの他PCセッションを inactive 化 |
| ⑧ | バックアップ作成 → 1ヶ月超削除 → 7日超エラーログ削除 |
| ⑨ | バージョンチェック（NAS `01_version.txt`・fire-and-forget） |
| ⑩ | `MainWindow.OnSourceInitialized` でウィンドウ状態復元 |
| ⑪ | MainWindow表示 → Splash閉じる → MAC認証 → セッション登録 → ハートビート開始 |
| ⑫ | `UserSettings.startup_page` を読み、`"cost"` なら原価集計へ遷移（v1.0.3 追加） |
| ⑬ | Closing: 状態保存 → shutdown要求 → 1秒強制終了タイマー → VM.Dispose → セッション inactive → Shutdown |

---

## 5. 画面構成

| 画面 | ファイル | 概要 |
| --- | --- | --- |
| メイン | MainWindow | サイドバー＋ヘッダ＋コンテンツ。ショートカット一括管理・startup_page反映 |
| スプラッシュ | SplashWindow | 起動進捗（アイコン・バージョン・プログレス） |
| ダッシュボード | DashboardPage | お知らせ・操作履歴・読込履歴・60秒自動更新・更新バナー |
| 原価集計 | CostPage | 現場タブ・折りたたみ・ドラッグ並べ替え・複数選択・集計モード切替 |
| **工数表** | **WorkloadPage** | **業務区分別の原価・延べ人数集計（v1.2.0 新規）** |
| 日報取込 | ImportPage | Excel読込・バリデーション・新規社員通知 |
| 設定 | SettingsPage | DB設定・管理メニュー・詳細設定・エラーログ参照 |
| 各種ダイアログ | 一括属性変更/Excel書出/印刷/初回登録/ヘルプ 他 | — |

---

## 6. 主要機能（バージョン別ハイライト）

### 6.1 工数表機能（v1.2.0・新設）★中核
業務単位集計（`agg_mode="task"`）の案件について、日報の業務内容(`detail`)を**キーワード部分一致で業務区分・サブ分類に自動振り分け**し、区分別に原価・延べ人数を**案件累計**で集計する。

| 項目 | 仕様 |
| --- | --- |
| 対象案件 | `is_active=1` かつ、**`projects.agg_mode='task'` または `cost_filter_tabs` に `agg_mode='task' AND is_archived=0` が1件以上**（OR条件・`[9A-fix2]` で複製タブ運用時の誤判定を修正） |
| 集計項目 | 原価／延べ人数(技師/助手/不明)／件数 |
| 区分判定 | `detail` への部分一致（正規化後・`Ordinal`）。区分 `sort_order`→`id` 順、区分内キーワード `priority`→`id` 順で**最初にマッチした区分で確定** |
| サブ分類 | common（案件共通）/custom（区分個別）/none の3モード。**区分が確定した行のみ**評価 |
| 未分類 | どの区分にもマッチしない行を「未分類」タブに明細表示（キーワード登録の起点） |
| 正規化 | 全角→半角（`c - 0xFEE0`）・全角スペース→半角・「、」→「・」・Trim・小文字化。判定の両辺に適用（例:「余呉川第2」の登録で「余呉川第２」にもマッチ） |
| 原価計算 | **業務単位集計 `load_task_mode_async` と同一計算で合計一致**。粒度＝日付×作業者×業務内容。人日＝`Round(合計時間 ÷ base_hours_per_day, 4)`（`base_hours<=0` なら8にフォールバック）。技師/助手いずれにも該当しない行は人件費0のまま `unknown_days` に人日を保持。（単価キー不一致による合計差は 2026-07-15 に修正済み＝6.4 参照） |
| 機材費 | 1グループ内で同一機材の合計時間 **6.0h 以上**のみ日額計上（→日単位集計と差が出る場合あり・仕様） |
| 交通費 | 往復距離(`distance×2`) × 単価。`travel_method=="高速"` かつ往復 ≤250km なら高速単価、それ以外は下道単価 |
| 単価 | `app_settings` から `get_rate_any` で優先順探索（**`engineer_daily_rate`→`engineer_rate`**／既定 34,800円、**`assistant_daily_rate`→`assistant_rate`**／既定 28,000円、`base_hours_per_day`／既定 8、`road_cost_per_km`／既定 50、`highway_cost_per_km`／既定 100）。設定画面・日単位集計・業務単位集計と**同じ生きたキーを優先**（2026-07-15 修正・6.4 参照）。**現場別単価は非適用** |
| 除外 | `category_code NOT IN ('有給','有休')`。区分結合は `CategoryGroupService.get_child_codes_async` で子コードも対象に含める |
| 主要関数 | `WorkloadAggregationService.aggregate_async` / `classify_category` / `classify_subgroup` / `normalize` / `ProjectGroupService.build_groups` |

### 6.2 集計モード機能（v1.1.0）
原価集計タブを**日単位集計**（`cost_records` 参照）と**業務単位集計（担当者別）**（`daily_reports` を 日付×作業者×業務内容 で都度集計・非破壊）で切替。「変更▾」2段メニューで「モード選択→このタブ/複製タブに適用」。モードは `projects.agg_mode`／`cost_filter_tabs.agg_mode` に永続化。

### 6.3 DB破損復旧の強化（v1.2.0 / Sprint 8）
起動時のヘッダー破損検知時、**復旧ダイアログを3択化**（最新で復元／バックアップを選んで復元／終了）。`WAL/SHM` 検出で他PC使用中を警告（既定=中止）。バックアップは本番／テスト（`99_Test`→`_test`）で保存先分離。

### 6.4 単価キー不一致（v1.2.0・**2026-07-15 修正済み**）

**結論**: 工数表だけが単価の「死にキー」を優先していたため、設定画面で単価を変更すると工数表のみ既定値を使い続け、業務単位集計と合計がズレる**潜在バグ**だった。`WorkloadAggregationService` のキー優先順を逆転して解消（`[9A-fix4]`）。

#### 調査で確定した事実（実ソース＋実DB）

| キー | 実態 | 確度 |
| --- | --- | --- |
| `engineer_rate` / `assistant_rate` | `database_manager.cs:298-299` の `insert_default_setting`（**`INSERT OR IGNORE`**）でDB作成時に一度だけ 34800/28000 が入る。**以後どこからも更新されない死にキー**（設定画面も書かない。`Models/App_setting.cs` の `setting_keys` 定数も**全く使われていない**） | **確定**（grep で書込箇所なしを確認） |
| `engineer_daily_rate` / `assistant_daily_rate` | 設定画面（`master_amount_view_model.cs:116-117`）が**保存する**。日単位集計（`CostAggregationService.cs:83-84`）と業務単位集計（`filter_tab_view_model.cs:318-319`ほか）が**読む**。＝**生きているキー** | **確定** |

#### 実DBの状態（2026-07-15・読み取り専用で確認）

| キー | 本番NAS DB | ローカルDB |
| --- | --- | --- |
| `engineer_rate` | **34800（行あり）** | 34800（行あり） |
| `engineer_daily_rate` | **行なし** | 行なし |

→ この「`engineer_daily_rate` の行がまだ存在しない」ため、旧実装でも**たまたま両者が 34800 に一致**しており（工数表は行から取得／業務単位集計はフォールバックで既定値）、バグは**表面化していなかった**。設定画面で単価を1回変更・保存した時点で顕在化する状態だった。

#### 修正内容

`WorkloadAggregationService.cs:173-174` のキー優先順を逆転（旧キーは互換フォールバックとして保持）:

```csharp
// 修正前（死にキーを優先＝誤り）
get_rate_any(setting_dict, new[] { "engineer_rate", "engineer_daily_rate" }, 34800m)
// 修正後（生きているキーを優先＝他の集計と一致）
get_rate_any(setting_dict, new[] { "engineer_daily_rate", "engineer_rate" }, 34800m)
```

実DBデータでの検証結果（新旧比較）:

| シナリオ | 工数表(修正前) | 工数表(修正後) | 業務単位集計 | 判定 |
| --- | --- | --- | --- | --- |
| 現在の本番NAS DB のまま | 34,800 | 34,800 | 34,800 | 修正前=一致／修正後=一致（**既存の数字は不変＝デグレなし**） |
| 設定画面で 36,000 に変更・保存後 | 34,800 | 36,000 | 36,000 | 修正前=**ズレ**／修正後=**一致**（バグ解消） |

#### ⚠️ 旧記述の誤りについて（記録として残す）

本台帳の旧版には「**v1.2.0 で単価キー不一致バグを修正済み（`get_rate_any`/`parse_m_any`）**」「実在キーは `engineer_rate` で、`*_daily_rate` は存在しない」という記述があったが、**これは事実誤認だった**。
- `parse_m_any` は**実在しない**（`WorkloadAggregationService.cs` のコメントに名前が出るだけ。`CostAggregationService` は今も `parse_m` ＋ `engineer_daily_rate`）。
- `*_daily_rate` は設定画面が保存するため**実在する**（保存操作後）。「存在しない」という前提が誤り。
- この誤診に基づく“修正”が工数表にのみ入った結果、今回の不整合が生まれた。**DB既定値に存在するキー＝正しいキー、とは限らない**（書き手が誰かまで追う必要がある）という教訓。

> **同じ誤りが設計書2点にも載っていた（2026-07-15 訂正済み）**
> 基本設計書 v1.2.0 の 5.5／5.6 と要件定義書 v1.2.0 の 7.3、および両者の変更履歴（v1.2.0 行）が、上記の誤った前提を**「仕様」として記載**していた。
> 本台帳だけを直しても、設計書を読んだ人が `[9A-fix4]` を「バグ」と誤認して**逆向きに戻す**危険があったため、**v1.2.1 として訂正**した（12章の該当ログ参照）。
> 設計書 5.6 が「`CostAggregationService.cs`（`parse_m_any`）」「`filter_tab_view_model.cs`（`get_rate_any`）」を v1.2.0 の**変更ファイルとして挙げていたのも誤り**。
> 両ファイルは当初から生きているキーを参照しており、**v1.2.0 で変更されていない**（`git diff HEAD` が空であることを確認）。実際の修正対象は `WorkloadAggregationService.cs` のみ。

#### 残課題（別件・未対応）

`highway_distance_cap`（高速単価の距離上限）は設定画面（`master_amount_view_model.cs:121`）が保存するが、**工数表・業務単位集計・日単位集計の3つとも `250` をハードコード**しており（`WorkloadAggregationService.cs:278`／`filter_tab_view_model.cs:454,768`／`CostAggregationService.cs:233`）、**この設定はどこにも効いていない**。3者とも同じ値のため合計差は出ないが、「設定が反映されない」問題として別途要対応。

### 6.5 過去バージョンの要点
- **v1.0.3**: 3件のバグ修正（保存して再起動で接続不可＝JSON書込堅牢化+再起動競合排除／起動時表示ページ反映の実装漏れ修正／スプラッシュアイコン非表示＝csproj+Inno Setup 2段階の漏れ修正）
- **v1.0.2**: スプラッシュウィンドウ・NAS DBヘッダー破損自動検知/復元
- **v1.0.1**: 設定保存先を `%LOCALAPPDATA%` に変更・ウィンドウ状態記憶・DBインデックス9個
- **v1.0.0**: 正式リリース・Inno Setupインストーラ・自動アップデート

---

## 7. DBテーブル仕様

### 7.1 業務データ
| テーブル | 用途 |
| --- | --- |
| projects | 現場マスタ（category_code/site_name/company_name/detail/attribute/tab_color/is_active/sort_order/**agg_mode**） |
| daily_reports | 日報本体（1人1日1業務=1行）。工数表の分類元 `detail` を保持 |
| daily_equipment / daily_transport | 日報の機材・交通明細 |
| cost_records | 集計後の原価（日単位集計の表示元） |
| cost_filter_tabs | 絞り込みタブ設定（agg_mode保持） |

### 7.2 マスタ
employees / equipment_rates / vehicles / vehicle_rates / legacy_name_map / category_groups

### 7.3 ユーザー・システム
pc_users / user_sessions / operation_logs / error_logs（7日自動削除）/ app_settings（ローカルDBに保存）

### 7.4 工数表テーブル（v1.2.0 新設・すべて `CREATE TABLE IF NOT EXISTS`）
| テーブル | 説明 |
| --- | --- |
| workload_categories | 業務区分（案件ごと・タブ表示）。`sort_order`=タブ順かつ判定順 |
| workload_category_keywords | 区分の検出キーワード。`priority`=区分内評価順 |
| workload_subgroups | サブ分類（**案件単位**）。`category_id` は **v1.3.0 で廃止**（旧: NULL=案件共通／値あり=区分個別）。列は旧データ保全のため残すが現行コードは参照せず、新規行は NULL |
| workload_subgroup_keywords | サブ分類の検出キーワード |
| **workload_subgroup_links** | **v1.3.0 新設**。サブ分類 × 業務区分の**適用先（多対多）**。この行があるサブ分類だけがその区分タブに出る＝適用先の唯一の根拠。`uq_workload_sub_links` で `(subgroup_id, category_id)` を UNIQUE |
| workload_subgroup_modes | 区分ごとのサブ分類モード。**v1.3.0 で common / none の2値に整理**（`custom` は廃止＝適用先を区分ごとに選べるため共通／専用の区別が不要になった。旧データの `custom` は移行時に `common` へ寄せる。読み込み側は「`none` 以外＝分ける」として扱うため互換）。`uq_workload_modes` で `(project_id, category_id)` を UNIQUE ＝ UPSERT 可能 |

**サブ分類の適用先（v1.3.0 の設計変更）**
- 旧構造は「案件共通（common モードの全区分に表示）」か「1区分専用」の二択しか持てず、**共通で1件追加すると全ての区分タブに同じサブ分類が出てしまう**問題があった。区分↔サブ分類を多対多にして解決。
- **既存データは起動時に一度だけ自動変換**（`app_settings` の `workload_subgroup_links_migrated` フラグで管理）。旧構造で実際に表示されていた組み合わせをそのままリンクにするため、**移行の前後で集計結果は一致する**。1回だけ実行するのは、移行後に利用者がチェックを外して適用先を減らすのは正常な操作であり、毎回流すとその設定を旧データから復活させてしまうため。
- 区分を削除しても**サブ分類本体は削除しない**（適用先リンクを外すだけ）。サブ分類は案件単位の資産で、他の区分にも適用されうるため。旧実装は `category_id` が一致するサブ分類を削除していた（v1.3.0 で修正）。

### 7.5 表示状態テーブル（v1.3.0 新設・`Data/ViewStateMigration.cs`）
| テーブル | 説明 |
| --- | --- |
| month_collapse_states | 原価集計・月度の折りたたみ（`pc_user_id` × `filter_tab_id` × `fiscal_month`）。`uq_month_collapse` で UNIQUE ＝ `INSERT OR REPLACE` が UPSERT として成立 |
| workload_collapse_states | 工数表・サブ分類グループの折りたたみ（`pc_user_id` × `project_id` × `category_id` × `subgroup_id`）。`uq_workload_collapse` で UNIQUE |

- ⚠️ **`month_collapse_states` は読み書きコードだけが先に存在し、`CREATE TABLE` がどこにも無かった**（実DBにも不在＝テーブル26個中に無し）。`filter_tab_view_model` の save/restore は例外を `Debug.WriteLine` で握り潰すため、**無言で永続化が効いていない**状態が続いていた。v1.3.0 で DDL を追加し、既存コードがそのまま機能するようにした。
- 既定は「展開」。**折りたたんだものだけを行として持ち、展開に戻したら行を消す**（自己クリーニング）。
- 区分IDを持たないタブ（[全体]・[未分類]）は `NO_CATEGORY(-1)`、「（サブ未分類）」グループは `NO_SUBGROUP(-1)` で表す。**SQLite の UNIQUE は NULL 同士を別物として扱い重複を防げない**ため、NULL を使わない。
- 利用者ごとの見た目の好みだが、`pc_user_id` を持たせて**共有DB上に置く**（同じ利用者が別PCでも同じ状態で開ける）。`user_settings.json` はPC単位のためこの用途には使わない。

**共通仕様（実ファイル確認済み）**
- 全テーブル共通カラム: `id INTEGER PRIMARY KEY AUTOINCREMENT` / `created_at`・`updated_at TEXT DEFAULT (datetime('now','localtime'))` / `updated_by TEXT DEFAULT ''`。マスタ系は `sort_order INTEGER NOT NULL DEFAULT 0` / `is_active INTEGER NOT NULL DEFAULT 1`、キーワード系は `priority INTEGER NOT NULL DEFAULT 0`。
- インデックス: `idx_workload_categories_project` / `idx_workload_cat_kw_category` / `idx_workload_subgroups_project` / `idx_workload_sub_kw_subgroup` / `uq_workload_modes`(UNIQUE) / **`uq_workload_sub_links`(UNIQUE) / `idx_workload_sub_links_category` / `uq_month_collapse`(UNIQUE) / `uq_workload_collapse`(UNIQUE)**。
- ⚠️ **FOREIGN KEY は一切宣言されていない**。参照整合はアプリ側（保存時のカスケードDELETE）で担保している。
- 保存先は **NAS DB**（分類設定は全PCで共有）。`App.xaml.cs` でローカルDB・NAS DB の両方にマイグレーションを実行。
  折りたたみ状態も `create_connection()` 経由で読み書きするため**NAS DB 側にもテーブルが必要**（`V2Migration` はローカルDBにしか走らないため、そこには置けない）。

---

## 8. ショートカットキー

| キー | 操作 | キー | 操作 |
| --- | --- | --- | --- |
| Ctrl+Z / Y | Undo / Redo（最大10回） | Ctrl+M | 月度折りたたみ |
| Ctrl+R | 更新 | Ctrl+Shift+M | 年度折りたたみ |
| Ctrl+F | 現場タブ検索フォーカス | F2 | 現場編集ダイアログ |
| Ctrl+P / E | 印刷 / Excel出力 | F1 | ヘルプ（全画面共通） |
| Ctrl+N | 絞り込みタブ追加 | Ctrl+U | 人員単価変更 |

---

## 9. 環境・パス定義

| 項目 | 値 |
| --- | --- |
| 本番NAS DB | `\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\00_Database\ea_core.db` |
| テストNAS DB | `\\NAS7E6AA6\...\99_Test\01_CostManager\ea_core.db` |
| ローカルDB | `C:\Users\{user}\Documents\EA_DataCore\db\ea_core.db` |
| バックアップ | `\\NAS7E6AA6\...\01_Backups\01_Cost Manager`（テストは `_test` サブフォルダ） |
| 設定JSON | `%LOCALAPPDATA%\01_EABASE Series\01_CostManager\user_settings.json` |
| 破損設定退避 | `...\user_settings.json.broken_yyyyMMdd_HHmmss` |
| GitHubブランチ | リポジトリ `https://github.com/yori-1002/EABASESeries.git`。作業は **develop に集約**し、**リリース時のみ main へ早送り**（2026-07-15 の yori 指示） |
| publish 出力 | `01_EA_CostManager\EA_CostManager\publish\`（`CostManager.exe` / `01_version.txt` / `icon_fix.ico`）。`.iss` の `MySourceDir` がここを指す |
| インストーラ出力 | `01_EA_CostManager\installer_output\EA_CostManager_setup_vX.X.X.exe`（`.iss` の `OutputDir` は .iss からの相対） |
| Inno Setup | **`C:\Users\earth\AppData\Local\Programs\Inno Setup 6\`（6.7.1）**。⚠️ **ユーザー領域へのインストール**のため `Program Files` にも HKLM のアンインストール登録にも無い。CLI は同フォルダの `ISCC.exe`（GUI は `Compil32.exe`） |
| 更新配布先(NAS) | `\\NAS7E6AA6\...\02_Updates\01_Cost Manager\` に `EA_CostManager_setup_vX.X.X.exe` と `01_version.txt` を置くと自動アップデートが発火 |

> ⚠️ **DB接続先の整合性に関する注意（正確性重視ルール）**: 本システムは NAS DB とローカルDB（フォールバック）の二段構成。
> 書込先と読込先の不一致・フォールバック時の整合性は、コード修正時に必ず確認する必要がある領域。

---

## 10. 設計書と実装の差分・要確認点（★正確性重視の指摘）

以下は、設計書 v1.2.0 とソース実体の突き合わせで検出した**確認すべき事実**。断定できるものと推測を分けて記載する。

| # | 事実 | 確度 | 影響／推奨アクション |
| --- | --- | --- | --- |
| 1 | ~~csproj の `<Version>` が **1.1.0 のまま**~~ → **2026-07-15 解消**。csproj・`setup.iss` とも **v1.3.1** に更新 | **確定**（csproj 34–36行・.iss 29行） | **対応完了**。`.iss` 側は csproj から自動連動しないため、**今後もリリースのたびに2箇所を合わせること**（0章の注意書き参照） |
| 2 | ~~工数表機能の新規ファイル群が**未コミット**~~ → **2026-07-15 解消**。develop にコミット済み・**2026-07-16 にリリースビルドとインストーラも作成** | **確定**（git log・installer_output） | **対応完了**。残る未了は **main へ未反映**（yori が実行）と **画面での動作確認が未実施**（#12） |
| 3 | 設計書に個別記載のない実装ファイルが存在：`workload_classify_view_model.cs`(914行)・`WorkloadClassifyDialog`・`WorkloadCopyDialog`・`WorkloadKeywordDialog` | **確定**（存在・役割とも 2026-07-15 の精査で確定。「12.1」参照） | 設計書は分類設定UIを「暫定（標準区分セット作成）」とするが、**実装は3ペインの本格的な分類設定UI＋他案件コピー＋明細からのキーワード登録まで到達しており、設計書より先行している**（確定）。→ 次回の設計書更新で反映が必要 |
| 9 | ~~**工数表と業務単位集計の合計が、サブ分類なしの全体表示でもズレる**（単価キーの優先順が逆）~~ → **2026-07-15 修正済み**（12章の該当ログ参照） | **確定**（実ソース＋実DB調査で裏付け済み） | **対応完了**。`WorkloadAggregationService.cs:173-174` のキー優先順を `engineer_daily_rate`→`engineer_rate` に逆転し、設定画面・日単位集計・業務単位集計・工数表の4者が同じ値を見るようにした。詳細な調査結果は「6.4」参照 |
| 6 | 設計書 6.1 の「対象案件＝`agg_mode='task'` のみ」という記述が実装と不一致。実装は `cost_filter_tabs`(`agg_mode='task' AND is_archived=0`)との **OR 条件** | **確定**（`workload_view_model:266-277`・`[9A-fix2]` コメント） | 本 md 6.1 は 2026-07-15 に修正済み。**設計書側も次回更新時に要修正** |
| 7 | 工数表5テーブルに **FOREIGN KEY 制約が未宣言**（参照整合はアプリ側のカスケードDELETEで担保） | **確定**（`WorkloadMigration.cs`） | 現状は動作上問題ないが、設計思想として設計書に明記しておくのが望ましい（要判断） |
| 8 | `ProjectGroupService.build_groups_auto` / `extract_prefix` が実装済みだが**現行フローから未呼出**（Sprint 9B 用） | **確定**（grep で呼出なし・コード内コメントにも明記） | デッドコードではなく次期用の先行実装。11章の「Sprint 9B: 大区分の自動抽出」に対応 |
| 4 | パッケージ `CommunityToolkit.Mvvm 8.3.2` が設計書の主要ライブラリ一覧に未記載 | **確定**（csproj 80行） | MVVM基盤なので設計書 2.2 に追記するのが望ましい |
| 5 | ~~設計書 docx がルート直下と `doc/` に二重・複数版で並存~~ → **2026-07-15 解消**。`00_Project_Docs/` に集約し v1.2.1 へ一本化（12章の該当ログ参照） | **確定**（`ls 00_Project_Docs/`・git status） | **対応完了**。旧 v1.2.0 は削除したが、削除前に一度コミットしたため**git 履歴から復元可能**。誤って旧版（単価キーの誤記述あり）を開く事故はこれで起きない |
| 10 | **設計書2点が v1.2.1 のままで、本体 v1.3.1 の変更が未反映**。特に「サブ分類は案件共通／区分専用の二択」「モードは common/custom/none の3値」は**現行実装と食い違う**（v1.3.0 で多対多化・`custom` 廃止） | **確定**（docx 表紙・`WorkloadMigration.cs`） | ⚠️ **6.4 と同じ事故の再発リスク**。設計書どおりに実装し直すと `[Sprint 7D]` が巻き戻る。運用ルール #2 に基づき、v1.3.x を織り込んだ**完全版として設計書を更新**すること（`workload_subgroup_links`・`workload_collapse_states`・`month_collapse_states`・`classify_index` の共通化を反映） |
| 11 | **運用ルール #1（その都度 md へ反映）が本 md の内部にしか書かれておらず、作業者がこの md を読むまでルールを認識できない**。実際に v1.2.1〜v1.3.1 の7コミットが未反映のまま進行し、yori の指摘で事後にまとめて追記した | **確定**（12章の 2026-07-15 最終ログ・`ls CLAUDE.md` で不在を確認） | **恒久対策としてリポジトリ直下に `CLAUDE.md` の新設を提案**。AIコーディング支援は `CLAUDE.md` を自動で読み込むため、そこに「本台帳の場所」「運用ルール #1〜#4」「続きを進める＝3点を読む」を書けば、**読み忘れが構造的に起きなくなる**。本 md には詳細を残し、`CLAUDE.md` からは参照させる（二重管理を避ける） |
| 12 | **v1.3.1 の一連の変更は画面での動作確認が未実施**（工数表の分類設定・折りたたみ・原価集計の現場情報バー） | **確定**（作業ログ） | インストーラ作成・配布の前に実機確認を推奨。特に**起動時に DB マイグレーションが走る**（サブ分類の適用先変換・折りたたみテーブル作成）ため、まず工数表の集計値が従来どおりであることを確認すること |

> これらは「バグ」ではなく**リリース確定前の作業途中に見える状態**。正確性重視の観点で、
> v1.2.0 を正式確定する前に #1〜#3・#6 の整合を取ることを推奨する。
> ※ #3 の各ダイアログの内部仕様は **2026-07-15 に精査済み**（「12.1」に記載）。この時点で #3・#6 は「推測」から「確定」に更新した。
> ※ **2026-07-15 追記**: #1・#2・#5・#9 は対応完了。**未対応は #10（設計書の追随・最優先）／#11（CLAUDE.md 新設）／#12（実機確認）**。
> #3・#4・#6・#7 は #10 の設計書更新にまとめて織り込むこと。
> ※ #9（単価キー不一致）は合計金額に直接影響する**実バグ**だったが、**2026-07-15 に修正済み**（6.4 参照）。
> 実DBに `engineer_daily_rate` 行が未作成だったため表面化していなかった潜在バグで、設定画面で単価を1回変更した時点で顕在化する状態だった。

---

## 11. 今後の予定（要件定義書より）

| 項目 | 対応バージョン |
| --- | --- |
| 工数表: 見積・請求・粗利、Excel出力、キーワード自動提案 | 次期 |
| 大区分の自動抽出・手動割り当て、詳細表示はみ出し修正 | Sprint 9B |
| 同接安定化・ペンディング保存・起動時NASアクセス最小化・Undo/Redoボタン | v1.0.4（仮）以降 |
| ローカルDB方式へのアーキテクチャ移行（NAS共有SQLiteの構造的限界の本質解決） | v1.x.0（中長期） |

### NAS共有SQLite の構造的限界（参考）
- **改善可能**: インデックス／破損検証／JSON書込堅牢化／再起動競合排除／クエリ最適化／マスタキャッシュ／ログ自動削除
- **使えない最適化**: WALモード・共有キャッシュモード（NAS共有では非推奨/不可）
- **本質的に未解決**: 同接時の書込ロック競合・NAS遅延・2台目起動の待機 → ローカルDB方式移行が本質策

### DIMS 方式による DB 接続設計（提案・未実装）

> 出典: `00_Project_Docs/DIMS_SQLite共有_設計書_v0.1.0.docx`（DIMS 共通基盤・他プロジェクト流用前提）。
> 本節は **AS-IS（現状の確定事実）** と **TO-BE（提案・未実装）** を明確に分ける。
> TO-BE は 2026-07-15 に yori と方針合意した段階案であり、コードには未反映。
> ⚠️ この節は codex レビュー対象（レビュー後に DB 接続の実装へ進む予定）。

**AS-IS：現状の DB 接続（実コードで確認・確定）**

| # | 事実 | 根拠 |
| --- | --- | --- |
| A1 | `create_connection()` は `Data Source={get_db_path()}` を開くだけ。NAS 有効時は `get_db_path()` が **NAS の UNC パス**を返すため、**全PCが NAS 上の単一 `.db` ファイルへ直接接続**する | `database_manager.cs:70`（接続文字列）・`:76`（`create_connection`）・`:611`（`switch_to_nas`）・`:40`（`set_db_path`） |
| A2 | 接続時 PRAGMA は `journal_mode=WAL` / `busy_timeout=5000`（v0.9.7 で30秒→5秒）/ `foreign_keys=ON` | `database_manager.cs:84`・`:91`・`:95` |
| A3 | ⚠️ **WAL はネットワーク共有上では動作対象外**（共有メモリ `-shm` の mmap が必要で SMB では成立しない）。DIMS 設計書 1.1 も「NAS 上の単一 SQLite への複数プロセス同時書込は採用しない」と明記 | SQLite 公式ドキュメントの WAL 制約（推測：本番の busy_timeout 短縮・破損検証・復元機能の存在は、実際にロック競合／破損を踏んだ結果と考えられる） |
| A4 | 排他制御・行識別GUID・changelog・行バージョン・楽観ロックは**いずれも無い**。主キーは全テーブル `INTEGER PRIMARY KEY AUTOINCREMENT`。`Guid.NewGuid()` の使用は `import_view_model.cs:267` の `batch_id` 1箇所のみ | grep で確認（`row_guid` 不在・AUTOINCREMENT 多数） |
| A5 | ローカルDBの既定パスは `Documents\EA_DataCore\db\ea_core.db` | `database_manager.cs:19` |
| A6 | アプリ実行中ずっと動く常駐ループは `main_view_model` の `DispatcherTimer`（2分間隔・セッションハートビート）が既に存在 | `main_view_model.cs:65`・`:135`・`:139` |

> 結論：現状は DIMS 設計書 1.1 が「採用しない」とする構成そのもの。事実上の1ユーザー運用は制約ではなく妥当な判断と考えられる。

**TO-BE：DIMS 方式（ローカルレプリカ＋変更ファイル受け渡し）— 提案・未実装**

DB 接続の中核方針は「NAS の SQLite を直接開くのをやめ、各PCがローカルDBレプリカを持ち、変更分（差分）だけをファイルで受け渡す」こと。

| 区間 | TO-BE の接続方式 |
| --- | --- |
| メイン/同期処理 ↔ ローカルDB | `create_connection()` は**常にローカルレプリカ**を開く（現状の NAS 直結をやめる）。WAL がローカル単一プロセスで初めて有効に働く副次効果あり |
| ↔ NAS | SQLite 接続は張らない。NAS へは**受け渡しフォルダのファイルI/O のみ**（`.tmp`→rename で書きかけ回避） |
| 変更の記録 | 書込は「ローカルDB更新＋changelog 追記」を1トランザクションで行う |
| 同期の実行 | **常駐エージェントは作らず**、既存 `DispatcherTimer`（A6）と同じ形の**アプリ内タイマー**で、未送信 changelog を NAS へ書出／他PCの変更ファイルを取込 |

**段階実装（yori 合意）**

| Phase | 内容 | 位置づけ |
| --- | --- | --- |
| 0 | 現状の単一ユーザー運用を明示的に安全化：起動時に NAS DB へロックを取り、2人目は**読み取り専用**で開く。A6 のハートビートを流用可 | 同期実装の有無に関わらず先に入れる価値がある安全策 |
| 1 | 全同期対象テーブルへ `row_guid`(GUID) と `row_version` を追加＋`changelog` テーブル＋**書き込み層の集約**。yori から「IDで分ける分のIDが無いのは追加しよう」と明示合意 | **真のコスト**。書込口が `CostPage.xaml.cs`・各VM・各Service に分散しており、changelog は「全書込が必ず通る場所」がないと成立しないため、寄せる作業が最大の工数。早いほど安い |
| 2 | 同期本体（差分適用・競合解決 LWW・tombstone・冪等適用）をアプリ内タイマーで実行 | 常駐なし。集約役も畳める（マスターDBは新規PC参加・復旧の起点として定期スナップショットで足りる） |

**設計書側で実装前に詰める宿題（DIMS 設計書 v0.1.0 に対する要確認点）**

| # | 論点 | 内容 |
| --- | --- | --- |
| D1 | 列単位マージ不可 | payload が行全体のため、同一行の別列を2台が編集すると片方の列変更が黙って消える（history には残る）。変更列だけを payload に入れる案は検討価値あり |
| D2 | `row_version` 採番規則が未定義 | 誰がどう増やすか決めないと、2台が同じ値を作り device_id タイブレークに落ち、常に同じPCが勝つ |
| D3 | 参照整合の適用順序 | `foreign_keys=ON`(A2) のため、親の tombstone と子の追加が競合すると FK 違反・孤児。適用順（親→子）と tombstone 済み親に子が来た時の扱いを決める |
| D4 | 同期対象テーブルの線引き | `user_sessions` のオンライン表示はリアルタイム性が要り、数分間隔の差分ファイル同期と相性が悪い。別扱いにするか諦めるか要判断 |

---

## 12. 更新ログ（プログラム修正・追加の記録）

> 運用ルール #1 に基づき、**コードの修正・追加・削除を行うたびにここへ追記する**。
> 記載形式: 日付 / 対象バージョン / 対象ファイル / 変更内容 / 種別（追加・修正・削除・リファクタ）。
> 併せて本 md の該当セクション（3. アーキテクチャ、6. 主要機能、7. DBテーブル 等）も同時に最新化すること。

| 日付 | 対象Ver | 対象ファイル | 変更内容 | 種別 |
| --- | --- | --- | --- | --- |
| 2026-07-15 | — | `doc/EA_CostManager_プロジェクト全体まとめ.md` | ドキュメント運用ルール（「続きを進める」で本 md を読む・修正の随時記載・設計書/要件定義書の定期最新化）を冒頭に明記。本更新ログセクションを新設。下記「12.1」として v1.2.0 の既存実装を遡及記載。 | 追加 |
| 2026-07-15 | — | `doc/EA_CostManager_プロジェクト全体まとめ.md` | 運用ルール #3 を改訂：「続きを進める」時に読むのは本 md **＋基本設計書＋要件定義書の3点**に変更。 | 修正 |
| 2026-07-15 | v1.2.0 | `ViewModels/workload_view_model.cs` | **工数表「すべて折りたたむ」を現場タブ（案件）別の状態に変更**。旧実装は `private bool _all_expanded` を VM に1つ持つだけで、開閉状態が工数表全体で共有されていた（ある現場タブで折りたたむと、他の現場タブに切り替えても折りたたまれたまま）。`Dictionary<int, bool> _expanded_by_project`（キー＝案件ID）で案件ごとに保持する方式へ変更し、`all_expanded` を選択中案件の値を読み書きする算出プロパティ化。既定値は `EXPANDED_DEFAULT = true`（展開＝従来の見え方を維持）、案件未選択時は既定値を返し setter は何もしない。`selected_project` の setter に `all_expanded` / `expand_toggle_text` の変更通知を追加（ボタン文言を切替先の状態に合わせるため）。`expand_toggle_text` の参照元をフィールドから算出プロパティへ変更。**XAML は変更なし**（Expander の `IsExpanded` は `all_expanded` への OneWay バインドのままで、現場タブ切替時に `load_workload_async` が tabs を作り直す際に読み直される）。ビルド成功（0エラー）。 | 修正 |
| 2026-07-15 | v1.2.0 | `doc/EA_CostManager_記録台帳.md` | 本MDの名称を「記録台帳」として扱うよう表題・位置づけを更新。工数表と業務単位集計の合計差について、**サブ分類で分けた時の内訳差は把握済み仕様、全体表示でも合計がズレる場合は単価キー不整合による設計ミス／実装不整合候補**として10章 #9へ追記。プログラム本体は未修正。 | 修正 |
| 2026-07-15 | v1.2.0 | `Services/WorkloadAggregationService.cs` | **単価キー不一致バグを修正（`[9A-fix4]`・10章 #9 の対応）**。`:173-174` の `get_rate_any` のキー優先順を `engineer_rate`→`engineer_daily_rate` から **`engineer_daily_rate`→`engineer_rate` に逆転**（助手も同様）。旧実装は DB既定値として一度だけ入るきりで誰も更新しない「死にキー」`engineer_rate` を優先していたため、設定画面（`engineer_daily_rate` のみ保存）で単価を変更すると**工数表だけが既定値 34800/28000 を使い続け**、日単位集計・業務単位集計と合計がズレる潜在バグだった。旧キーは互換フォールバックとして保持（`engineer_daily_rate` 行が無いDBでも従来どおり解決可能）。**実DB検証済み**：現在の本番NAS DB では修正前後とも 34,800 で業務単位集計と一致（**既存の数字は不変＝デグレなし**）、設定画面で 36,000 に変更した状態を再現すると修正前は 34,800 でズレ・修正後は 36,000 で一致（バグ解消を確認）。あわせてクラス冒頭・`get_rate_any` の**誤った前提に基づくコメントを実態に合わせて修正**（「実在キー `engineer_rate` を優先」「`parse_m_any` と同一仕様」＝いずれも事実誤認だった）。行数 408→426。ビルド成功（0エラー）。**画面での動作確認は未実施**。 | 修正 |
| 2026-07-15 | v1.2.0 | `doc/EA_CostManager_記録台帳.md` | 上記修正に伴い 6.4 を全面改訂（調査で確定した事実・実DBの状態・修正内容・実DB検証結果・旧記述の誤り・残課題 `highway_distance_cap` を記載）。10章 #9 を「修正済み」に更新。3.3／6.1 の該当記述も最新化。 | 修正 |
| 2026-07-15 | v1.2.0 | `ViewModels/workload_view_model.cs`<br>`Views/WorkloadPage.xaml` | **工数表「すべて折りたたむ」の粒度を「現場タブ × 区分タブ」別に変更（`[Sprint 9D-2]`）**。上記 `[9D]` 修正（案件別保持）でも、**同じ案件の中では開閉状態が1つ**だったため、別の区分タブに切り替えると折りたたまれたままだった。区分タブごとに見たい／畳みたいが変わるため、タブ単位で独立させた。実装：①状態の置き場所を VM から `workload_tab_item.all_expanded`（通知付きプロパティ）へ移動＝Expander は自分のタブの値だけを見る ②タブは再集計のたびに作り直されるため、VM 側の `Dictionary<string, bool> _expanded_by_tab`（キー＝`expand_key(project_id, tab)` ＝ `"案件ID|区分ID"`）に控え、`load_workload_async` のタブ生成時に復元（`:515-518`）。既定は `EXPANDED_DEFAULT = true`（展開＝従来の見え方）③`workload_tab_item.category_id`（`int?`）を新設＝保存キー用。[全体]・[未分類]タブは区分に紐づかないため `null`（両タブとも `is_grouped=false` で開閉対象外）④`toggle_expand()` は選択中タブのみ書き換え＋辞書へ控え＋ボタン文言を通知 ⑤`expand_toggle_text` / `can_toggle_expand` は `selected_tab` を参照する算出プロパティ。**XAML も変更**（`[9D]` 時点の「XAML は変更なし」から状況が変わった点に注意）：Expander の `IsExpanded` バインド先を `UserControl`（ページVM）から `RelativeSource AncestorType=DataGrid` の `DataContext.all_expanded`（＝タブ自身）へ差し替え（`WorkloadPage.xaml:350`、`Mode=OneWay` / `FallbackValue=True` は維持）。行数 743→**790**（XAML 482→**487**）。**画面での動作確認は未実施**。 | 修正 |
| 2026-07-15 | v1.2.1 | `doc/EA_CostManager_基本設計書_v1_2_1.docx`<br>`doc/EA_CostManager_要件定義書_v1_2_1.docx` | **設計書2点の単価キー記述の誤りを訂正（v1.2.0 → v1.2.1）**。両文書が 6.4 で事実誤認と確定済みの内容を「仕様」として記載しており、**そのまま実装し直すと `[9A-fix4]` が逆向きに戻ってバグが復活する**状態だったため訂正した。訂正箇所＝基本設計書：表紙（版・作成日・ステータス）／5.5 本文を実態に沿って全面書き換え＋「v1.2.0 の記述は事実誤認だった」注記を追加／5.6 の `WorkloadAggregationService.cs` 行に `[9A-fix4]` を追記・`filter_tab_view_model.cs`・`CostAggregationService.cs` の2行を「▼変更なし（v1.2.0 の記載は誤り）」に訂正／変更履歴に v1.2.1 行を追加。要件定義書：表紙（版・作成日）／7.3「単価キー不一致の解消」の実装内容を訂正／変更履歴に v1.2.1 行を追加。**v1.2.0 の変更履歴行は削除せず残した**（誤った記述が書かれていた事実の記録として。本台帳 6.4 の「旧記述の誤り」と同じ方針）。**裏取り**：`parse_m_any` は全ソースに grep で不在＝実在しないことを確認。`CostAggregationService`(`:83-84`)＝`parse_m`＋`engineer_daily_rate`、`filter_tab_view_model`(`:318-319`)＝`get_rate`＋`engineer_daily_rate`、設定画面 `master_amount_view_model.save_async`(`:116-117`)＝`engineer_daily_rate` を `INSERT OR REPLACE`、`database_manager`(`:298-299`)＝`engineer_rate` を `INSERT OR IGNORE` で既定値投入、をそれぞれ実ファイルで確認。加えて `git diff HEAD` が空＝**両ファイルは v1.2.0 で変更されていない**ことを確認した。**方法**：書式・表スタイル保持のため docx を作り直さず `word/document.xml` の該当段落・セルのみ差し替え（全13箇所）。元ファイルはバックアップ後に作業し、生成物は zip 整合性チェックとテキスト再抽出で検証済み。**旧 v1_2_0.docx は削除していない**（10章 #5 参照）。 | 修正 |
| 2026-07-15 | v1.2.1 | `doc/EA_CostManager_記録台帳.md` | 上記の設計書訂正を反映。0章サマリの設計書バージョンを v1.2.1 に、本記録台帳の位置づけの参照先を v1.2.1 に更新。6.4「旧記述の誤り」に「同じ誤りが設計書2点にも載っていた（訂正済み）」節を追加。 | 修正 |
| 2026-07-15 | — | `00_Project_Docs/`（新設）<br>`.gitignore`<br>`EA_CostManager/doc/`（廃止） | **ドキュメントを `00_Project_Docs/` に集約し、git 管理下に置いた（yori 指示）**。①**移動**：`EA_CostManager/doc/`（＝C#プロジェクトフォルダの**内側**にあった）を `01_EA_CostManager/00_Project_Docs/` へ移動・改称。設計書・要件定義書・記録台帳・操作マニュアル pptx を集約。`.csproj`／`setup.iss` は `doc/` を参照していないためビルド・配布への影響なし（grep で確認）。②**git 管理化**：`.gitignore` の `doc/` 除外行を削除。**容量を実測して判断**＝`.git` 実体 1.94MB／記録台帳MD 57KB（gzip 21KB）／docx 各30〜40KB。MDはテキストで差分圧縮が効き、docx はバイナリで1版40KB増だが、GitHub の警告水準（1ファイル50MB・リポジトリ1GB）と桁が3つ違うため**容量は問題にならない**と結論。③**重複解消**：ルート直下と `doc/` の v1.2.0 docx は md5 一致の完全な重複だったため、ルート側を削除。④**旧版削除**：v1.2.0 docx 2本を削除し v1.2.1 に一本化。ただし v1.2.0 は**一度もコミットされておらず**（ルートは未追跡・`doc/` は除外）そのまま消すと復元不可だったため、**先に集約状態をコミットして履歴に残してから次コミットで削除**する2段階とした。⑤**副次修正**：`.gitignore` の末尾が `.DS_Store* . e x e` と壊れていた（コミット済みHEAD版は UTF-16LE の NUL バイト混入＝git が**バイナリ扱い**し差分表示不能。PowerShell の既定 UTF-16 書き込みが原因と推定）。UTF-8 で書き直し、`.DS_Store` と `*.exe` を正しい2行に分離して修復。⑥運用ルール #2・#3、本台帳の位置づけ、10章 #5 のパス参照を新フォルダに更新。 | 追加 |
| 2026-07-15 | v1.2.0 | `doc/EA_CostManager_記録台帳.md` | 上記 `[9D-2]` を反映。3.4（`workload_view_model.cs` 743→790・開閉の粒度）／12.1 の行数（VM 790・WorkloadPage.xaml 487）を最新化。**本追記時点で、MD の記載が `[9D]` 止まりで `[9D-2]` が未反映になっていた**（運用ルール #1 の取りこぼし）ため、実ファイル突き合わせのうえ補記した。 | 修正 |
| 2026-07-15 | v1.2.1 | 工数表の新規ファイル群<br>`App.xaml.cs` ほか既存4本<br>`00_Project_Docs/` docx 2点<br>`.gitignore` | **12.1 に遡及記載していた工数表機能を初コミット（`9f772d2`）**。新規19ファイル＋既存改修。設計書・要件定義書を v1.2.1 として `00_Project_Docs/` に集約。`.gitignore` を UTF-16→UTF-8 へ変換（内容は同一）。v1_2_0 の docx 2点は yori 指示により**コミットせずローカルに残置**。ビルド成功（0エラー／警告2＝既存の SQLitePCLRaw 脆弱性アドバイザリ）。 | 追加 |
| 2026-07-15 | v1.2.2 | `Data/WorkloadMigration.cs`<br>`Models/workload_models.cs`<br>`Services/WorkloadAggregationService.cs`<br>`ViewModels/workload_classify_view_model.cs`<br>`Views/WorkloadClassifyDialog.xaml` | **サブ分類の適用先を区分タブごとに選べるよう多対多化（`[Sprint 7D]`・コミット `23e3397`）**。症状＝分類タブでサブ分類を1件登録すると**全ての区分タブに反映されてしまう**。原因＝サブ分類が「案件共通（common モードの全区分に表示）」か「1区分専用」の二択しか持てない構造だったこと。実装：①`workload_subgroup_links`（サブ分類 × 区分）を追加し**適用先の唯一の根拠**にする ②分類設定ダイアログにサブ分類一覧＋「適用する区分タブ」チェックリスト（全選択／全解除つき）を追加＝後からの変更もチェック切替だけ ③サブ分類追加時の適用先は**選択中の区分1つだけ**を初期値に ④**区分の削除がサブ分類本体を巻き込まないよう、適用先リンクの削除のみに変更**（サブ分類は案件単位の資産で他区分にも適用されうるため。旧実装は `category_id` 一致のサブ分類を削除していた＝別バグ） ⑤モードを common/none の2値に整理（`custom` 廃止） ⑥既存データは起動時に一度だけ自動変換（`app_settings` の `workload_subgroup_links_migrated`）。旧構造で表示されていた組み合わせをそのままリンクにするため**集計結果は不変**。1回だけなのは、移行後に利用者が外した適用先を再実行で復活させないため ⑦適用先未設定のサブ分類がある場合は保存前に確認。**検証**：実際の `WorkloadMigration` を取り込んだ検証プログラムで、旧構造（共通／専用／使わない・複数案件）からの変換結果が旧表示と一致すること・モード整理・冪等性・「外した設定が復活しないこと」の4点を確認。**画面での動作確認は未実施**。 | 修正 |
| 2026-07-15 | v1.2.2 | `Views/CostPage.xaml` | **原価集計の現場情報バーで、詳細が長いとボタンが潰れる問題を修正（コミット `bd89393`）**。原因は `DockPanel` の子の宣言順＝現場情報（左）を最初の子にしていたため、そちらが先に必要なだけ幅を確保していた。加えて現場情報が横並びの `StackPanel` で、**子には無限幅が与えられる**ため詳細がいくら長くても折り返さず伸び続けていた。修正：①ボタン類を先に宣言して幅を確保し、現場情報は最後の子（`LastChildFill`）として残り幅に収める ②現場情報を `Grid` にして**詳細の列だけを `*`** にし幅を確定＝残り幅で折り返す（表示順は従来どおり） ③長文でバーが高くなりすぎないよう2行まで（`MaxHeight=34`）とし、続きは省略記号＋ツールチップ。`find_ancestor` によるハンドラのVM解決は DataContext 経由のため並び替えの影響を受けないことを確認。行数 1093→**1148**。**画面での動作確認は未実施**。 | 修正 |
| 2026-07-15 | v1.2.2 | `Services/WorkloadAggregationService.cs`<br>`ViewModels/workload_classify_view_model.cs`<br>`EA_CostManager.csproj` | **サブ分類の該当件数を適用先の区分基準に変更／バージョン表記を更新（コミット `a24376f`）**。①**件数**：案件全体の業務行に対して数えていたため実際にタブに出る件数と大きくズレていた（例：適用先では3件なのに132件と表示）。共通／専用の二択だった頃は目安として妥当だったが、適用先を区分ごとに選ぶ今は誤解を招くため、**適用先にチェックした区分に振り分けられた行の中だけ**で数えるようにした ②**判定規則の共通化**：区分判定（並び順に評価して最初に当たった区分が取る）を `WorkloadAggregationService.classify_index` として公開し、集計本体とプレビューで**同じ関数を共有**（二重に書くとプレビューと実際の集計がずれるため。`classify_category` は廃止し、キーワードはループ外で1度だけ用意して性能を維持） ③**再計算漏れの修正**：区分の**削除・並び替え**で件数が再計算されていなかった（並び順＝判定の優先順のため振り分け先が変わる）。適用先チェックの ON/OFF・「分けない」切替でも数え直す ④区分とサブ分類で該当件数の意味が異なるため説明文を両方に触れる形へ ⑤csproj が 1.1.0 のままで**起動しているのが修正前後どちらのビルドか判別できなかった**（実際に確認作業で混乱の原因になった）ため 1.2.2 に更新。画面表示・バナーはすべて `AssemblyVersion` 連動のため csproj のみの変更で全箇所へ反映。 | 修正 |
| 2026-07-15 | v1.3.0 | `Data/ViewStateMigration.cs`（新規）<br>`App.xaml.cs`<br>`ViewModels/workload_view_model.cs` | **折りたたみ状態が再起動で展開に戻る問題を修正（`[Sprint 7E]`・コミット `b13238f`）**。原因は画面ごとに**別物が2つ**あった。①**原価集計・月度**＝読み書きコード（`filter_tab_view_model` の restore/save 3メソッド）は以前からあったが、**`month_collapse_states` の `CREATE TABLE` がどこにも無く、実DBにも存在しなかった**（テーブル26個中に無し）。両メソッドは例外を `Debug.WriteLine` で握り潰すため**無言で永続化が効いていない**状態だった。テーブルを作ることで既存コードがそのまま機能する。`INSERT OR REPLACE` が UPSERT として成立するよう UNIQUE 索引も付与 ②**工数表**＝そもそも永続化しておらず、開閉状態は VM のフィールドだけにあった（再集計・現場タブ往復では保つが再起動で必ず失われる）。`workload_collapse_states` へ保存し、ページ初期化時に**タブを組み立てる前に**読み戻す。`App.xaml.cs` のローカルDB・NAS DB 両方から呼ぶ（折りたたみ状態は `create_connection()` 経由で読み書きするため NAS 側にもテーブルが必要。`V2Migration` はローカルDBにしか走らないため使えない）。**検証**：実際の `ViewStateMigration` を取り込んだ検証プログラムで6項目（作成の冪等性・既存の月度SQL（UPSERT/復元/一括保存）がそのまま通ること・区分なしタブが重複しないこと・展開時の行削除）を確認。 | 追加 |
| 2026-07-15 | v1.3.0 | `EA_CostManager.csproj`<br>`EA_CostManager_setup.iss` | **リリース版数を v1.3.0 に確定（コミット `9eb1a23`）**。前回リリースが v1.1.0 で、v1.2.1 / v1.2.2 は develop 内のみの未リリース版のため、新機能（工数表）の追加としてマイナーを上げた。**`.iss` の `MyAppVersion` が v1.1.0 のまま取り残されていた**ことを発見し是正——そのままだと `EA_CostManager_setup_v1.1.0.exe` として出力され、レジストリと「プログラムと機能」の表示も 1.1.0 になるところだった。Inno Setup 側は csproj から自動連動できないため、**両方を合わせる旨を双方のコメントに明記**。 | 修正 |
| 2026-07-15 | v1.3.1 | `ViewModels/workload_view_model.cs`<br>`Views/WorkloadPage.xaml`<br>`Data/ViewStateMigration.cs`<br>`EA_CostManager.csproj`<br>`EA_CostManager_setup.iss` | **工数表の折りたたみをグループ単位で保存（`[Sprint 7E-2]`・コミット `bdffa52`）**。v1.3.0 でも維持されず、DB を調べると `workload_collapse_states` は**0行＝保存が一度も走っていなかった**（月度側は35行で正常）。原因は保存の粒度とグループとVMの結び方：①状態がタブ単位の bool 1つ（`all_expanded`）しかなく、**見出しを個別に開閉した状態を表現できなかった** ②Expander の `IsExpanded` がタブの `all_expanded` と **`Mode=OneWay`** で結ばれ、個別に開閉しても値がVMへ戻らず保存する手段が無かった ③**グループキーが「笙の川　121 件／…／2,396,050 円」という小計込みの見出し文字列**で、集計値が変わればキーも変わるため開閉状態の安定した目印にできなかった。実装：①`workload_group`（サブ分類ID＋見出し＋開閉状態）を追加し、明細行はこのインスタンスでグループ化（キーがIDベースになり安定） ②`IsExpanded` を `Name.is_expanded` と**双方向**で結び、個別クリックも「すべて折りたたむ」ボタンも**同じ経路でVMに伝わり保存**（ボタンは全グループの `is_expanded` を設定するだけにして保存経路を一本化＝保存漏れが起きない） ③`workload_collapse_states` に `subgroup_id` を追加。旧定義のテーブルが残っていれば検知して作り直す（未リリースかつ保持しているのが折りたたみの好みだけのため） ④「（サブ未分類）」は `NO_SUBGROUP(-1)`（SQLite の UNIQUE は NULL 同士を別物として扱い重複を防げないため） ⑤`all_expanded` をグループ側の状態から算出する値に変更＝`workload_tab_item` から変更通知が不要になり `INotifyPropertyChanged` を除去。**ボタン経路が0行になった直接原因は静的な読みでは特定できず、推測で当てにいかず作りごと入れ替えた**（個別対応には結局この作りを変える必要があり、直せばボタン側も同じ経路を通るため）。**検証**：旧定義テーブルの作り直し・冪等性、グループごとに独立して保存され重複しないこと（サブ未分類・別区分タブの同一サブ分類を含む）、展開に戻すと当該行だけ消えることを確認。行数 VM 790→**946**／XAML 487→**485**。**画面での動作確認は未実施**。 | 修正 |
| 2026-07-15 | v1.3.1 | `EA_CostManager/publish/`（成果物） | **リリースビルドを作成**。`dotnet publish -c Release -r win-x64 --self-contained true -p:DebugType=embedded -o publish`。出力＝`CostManager.exe`（65.8MB・自己完結シングルファイル）／`01_version.txt`（`1.3.1`・csproj から自動生成）／`icon_fix.ico`。`.iss` の `MySourceDir` が指す場所と一致。EXE のプロパティを実測確認＝FileVersion `1.3.1.0` / ProductVersion `1.3.1+bdffa52`。**インストーラ（Inno Setup）は未作成・main への push も未実施**。 | 追加 |
| 2026-07-17 | — | `00_Project_Docs/EA_CostManager_記録台帳.md` | **DIMS 方式による DB 接続設計を 11 章に追記（提案・未実装）**。yori 指示により、DIMS_SQLite共有_設計書_v0.1.0 のレビューと段階実装方針を台帳へ記録。AS-IS（現状の DB 接続の確定事実）と TO-BE（提案）を分離：AS-IS＝`create_connection()` が NAS UNC を直結（`database_manager.cs:70/76/611`）・WAL/busy_timeout=5000/foreign_keys=ON（`:84/91/95`）・WAL は SMB で非対応・行識別GUID/changelog/行バージョン/楽観ロックは無し（主キー全て AUTOINCREMENT、`Guid.NewGuid` は `import_view_model.cs:267` の batch_id のみ）・ローカルDB は `Documents\EA_DataCore\db\ea_core.db`（`:19`）・常駐タイマーは `main_view_model.cs:65/135/139` に既存。TO-BE＝ローカルレプリカ＋変更ファイル受け渡し、常駐エージェントは作らずアプリ内タイマー、Phase 0（NAS ロック＋2人目読取専用）/1（row_guid・row_version・changelog・書込層集約）/2（同期本体）。設計書側の宿題4点（列単位マージ不可・row_version 採番・FK 適用順・同期対象の線引き）も記載。**codex レビュー対象**。引用した行番号・事実はすべて実コードで確認済み。 | 追加 |
| 2026-07-16 | v1.3.1 | `EA_CostManager/publish/`<br>`installer_output/`（新規）<br>`00_Project_Docs/EA_CostManager_記録台帳.md` | **インストーラを作成（`EA_CostManager_setup_v1.3.1.exe`・60.9MB）**。①**publish を develop 先端で作り直した**——初回ビルドは `bdffa52` 時点で、その後の `7141766`（台帳MDのみの変更）とコード差分は無かったが、EXE に埋め込まれる ProductVersion が `1.3.1+bdffa52` のままで**成果物とコミットの対応が追えなくなる**ため。作り直し後は `1.3.1+7141766` で develop 先端と一致。②`ISCC.exe`（Inno Setup CLI）で `EA_CostManager_setup.iss` をコンパイル。ExitCode 0。**Inno Setup 6.7.1 は `C:\Users\earth\AppData\Local\Programs\Inno Setup 6\` にユーザー領域インストールされており**、`Program Files` にも HKLM のアンインストール登録にも無い（9章に追記）。③**検証**：インストーラのプロパティ＝ProductName `CostManager` / ProductVersion `1.3.1` / CompanyName `EABASE Series`、同梱 EXE の ProductVersion＝`1.3.1+7141766`、`01_version.txt`＝`1.3.1`。④9章に publish 出力先・インストーラ出力先・Inno Setup のパス・NAS 更新配布先を追記。0章／10章 #2 を「インストーラ作成済み」に更新。**未了**：NAS 配置・main への push・画面での動作確認。⚠️ コンパイル時に `Minimum version is set to 6.1 but using 6.1sp1 is recommended` の警告が出るが、`MinVersion=6.1` は既存設定のため**指示外として変更していない**（要判断）。 | 追加 |
| 2026-07-15 | v1.3.1 | `00_Project_Docs/EA_CostManager_記録台帳.md` | **本台帳を v1.2.1〜v1.3.1 の作業に追随させた（運用ルール #1 の取りこぼしを是正）**。⚠️ **経緯の記録**：上記 v1.2.1〜v1.3.1 の一連の作業（コミット `9f772d2`〜`bdffa52` の7コミット）は、**その都度の反映ができておらず、yori の指摘を受けて事後にまとめて追記した**。原因は、運用ルール #1 が本 md の内部にのみ書かれており、作業者（ジェイ）が本 md を読むまでルールの存在を認識していなかったこと。**恒久対策として `CLAUDE.md` の新設を提案**（10章 #11）。反映内容：0章サマリ（版数・リリース経緯・バージョン表記が2箇所ある注意）／3.2（`ViewStateMigration.cs` 追加・`WorkloadMigration.cs` 113→206）／7.4（`workload_subgroup_links` 追加・`category_id` 廃止・モード2値化・適用先の設計変更）／7.5 新設（表示状態テーブル）／12 更新ログ（本表）。行数はすべて実ファイルで実測。 | 修正 |

### 12.1 v1.2.0 工数表機能（遡及記載・2026-07-15 時点で**未コミット**）

> 本ログ運用の開始前に実装済みだった作業を、実ファイル精査のうえ遡及記載したもの。
> 状態: `git status` で新規14ファイルが `??`、既存4ファイルが `M`。**コミット・ビルド前**。
> コード内コメントのスプリント表記は `Sprint 7A / 7B / 7C-1 / 7C-fix1〜fix5 / 7D-1 / 9A / 9A-fix2 / 9A-fix3` が混在（v1.2.0 がこれらの累積である点は**推測**）。

#### 新規追加ファイル（14本・.cs 合計 3,136行／XAML 含む全体 4,064行）

| 対象ファイル | 行数 | 変更内容 | 種別 |
| --- | --- | --- | --- |
| `Data/WorkloadMigration.cs` | 113 | `WorkloadMigration.migrate(SqliteConnection)`。工数表5テーブル＋インデックス5本を `CREATE TABLE/INDEX IF NOT EXISTS` で冪等作成。既存テーブルへの変更なし。**FOREIGN KEY は未宣言**（参照整合はアプリ側のカスケードDELETEで担保）。 | 追加 |
| `Models/workload_models.cs` | 166 | Dapper 用 POCO 群：`workload_category` / `workload_category_keyword` / `workload_subgroup` / `workload_subgroup_keyword` / `workload_subgroup_mode`（定数 `MODE_COMMON`/`MODE_CUSTOM`/`MODE_NONE`）／集計行 `workload_task_row`／結果 `workload_result`（`get_subgroup_set(category_id)` でモード解決を集計側と共通化）。 | 追加 |
| `Services/WorkloadAggregationService.cs` | 408<br>（→ 2026-07-15 の修正で **426**） | 集計本体 `aggregate_async(int project_id)`。判定基準をダイアログのプレビューと共用するため `normalize_public(string?)` を公開。private に `classify_category` / `classify_subgroup` / `normalize` / `get_rate_any`。 | 追加 |
| `Services/ProjectGroupService.cs` | 174 | 大区分（会社）タブ構築の共通化。`build_groups` は現行 Sprint 9A 仕様で `MAIN_PREFIXES = {"EA","RD","SM"}` 固定＋「その他」（`PREFIX_OTHER`）、先頭は必ず「すべて」、0件区分は非表示、`MIN_COUNT_FOR_GROUP = 3`。`matches(category_code, group_prefix)`。**`build_groups_auto` / `extract_prefix` は Sprint 9B 用で現行フローから未呼出**（grep で呼出なしを確認）。 | 追加 |
| `ViewModels/workload_view_model.cs` | 718<br>（→ 2026-07-15 の修正で **790**） | 工数表ページVM。対象案件抽出・大区分／現場タブ・タブ組み立て（`ensure_initialized_async` / `load_workload_async`、コマンド `reload` / `open_classify` / `add_keyword_from_detail` / `toggle_expand`）。補助クラス `workload_project_item` / `workload_tab_item` / `workload_summary_row` / `workload_detail_row` を同居。 | 追加 |
| `ViewModels/workload_classify_view_model.cs` | 914 | 分類設定ダイアログVM。**保存反映方式**の編集用モデル3種（`edit_category` / `edit_keyword` / `edit_subgroup`）＋VM本体。9コマンド（区分追加/削除/上下移動・キーワード追加/削除・サブ分類追加/削除・他案件コピー）、該当件数プレビュー、`save_async`（1トランザクション）。詳細は下記「実装メモ」。 | 追加 |
| `Views/WorkloadPage.xaml(.cs)` | 87（XAML 482<br>→ `[9D-2]` で **487**） | 工数表ページ本体。`public workload_view_model vm { get; }` を公開。`IsVisibleChanged` で初回表示時のみ `ensure_initialized_async()`。タブ展開（折りたたみ `MaxHeight=90`／展開時無制限・▼▲）は CostPage と同一仕様。 | 追加 |
| `Views/WorkloadClassifyDialog.xaml(.cs)` | 72（XAML 291） | 分類設定ダイアログ（3ペイン：区分一覧／区分名・キーワード／サブ分類モード3択）。`save_click` で `commit_pending_edits()`（`DataGrid.CommitEdit` を Cell→Row の順＋フォーカス中 TextBox の `UpdateSource()`）後に `save_async()`。 | 追加 |
| `Views/WorkloadCopyDialog.xaml(.cs)` | 220（XAML 80） | 他案件から区分をコピー。候補は区分1件以上の案件のみ（自案件除外）、既定で全チェックON。**DB書き込みなし**——選択分は `id=0`（新規）として呼出元の編集中リストへ渡り、分類設定の「保存」で初めて確定。 | 追加 |
| `Views/WorkloadKeywordDialog.xaml(.cs)` | 264（XAML 75） | 明細行の右クリック「この業務内容からキーワードを登録...」から起動。**即時DB反映**（`workload_category_keywords` へ INSERT、同名は `COLLATE NOCASE` で重複回避、`priority = MAX+10`）。新規区分作成時は `workload_categories` へも INSERT（`sort_order = MAX+10` ＝末尾追加で既存判定を邪魔しない）＋区分名を第1キーワード（`priority=10`）登録。全体1トランザクション。 | 追加 |

#### 既存ファイルの改修（4本・+139行/-1行）

| 対象ファイル | 変更内容 | 種別 |
| --- | --- | --- |
| `App.xaml.cs` | +28行。`WorkloadMigration.migrate()` の呼出を2箇所追加：`App.xaml.cs:125`（ローカルDB初期化直後）と `:317`（NAS切替直後）。NAS側は短タイムアウト接続＋失敗時スキップ方式（分類設定は全PCで共有するためNAS DBに保存）。 | 追加 |
| `ViewModels/main_view_model.cs` | +38行。`navigate_to_workload_command`（工数表表示中の再クリックでツリーをトグル、原価集計ツリーは閉じる）、`is_workload_expanded`、`is_workload` を追加。他ページ遷移時は `is_workload_expanded = false`。`current_page` 変更時に `is_workload` を通知。 | 追加 |
| `Views/MainWindow.xaml` | +53行。サイドバーに工数表ボタンと大区分ツリー（`workload_group_list`）を追加。 | 追加 |
| `Views/MainWindow.xaml.cs` | +21行。`workload_group_list.ItemsSource = workload_page.vm.group_items` をコードビハインドで設定（工数表の `group_items` は WorkloadPage 自前VMが持ち、MainWindow の DataContext から XAML だけでは辿れないため）。`workload_group_item_Click` を追加——大区分の選択状態は原価集計側と完全に独立。 | 追加 |
| `.gitignore` | Bin 219→205 bytes の変更あり（内容差分は未確認） | 修正 |

#### 実装メモ（引き継ぎ用・コメントに明記された確定事項）

- **対象案件の判定は2条件の OR**：`projects.agg_mode='task'` **または** `cost_filter_tabs` に `agg_mode='task' AND is_archived=0` が1件以上。旧実装は前者のみで**複製タブ運用時に誤判定**していた（`[9A-fix2]`）。抽出SQL（`workload_view_model:266-277`）は LEFT JOIN ＋ `SELECT DISTINCT` で現場タブの重複を防止。
- **機材費の6時間判定は「1グループ（1人1業務）内」で行う**ため、日単位集計では計上される機材が工数表では計上されない場合がある（業務単位集計タブと同じ挙動・**仕様**）。
- **該当件数プレビューは他区分との優先順位を考慮しない**単純な部分一致カウント。サブ分類も区分で絞り込まず案件全体に対して数えるため、**実際の集計件数はプレビュー値以下**になる（キーワードの効き具合の目安）。
- `visible_subgroups` は `ObservableCollection` に詰め直している。LINQ の `IEnumerable` を返していた頃は DataGrid 編集時に「EditItem は、このビューに対して許可されていません」エラーが出たため（`[7C-fix1]`）。
- 新規区分（`id=0`）には**負の一時ID**（`_temp_id_seq--`）を採番してサブ分類モードを管理。`custom` モードでも未保存の区分には専用サブ分類を追加できない（MessageBox で案内）。
- 保存時、**キーワードは差分管理せず毎回 DELETE→全INSERT**（`priority` は 10 から +10 ずつ再採番）。区分の `sort_order` も画面の並び順で 10 から +10 ずつ再採番。
- 明細のサブ分類グループ見出しは VM 側で小計込み文字列を確定生成（`make_group_header`）。XAML 側での再集計・数値復元を避けるため。
- **命名規約の混在が実在**：プロジェクト全体は `snake_case` だが、`Services`／`Data`／一部 `Views` のクラスは `PascalCase`（`WorkloadAggregationService`, `ProjectGroupService`, `WorkloadMigration`, `WorkloadCopyDialog`）。

---

*END OF DOCUMENT*

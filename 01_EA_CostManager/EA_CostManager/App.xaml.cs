using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Services; // ▼▼▼ [Sprint 5D] 追加 ▼▼▼
using EA_CostManager.Views;    // ▼ 追加（v1.0.2）：SplashWindow / MainWindow を参照するため

namespace EA_CostManager
{
    public partial class App : Application
    {
        private static readonly string LOG_PATH = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "EA_CostManager_error.log");

        // ▼▼▼ 追加（v0.9.6）：アプリ全体のキャンセルトークンソース ▼▼▼
        // UpdateCheckerService 等の fire-and-forget 非同期処理を
        // アプリ終了時に一括キャンセルするために使う。
        // request_shutdown() または OnExit() で Cancel() される。
        private static readonly CancellationTokenSource _app_cts = new();

        /// <summary>
        /// ▼▼▼ 追加（v0.9.6）：アプリ全体のキャンセルトークン ▼▼▼
        /// 他の fire-and-forget 処理から参照できるように公開する
        /// </summary>
        internal static CancellationToken app_token => _app_cts.Token;

        // ▼ 追加（v1.0.2）：スプラッシュウィンドウのインスタンス
        // ・OnStartup の冒頭で生成し、各起動段階で update_status() を呼ぶ
        // ・起動完了時（または致命的エラー時）に Close() する
        // ・null は「未表示／既にCloseされた」状態を意味する
        private static SplashWindow? _splash;

        /// <summary>
        /// ▼▼▼ 追加（v0.9.6）：アプリ終了要求を発行する ▼▼▼
        /// MainWindow.shutdown_async() の冒頭から呼ばれる。
        /// これにより UpdateCheckerService 等の NAS通信が即キャンセルされ、
        /// プロセス終了を妨げない。
        /// </summary>
        public static void request_shutdown()
        {
            try { _app_cts.Cancel(); } catch { /* Dispose 済みなら無視 */ }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += on_dispatcher_exception;
            AppDomain.CurrentDomain.UnhandledException += on_unhandled_exception;
            TaskScheduler.UnobservedTaskException += on_task_exception;

            write_log("=== EA_CostManager 起動開始 ===");
            write_log($"OS: {Environment.OSVersion}");
            write_log($"64bit: {Environment.Is64BitOperatingSystem}");
            write_log($"実行パス: {AppDomain.CurrentDomain.BaseDirectory}");

            try
            {
                base.OnStartup(e);

                // ▼ 追加（v1.0.2）：スプラッシュウィンドウを表示
                // 起動の各段階で update_status() を呼んで進捗をユーザーに見せる
                // これにより「何も起きない」とユーザーが誤認してプロセスを多重起動するのを防ぐ
                _splash = new SplashWindow();
                _splash.Show();
                _splash.update_status("ユーザー設定を読み込み中...");

                // ▼▼▼ ローカルユーザー設定を読み込んでテーマ・フォントを適用 ▼▼▼
                // apply_immediate で全設定（テーマ・フォント・色帯・選択タブ拡大）を一括適用する
                // 今後 UserSettingsManager に設定を追加した場合もここの変更は不要
                write_log("ユーザー設定読み込み開始");
                var user_settings = UserSettingsManager.load();
                UserSettingsManager.apply_immediate(user_settings);
                write_log($"テーマ適用：{user_settings.theme} / フォント：{user_settings.font_size}");

                // ▼ 追加（v1.0.2）：スプラッシュステータス更新
                _splash.update_status("データベースを初期化中...");

                // DB初期化（ローカルDB）
                write_log("DB初期化開始");
                var db_manager = new database_manager();
                db_manager.initialize_database();
                write_log("DB初期化完了");

                // ▼ 追加（v1.0.2）：スプラッシュステータス更新
                _splash.update_status("ローカルDBをマイグレーション中...");

                // V2マイグレーション実行
                write_log("V2マイグレーション開始");
                using (var conn = database_manager.create_connection())
                {
                    V2Migration.RunAsync(conn).GetAwaiter().GetResult();
                }
                write_log("V2マイグレーション完了");

                // ▼▼▼ [Sprint 5D] error_logsテーブルマイグレーション ▼▼▼
                // ※ NAS切替より前に実行すること
                //   理由：NAS切替のcatchでErrorLogServiceを呼ぶため、
                //         その前にローカルDBにテーブルが存在している必要がある
                write_log("ErrorLogマイグレーション開始（ローカルDB）");
                using (var conn = database_manager.create_connection())
                {
                    ErrorLogMigration.RunAsync(conn).GetAwaiter().GetResult();
                }
                write_log("ErrorLogマイグレーション完了（ローカルDB）");

                // ▼▼▼ [Sprint 5E] category_groupsテーブルマイグレーション ▼▼▼
                write_log("CategoryGroupマイグレーション開始（ローカルDB）");
                using (var conn = database_manager.create_connection())
                {
                    CategoryGroupMigration.RunAsync(conn).GetAwaiter().GetResult();
                }
                write_log("CategoryGroupマイグレーション完了（ローカルDB）");

                // ▼▼▼ 追加 [Sprint 7B] workload_*（工数表）テーブルマイグレーション ▼▼▼
                // 工数表機能（業務区分・キーワード・サブ分類）用の5テーブルを作成。
                // CREATE TABLE IF NOT EXISTS のため冪等（既存テーブルへの変更なし）
                write_log("Workloadマイグレーション開始（ローカルDB）");
                using (var conn = database_manager.create_connection())
                {
                    WorkloadMigration.migrate(conn);
                }
                write_log("Workloadマイグレーション完了（ローカルDB）");

                // ▼ 追加（v1.0.2）：スプラッシュステータス更新
                _splash.update_status("NAS接続を確認中...");

                // NAS切り替え
                try
                {
                    write_log("NAS設定確認開始");
                    using var conn_nas = database_manager.create_connection();
                    var settings = conn_nas.Query<(string key, string value)>(
                        "SELECT key, value FROM app_settings WHERE key IN ('nas_enabled', 'nas_path')")
                        .ToDictionary(s => s.key, s => s.value);

                    if (settings.TryGetValue("nas_enabled", out var enabled) && enabled == "1"
                        && settings.TryGetValue("nas_path", out var nas_path)
                        && !string.IsNullOrWhiteSpace(nas_path))
                    {
                        // ▼ 追加（v1.0.2）：スプラッシュステータス更新
                        _splash.update_status("NASに接続中...");

                        write_log($"NAS切り替え開始: {nas_path}");
                        database_manager.switch_to_nas(nas_path);
                        write_log("NAS切り替え完了");

                        // ▼ 追加 [v1.0.2 DB破損ヘッダー検証] NAS切替直後にファイルヘッダーを検証
                        //
                        // 背景：
                        //   後段の WAL チェックポイント + integrity_check は DB を SQLite として
                        //   開けることが前提のため、ヘッダー破損（SQLite Error 26）は検知できない。
                        //   このため、ヘッダーが壊れた NAS DB を読み込もうとしたとき、
                        //   既存の自動復元ルートに乗らず、MainWindow コンストラクタで例外死していた。
                        //   接続前にバイナリレベルで先頭16バイトを検証することで早期検出する。
                        //
                        // 動作仕様（半自動・ユーザー確認後に復元）：
                        //   ヘッダー破損検知
                        //     → スプラッシュ閉鎖（ダイアログが背面に隠れるのを防ぐ）
                        //     → find_latest_backup() で最新バックアップ検索
                        //         ├ バックアップあり → 確認ダイアログ（YesNo・既定Noで誤クリック防止）
                        //         │     ├ Yes → restore_latest_backup() → 再起動
                        //         │     └ No  → Shutdown(0) でアプリ正常終了
                        //         └ バックアップなし → エラーダイアログ → Shutdown(1) で異常終了
                        //
                        // 検証スキップ条件（is_valid_sqlite_header が true を返す）：
                        //   ・ファイルが存在しない（初回起動・新規作成シナリオ）
                        //   ・I/O 例外（アクセス権・ネットワーク問題等は既存の catch に委ねる）
                        write_log("NAS DB ヘッダー検証開始");
                        if (!database_manager.is_valid_sqlite_header(nas_path))
                        {
                            write_log("【警告】NAS DB ヘッダー破損を検知（SQLite フォーマット不一致）");

                            // ダイアログ表示前にスプラッシュを閉じる
                            // （スプラッシュが Topmost のため、ダイアログがその背面に隠れる可能性を排除）
                            try { _splash?.Close(); _splash = null; } catch { /* 既に閉じられていれば無視 */ }

                            // 最新バックアップを検索
                            var latest_backup = database_manager.find_latest_backup();

                            if (latest_backup == null)
                            {
                                // バックアップなし → 手動復旧を案内して終了（Q2: エラー終了）
                                write_log("【エラー】バックアップが見つかりません → 手動復旧が必要");
                                MessageBox.Show(
                                    "NASデータベースの破損を検出いたしましたが、復元用のバックアップが見つかりませんでした。\n\n" +
                                    "お手数ですが、以下のフォルダにバックアップが存在しないかご確認の上、\n" +
                                    "手動でデータベースの復旧を実施してください。\n\n" +
                                    "【NASバックアップフォルダ】\n" +
                                    @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\01_Backups\01_Cost Manager" + "\n\n" +
                                    $"【ログファイル】\n{LOG_PATH}",
                                    "データベース破損エラー",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                                Shutdown(1);
                                return;
                            }

                            // 復元確認ダイアログ表示用の情報を準備
                            string broken_filename = Path.GetFileName(nas_path);
                            string corrupt_backup_name =
                                $"{broken_filename}.broken_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            double backup_size_mb = latest_backup.Length / 1024.0 / 1024.0;

                            // 確認ダイアログ表示（既定ボタンは「いいえ」＝誤クリックで上書きされない）
                            var dialog_result = MessageBox.Show(
                                "NASデータベースファイルの破損を検出いたしました。\n" +
                                "起動を継続するには、最新のバックアップから復元する必要がございます。\n\n" +
                                "【破損ファイル】\n" +
                                $"{broken_filename}\n\n" +
                                "【最新バックアップ】\n" +
                                $"ファイル名:{latest_backup.Name}\n" +
                                $"取得日時 :{latest_backup.LastWriteTime:yyyy年MM月dd日 HH:mm:ss}\n" +
                                $"サイズ   :{backup_size_mb:F2} MB\n\n" +
                                "バックアップから復元してよろしいでしょうか?\n\n" +
                                "※ 復元前に、破損ファイルは下記の名前でリネーム退避されます。\n" +
                                $"   {corrupt_backup_name}\n\n" +
                                "※ 復元後、アプリケーションは自動的に再起動いたします。",
                                "データベース破損を検出いたしました",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning,
                                MessageBoxResult.No);

                            if (dialog_result != MessageBoxResult.Yes)
                            {
                                // ユーザーが復元を拒否 → 正常終了（Q1: アプリ終了）
                                write_log("ユーザーが復元を拒否 → アプリを終了します");
                                Shutdown(0);
                                return;
                            }

                            // 復元実行（既存の restore_latest_backup を呼び出す）
                            // 内部で: 破損DBを .broken_yyyyMMdd_HHmmss にリネーム退避
                            //        → WAL/SHM ファイル削除
                            //        → 最新バックアップを ea_core.db にコピー
                            write_log("ユーザー承認 → バックアップからの復元を実行します");
                            bool restored = database_manager.restore_latest_backup();

                            if (!restored)
                            {
                                // 復元失敗 → 手動復旧を案内して終了
                                write_log("【エラー】バックアップ復元処理に失敗");
                                MessageBox.Show(
                                    "バックアップからの復元処理中にエラーが発生いたしました。\n" +
                                    "お手数ですが、手動でデータベースの復旧を実施してください。\n\n" +
                                    $"【ログファイル】\n{LOG_PATH}",
                                    "復元エラー",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                                Shutdown(1);
                                return;
                            }

                            // 復元成功 → 再起動（Q4: 既存の integrity_check 経路と一貫性）
                            write_log("バックアップからの復元完了 → 再起動します");
                            MessageBox.Show(
                                "バックアップからの復元が完了いたしました。\n" +
                                "アプリケーションを再起動いたします。",
                                "復元完了",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                            app_restart_helper.restart();
                            return;
                        }
                        write_log("NAS DB ヘッダー検証完了");

                        // ▼ 追加（v1.0.2）：スプラッシュステータス更新
                        _splash.update_status("NAS DBをマイグレーション中...");

                        // ▼▼▼ [Sprint 5D] NAS DB にも error_logs テーブルを作成 ▼▼▼
                        // NAS切替後に再度マイグレーションしてNAS DB側にもテーブルを作成する
                        // これにより複数PCからのエラーログがNAS DBに集約される
                        // ▼▼▼ 修正（v0.9.6 レベル1）：短タイムアウト接続に変更 ▼▼▼
                        try
                        {
                            write_log("ErrorLogマイグレーション開始（NAS DB）");
                            using (var conn_nas2 = create_startup_connection())
                            {
                                ErrorLogMigration.RunAsync(conn_nas2).GetAwaiter().GetResult();
                            }
                            write_log("ErrorLogマイグレーション完了（NAS DB）");
                        }
                        catch (Exception ex_mig1)
                        {
                            write_log($"ErrorLogマイグレーションスキップ（NAS DB）: {ex_mig1.Message}");
                        }

                        // ▼▼▼ [Sprint 5E] NAS DBにもcategory_groupsテーブルを作成 ▼▼▼
                        // ▼▼▼ 修正（v0.9.6 レベル1）：短タイムアウト接続に変更 ▼▼▼
                        try
                        {
                            write_log("CategoryGroupマイグレーション開始（NAS DB）");
                            using (var conn_nas3 = create_startup_connection())
                            {
                                CategoryGroupMigration.RunAsync(conn_nas3).GetAwaiter().GetResult();
                            }
                            write_log("CategoryGroupマイグレーション完了（NAS DB）");
                        }
                        catch (Exception ex_mig2)
                        {
                            write_log($"CategoryGroupマイグレーションスキップ（NAS DB）: {ex_mig2.Message}");
                        }

                        // ▼▼▼ 追加 [Sprint 7B] NAS DBにもworkload_*（工数表）テーブルを作成 ▼▼▼
                        // NAS切替後に再度マイグレーションしてNAS DB側にもテーブルを作成する
                        // （分類設定は全PCで共有するためNAS DBに保存される）
                        // 既存Migrationと同じく短タイムアウト接続＋失敗時スキップ方式
                        try
                        {
                            write_log("Workloadマイグレーション開始（NAS DB）");
                            using (var conn_nas_wl = create_startup_connection())
                            {
                                WorkloadMigration.migrate(conn_nas_wl);
                            }
                            write_log("Workloadマイグレーション完了（NAS DB）");
                        }
                        catch (Exception ex_mig_wl)
                        {
                            write_log($"Workloadマイグレーションスキップ（NAS DB）: {ex_mig_wl.Message}");
                        }

                        // ▼▼▼ [現場タブ追跡] NAS DBに current_tab カラムを追加 ▼▼▼
                        // V2Migration はローカルDBにしか走らないため NAS DB には別途追加が必要
                        // ▼▼▼ 修正（v0.9.6 レベル1）：短タイムアウト接続に変更 ▼▼▼
                        write_log("current_tabカラム追加（NAS DB）");
                        try
                        {
                            using (var conn_nas4 = create_startup_connection())
                            {
                                conn_nas4.Execute(
                                    "ALTER TABLE user_sessions ADD COLUMN current_tab TEXT DEFAULT ''");
                            }
                            write_log("current_tabカラム追加完了（NAS DB）");
                        }
                        catch
                        {
                            write_log("current_tabカラムは既に存在（NAS DB）");
                        }

                        // ▼ 追加 [v1.0.1] NAS DB に検索インデックスを作成
                        //   業務テーブル（daily_reports / daily_equipment / daily_transport /
                        //   cost_records / operation_logs）への検索インデックスを追加。
                        //   initialize_database() ではローカルDBにしかINDEXが貼られないため
                        //   NAS切替後の本番運用DB側にも明示的に migrate_indexes を呼ぶ必要がある。
                        //   全て CREATE INDEX IF NOT EXISTS で冪等のため、複数PCから同時に
                        //   呼ばれても安全。各INDEX作成は database_manager 側で個別 try-catch。
                        try
                        {
                            write_log("INDEXマイグレーション開始（NAS DB）");
                            using (var conn_nas5 = create_startup_connection())
                            {
                                database_manager.migrate_indexes(conn_nas5);
                            }
                            write_log("INDEXマイグレーション完了（NAS DB）");
                        }
                        catch (Exception ex_mig3)
                        {
                            write_log($"INDEXマイグレーションスキップ（NAS DB）: {ex_mig3.Message}");
                        }
                    }
                    else
                    {
                        write_log("NAS未使用・ローカルDBで続行");
                    }
                }
                catch (Exception ex)
                {
                    write_log($"NAS設定エラー（ローカルDBで続行）: {ex.Message}");
                    // ▼▼▼ [Sprint 5D] NAS切替失敗をDBログにも記録 ▼▼▼
                    ErrorLogService.log_caught_async("App.OnStartup（NAS切替）", ex)
                        .GetAwaiter().GetResult();
                }

                // ▼ 追加（v1.0.2）：スプラッシュステータス更新
                _splash.update_status("データベースの整合性を確認中...");

                // ▼▼▼ 追加：WALチェックポイントとDB整合性チェック ▼▼▼
                // 異常終了・複数PC同時書き込みによるWALファイルの残留を解消して
                // DBが破損する前に検知してバックアップから自動復元する
                try
                {
                    write_log("WALチェックポイント開始");
                    // ▼▼▼ 修正（v0.9.6 レベル1）：短タイムアウト接続に変更 ▼▼▼
                    using (var conn_chk = create_startup_connection())
                    {
                        // WALファイルをDBに統合してWALをクリアする
                        // TRUNCATE モード：チェックポイント後にWALファイルをゼロバイトにリセット
                        conn_chk.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
                        write_log("WALチェックポイント完了");

                        // DB整合性チェック（通常は "ok" が返る）
                        write_log("DB整合性チェック開始");
                        var integrity = conn_chk.ExecuteScalar<string>("PRAGMA integrity_check;") ?? "";
                        write_log($"DB整合性チェック結果: {integrity}");

                        if (integrity != "ok")
                        {
                            // 破損検知 → 最新バックアップから自動復元を試みる
                            write_log("【警告】DB破損検知 → バックアップから自動復元を試みます");

                            // ▼ 追加（v1.0.2）：DB破損時もスプラッシュを閉じる
                            try { _splash?.Close(); _splash = null; } catch { }

                            bool restored = database_manager.restore_latest_backup();
                            if (restored)
                            {
                                write_log("バックアップからの自動復元完了 → 再起動します");
                                MessageBox.Show(
                                    "データベースの破損を検知したため、最新のバックアップから自動復元しました。\nアプリを再起動します。",
                                    "DB自動復元",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                                app_restart_helper.restart();
                                return;
                            }
                            else
                            {
                                write_log("【エラー】自動復元失敗 → 手動復元が必要です");
                                MessageBox.Show(
                                    "データベースが破損しており、自動復元にも失敗しました。\n設定画面のバックアップ復元から手動で復元してください。",
                                    "DB破損エラー",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    write_log($"WALチェックポイントエラー（続行）: {ex.Message}");
                }

                // ▼ 追加（v1.0.2）：スプラッシュステータス更新
                _splash?.update_status("バックアップを作成中...");

                try
                {
                    write_log("バックアップ作成");
                    database_manager.create_backup();
                }
                catch (Exception ex)
                {
                    write_log($"バックアップスキップ（初回起動等）: {ex.Message}");
                    // ▼▼▼ [Sprint 5D] バックアップ失敗をDBログにも記録 ▼▼▼
                    ErrorLogService.log_caught_async("App.OnStartup（バックアップ）", ex)
                        .GetAwaiter().GetResult();
                }

                // ▼▼▼ [Sprint 6] 1ヶ月以上前のバックアップを自動削除 ▼▼▼
                try
                {
                    write_log("古いバックアップ削除開始");
                    database_manager.cleanup_old_backups();
                    write_log("古いバックアップ削除完了");
                }
                catch (Exception ex)
                {
                    write_log($"バックアップ削除スキップ: {ex.Message}");
                }

                // ▼ 追加（v1.0.2）：スプラッシュステータス更新
                _splash?.update_status("古いログを整理中...");

                // ▼▼▼ [Sprint 5D] 起動時に7日以上前のエラーログを削除 ▼▼▼
                // ▼▼▼ 修正（v0.9.6 レベル1）：5秒タイムアウトを設定 ▼▼▼
                // NAS DB がロック中だとこの処理も30秒待機するため、
                // タイムアウトを設けてスキップ可能にする（翌日再試行で問題なし）
                write_log("古いエラーログ削除開始");
                try
                {
                    var cleanup_task = ErrorLogService.cleanup_old_logs_async();
                    if (await Task.WhenAny(cleanup_task, Task.Delay(5000)) != cleanup_task)
                    {
                        write_log("古いエラーログ削除タイムアウト（スキップ）");
                    }
                    else
                    {
                        write_log("古いエラーログ削除完了");
                    }
                }
                catch (Exception ex)
                {
                    write_log($"古いエラーログ削除スキップ: {ex.Message}");
                }

                // ▼▼▼ [Sprint 6] 起動時バージョンチェック（NAS接続時のみ・サイレント） ▼▼▼
                // 失敗しても起動を妨げないため fire-and-forget で実行
                // ▼▼▼ 修正（v0.9.6）：_app_cts.Token を渡して終了時にキャンセル可能にする ▼▼▼
                _ = check_update_async(_app_cts.Token);

                write_log("=== OnStartup完了 ===");

                // ▼ 追加（v1.0.2）：MainWindow を手動で表示
                // App.xaml の StartupUri を削除したため、ここで明示的に new + Show する
                // これによりスプラッシュ → 各種初期化 → MainWindow の順序が保たれる
                _splash?.update_status("メイン画面を表示中...");
                var main_window = new EA_CostManager.Views.MainWindow();
                main_window.Show();

                // ▼ 追加（v1.0.2）：MainWindow 表示後にスプラッシュを閉じる
                // MainWindow より先に Close すると、最前面ウィンドウが消えてから
                // MainWindow が前面に来るまでに一瞬空白が生じるため、
                // 「Show してから Close」の順序で空白を最小化する
                try { _splash?.Close(); } catch { /* 既に閉じられていれば無視 */ }
                _splash = null;
            }
            catch (Exception ex)
            {
                write_log($"【致命的エラー】OnStartup: {ex}");

                // ▼ 追加（v1.0.2）：致命的エラー時もスプラッシュを確実に閉じる
                // スプラッシュが残ったままだとエラーダイアログの裏に隠れる可能性がある
                try { _splash?.Close(); _splash = null; } catch { }

                MessageBox.Show(
                    $"起動時にエラーが発生しました。\n\nエラー内容：{ex.Message}\n\n" +
                    $"ログファイル：{LOG_PATH}\n\nシステム管理者にログファイルを送付してください。",
                    "起動エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>
        /// ▼▼▼ 追加（v0.9.6）：アプリ終了時の保険クリーンアップ ▼▼▼
        /// Application.Shutdown() が呼ばれた後に発火する。
        /// MainWindow.shutdown_async() で既に request_shutdown() が呼ばれている想定だが、
        /// 万が一呼ばれていないケース（例外等）に備えて二重で Cancel する。
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            // ▼▼▼ 追加（v0.9.6-fix）：終了処理の記録（次回診断用） ▼▼▼
            write_log("=== OnExit開始 ===");
            try { _app_cts.Cancel(); } catch { /* ignore */ }
            try { _app_cts.Dispose(); } catch { /* ignore */ }
            write_log("=== OnExit完了 ===");
            base.OnExit(e);
        }

        private void on_dispatcher_exception(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            write_log($"【UIスレッド例外】{e.Exception}");
            // ▼▼▼ [Sprint 5D] UIスレッド未処理例外をDBログにも記録 ▼▼▼
            ErrorLogService.log_unhandled("Dispatcher.UnhandledException", e.Exception);
            MessageBox.Show(
                $"エラーが発生しました。\n\n{e.Exception.Message}\n\nログ：{LOG_PATH}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void on_unhandled_exception(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            write_log($"【未処理例外】IsTerminating={e.IsTerminating}: {ex}");
            // ▼▼▼ [Sprint 5D] バックグラウンドスレッド未処理例外をDBログにも記録 ▼▼▼
            if (ex != null)
                ErrorLogService.log_unhandled("AppDomain.UnhandledException", ex);
        }

        private void on_task_exception(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            write_log($"【Task例外】{e.Exception}");
            // ▼▼▼ [Sprint 5D] Task未処理例外をDBログにも記録（caught扱い） ▼▼▼
            ErrorLogService.log_caught_async("TaskScheduler.UnobservedTaskException", e.Exception)
                .GetAwaiter().GetResult();
            e.SetObserved();
        }

        // ▼▼▼ Sprint 5A：起動時ユーザー確認 ▼▼▼
        // MACアドレスでpc_usersを検索し、未登録なら初回登録ダイアログを表示
        // 登録完了後にOnStartupでMainWindowを表示する
        private static async Task check_user_async()
        {
            try
            {
                string mac = UserSession.fetch_mac_address();
                string pc = UserSession.fetch_pc_name();

                using var conn = database_manager.create_connection();

                // MACアドレスで既存ユーザーを検索
                var user = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT id, user_name, employee_id, is_admin FROM pc_users WHERE mac_address = @mac",
                    new { mac });

                if (user != null)
                {
                    // 登録済み → そのままログイン
                    UserSession.set(
                        (int)user.id,
                        (string)user.user_name,
                        (int)user.employee_id,
                        (int)user.is_admin >= 2); // 2=管理者

                    // PC名を最新に更新
                    await conn.ExecuteAsync(
                        "UPDATE pc_users SET pc_name = @pc, updated_at = datetime('now','localtime') WHERE mac_address = @mac",
                        new { pc, mac });
                }
                else
                {
                    // 未登録 → ダイアログだけ先に表示（MainWindowはまだ表示しない）
                    var dlg = new EA_CostManager.Views.FirstLoginDialog
                    {
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };

                    bool registered = dlg.ShowDialog() == true;

                    if (!registered)
                    {
                        // 登録キャンセル時はゲストとして続行
                        UserSession.set(0, "ゲスト", 0, false);
                    }
                }
            }
            catch (Exception ex)
            {
                write_log($"ユーザー確認エラー（ゲストで続行）: {ex.Message}");
                UserSession.set(0, "ゲスト", 0, false);
            }
        }

        // ▼▼▼ [Sprint 6] バージョンチェック ▼▼▼
        /// <summary>
        /// NASのversion.txtをチェックしてアップデートがあればMainWindowに通知する
        /// 起動後にバックグラウンドで実行されるためUIをブロックしない
        ///
        /// ▼▼▼ 修正（v0.9.6）：CancellationToken 対応 ▼▼▼
        /// アプリ終了時にキャンセルされて NAS 通信を即中断できるようにする。
        /// これがないと NAS への File.Exists / ReadAllTextAsync が
        /// SMB タイムアウト（最大数十秒）まで居座り、プロセスが終了できなくなる。
        /// </summary>
        private static async Task check_update_async(CancellationToken ct = default)
        {
            try
            {
                write_log($"バージョンチェック開始（現在: v{AppVersionInfo.CURRENT_VERSION}）");

                // ▼▼▼ 修正（v0.9.6）：トークンを渡す ▼▼▼
                string? latest = await EA_CostManager.Services.UpdateCheckerService
                    .get_latest_version_async(ct);

                // ▼▼▼ 追加（v0.9.6）：キャンセル確認 ▼▼▼
                if (ct.IsCancellationRequested) return;

                if (latest == null)
                {
                    write_log("バージョンチェック：NAS未接続またはversion.txt未発見");
                    return;
                }

                write_log($"バージョンチェック：最新 v{latest}");

                if (!AppVersionInfo.is_newer(AppVersionInfo.CURRENT_VERSION, latest))
                {
                    write_log("バージョンチェック：最新版です");
                    return;
                }

                write_log($"バージョンチェック：アップデートあり v{latest}");

                // UIスレッドでMainWindowに通知
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (Current.MainWindow is EA_CostManager.Views.MainWindow mw)
                        mw.show_update_banner(latest);
                });
            }
            catch (OperationCanceledException)
            {
                // ▼▼▼ 追加（v0.9.6）：キャンセルは正常フロー ▼▼▼
                write_log("バージョンチェック：アプリ終了によりキャンセル");
            }
            catch (Exception ex)
            {
                write_log($"バージョンチェックエラー（無視）: {ex.Message}");
            }
        }

        internal static void write_log(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(LOG_PATH, line + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// ▼▼▼ 追加（v0.9.6 レベル1）：起動時専用の短タイムアウト接続 ▼▼▼
        /// 通常の database_manager.create_connection() は busy_timeout=30秒だが、
        /// 起動時のNASマイグレーション処理では他PCがDB使用中だと
        /// 30秒 × 5〜7箇所 = 最大3分の待機が発生して起動できなくなる。
        ///
        /// この接続は busy_timeout=3秒 に設定し、ロック中なら即座に失敗させる。
        /// マイグレーション処理は「テーブル/カラムが既に存在していれば不要」なので
        /// スキップしても機能に影響しない。
        /// </summary>
        private static Microsoft.Data.Sqlite.SqliteConnection create_startup_connection()
        {
            var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                database_manager.get_connection_string());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();

            // 起動時専用：3秒でタイムアウト（通常時の30秒ではなく）
            cmd.CommandText = "PRAGMA busy_timeout=3000;";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();

            return conn;
        }
    }
}
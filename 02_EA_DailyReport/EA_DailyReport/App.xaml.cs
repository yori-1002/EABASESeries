using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Dapper;
using EA_DailyReport.Data;
using EA_DailyReport.Services;

namespace EA_DailyReport
{
    public partial class App : Application
    {
        // ログファイルパス（exeと同フォルダ）
        private static readonly string LOG_PATH = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "EA_DailyReport_error.log");

        // アプリ全体のキャンセルトークンソース
        // fire-and-forget の非同期処理をアプリ終了時に一括キャンセルする
        private static readonly CancellationTokenSource _app_cts = new();

        /// <summary>アプリ全体のキャンセルトークン（外部公開）</summary>
        internal static CancellationToken app_token => _app_cts.Token;

        /// <summary>
        /// アプリ終了要求を発行する
        /// MainWindow.shutdown_async() の冒頭から呼ばれる
        /// これにより NAS通信・同期処理が即キャンセルされプロセス終了を妨げない
        /// </summary>
        public static void request_shutdown()
        {
            try { _app_cts.Cancel(); } catch { /* Dispose済みなら無視 */ }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            // 未処理例外ハンドラーを登録
            DispatcherUnhandledException += on_dispatcher_exception;
            AppDomain.CurrentDomain.UnhandledException += on_unhandled_exception;
            TaskScheduler.UnobservedTaskException += on_task_exception;

            write_log("=== EA_DailyReport 起動開始 ===");
            write_log($"OS: {Environment.OSVersion}");
            write_log($"64bit: {Environment.Is64BitOperatingSystem}");
            write_log($"実行パス: {AppDomain.CurrentDomain.BaseDirectory}");

            try
            {
                base.OnStartup(e);

                // ▼ ユーザー設定読み込み → テーマ・フォントを即時適用
                write_log("ユーザー設定読み込み開始");
                var user_settings = UserSettingsManager.load();
                UserSettingsManager.apply_immediate(user_settings);
                write_log($"テーマ適用：{user_settings.theme} / フォント：{user_settings.font_size}");

                // ▼ 追加：v0.1.7 DB モード判定（本番/テスト）
                // DbConfig（JSON）から読込 → DB 初期化前に apply_db_mode() で適用
                // テストモード時は ea_daily_local_test.db に接続される
                var db_config = database_manager.DbConfig.load();
                database_manager.apply_db_mode(db_config.db_mode);
                write_log($"DB モード: {db_config.db_mode}");

                // ▼ ローカルDB初期化（ea_daily_local.db）
                write_log("ローカルDB初期化開始");
                var db_manager = new database_manager();
                db_manager.initialize_database();
                write_log("ローカルDB初期化完了");

                // ▼ ローカルDB Migration実行
                write_log("DailyReportMigration開始（ローカルDB）");
                using (var conn = database_manager.create_connection())
                {
                    await DailyReportMigration.run_async(conn);
                }
                write_log("DailyReportMigration完了（ローカルDB）");

                // ▼ 修正：v0.1.5
                // seed投入は NAS取得の後ろに移動した（NAS取得成功時は seed 不要）
                // 旧位置にあった「デモ用シードデータ投入」処理は NAS接続処理の後に移動済み

                // ▼ ErrorLog Migration（ローカルDB）
                write_log("ErrorLogMigration開始（ローカルDB）");
                using (var conn = database_manager.create_connection())
                {
                    ErrorLogMigration.RunAsync(conn).GetAwaiter().GetResult();
                }
                write_log("ErrorLogMigration完了（ローカルDB）");

                // ▼ NAS DB接続確認 + 起動時取得（v0.1.5 修正）
                // 旧仕様：current_db_path を NAS に切り替え（switch_to_nas）→ 全操作が NAS 直接
                //   問題：CostManager のスキーマが v0.1.4 と異なるためクラッシュ
                // 新仕様：current_db_path はローカル固定。NAS は読み取りのみ。
                //   起動時に NasDataFetchService がマスタ＋日報をローカルに取得する
                //
                // ▼ 修正：v0.1.7
                // テストモード時は nas_path_test を、本番モード時は nas_path を使用
                try
                {
                    write_log("NAS設定確認開始");
                    using var conn_local = database_manager.create_connection();

                    // モードに応じて取得するキーを切替
                    string nas_path_key = db_config.db_mode == "test"
                        ? "nas_path_test"
                        : "nas_path";

                    var settings = conn_local.Query<(string key, string value)>(
                        $"SELECT key, value FROM app_settings WHERE key IN ('nas_enabled', '{nas_path_key}')")
                        .ToDictionary(s => s.key, s => s.value);

                    if (settings.TryGetValue("nas_enabled", out var enabled) && enabled == "1"
                        && settings.TryGetValue(nas_path_key, out var nas_path)
                        && !string.IsNullOrWhiteSpace(nas_path))
                    {
                        write_log($"NAS接続確認（{db_config.db_mode}モード）: {nas_path}");

                        // ▼ 修正：v0.1.5
                        // database_manager.switch_to_nas() は呼ばない（NAS 直接接続を廃止）
                        // 代わりに NasDataFetchService が必要時のみ NAS を読みに行く

                        // ▼ NAS DBの整合性チェック（読み取り専用接続でチェック）
                        try
                        {
                            write_log("NAS DB整合性チェック開始");
                            using (var conn_chk = database_manager.create_nas_connection(nas_path))
                            {
                                var integrity = conn_chk.ExecuteScalar<string>(
                                    "PRAGMA integrity_check;") ?? "";
                                write_log($"DB整合性チェック結果（NAS DB）: {integrity}");
                                if (integrity != "ok")
                                {
                                    write_log("【警告】NAS DB破損検知 → ローカルDBのみで続行します");
                                    // CostManagerの本番DBに対する自動復元はリスク大のため
                                    // EA_DailyReport 側からは行わない（ログ警告のみ）
                                }
                            }
                        }
                        catch (Exception ex_chk)
                        {
                            write_log($"NAS DB整合性チェック失敗（続行）: {ex_chk.Message}");
                        }

                        // ▼ 追加：v0.1.5 NAS → ローカル取得（起動時の初期データ同期）
                        // マスタ（employees / projects）+ 日報を取得する
                        // updated_at 比較で差分のみ取得（初回起動時は全件取得）
                        try
                        {
                            write_log("NAS → ローカル取得開始（マスタ+日報）");
                            bool fetched = await EA_DailyReport.Services.NasDataFetchService
                                .fetch_all_on_startup_async(nas_path);
                            write_log($"NAS → ローカル取得完了: {(fetched ? "成功" : "失敗（ローカルのみで続行）")}");
                        }
                        catch (Exception ex_fetch)
                        {
                            // 取得失敗してもアプリ起動は継続する
                            write_log($"NAS → ローカル取得エラー（続行）: {ex_fetch.Message}");
                            ErrorLogService.log_caught_async(
                                "App.OnStartup（NAS取得）", ex_fetch)
                                .GetAwaiter().GetResult();
                        }
                    }
                    else
                    {
                        write_log("NAS未設定・ローカルDBのみで続行");
                    }
                }
                catch (Exception ex)
                {
                    write_log($"NAS設定エラー（ローカルDBで続行）: {ex.Message}");
                    ErrorLogService.log_caught_async("App.OnStartup（NAS接続）", ex)
                        .GetAwaiter().GetResult();
                }

                // ▼ 追加：v0.1.5
                // デモ用シードデータ投入（DBが空の場合のみ）
                // NAS取得が成功していれば daily_reports は空ではないため何もしない
                // NAS未接続 or 取得失敗時のフォールバックとして動作する
                try
                {
                    write_log("デモ用シードデータ投入チェック開始");
                    await EA_DailyReport.Services.DailyReportService
                        .seed_demo_data_if_empty_async();
                    write_log("デモ用シードデータ投入完了（または既存データあり）");
                }
                catch (Exception ex_seed)
                {
                    write_log($"シード投入スキップ（無視）: {ex_seed.Message}");
                }

                // ▼ バックアップ作成
                try
                {
                    write_log("バックアップ作成開始");
                    database_manager.create_backup();
                    write_log("バックアップ作成完了");
                }
                catch (Exception ex)
                {
                    write_log($"バックアップスキップ（初回起動等）: {ex.Message}");
                    ErrorLogService.log_caught_async("App.OnStartup（バックアップ）", ex)
                        .GetAwaiter().GetResult();
                }

                // ▼ 古いバックアップを自動削除（1ヶ月以上前）
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

                // ▼ 古いエラーログを削除（7日以上前）
                write_log("古いエラーログ削除開始");
                try
                {
                    var cleanup_task = ErrorLogService.cleanup_old_logs_async();
                    if (await Task.WhenAny(cleanup_task, Task.Delay(5000)) != cleanup_task)
                        write_log("古いエラーログ削除タイムアウト（スキップ）");
                    else
                        write_log("古いエラーログ削除完了");
                }
                catch (Exception ex)
                {
                    write_log($"古いエラーログ削除スキップ: {ex.Message}");
                }

                // ▼ バージョンチェック（fire-and-forget・NAS接続時のみ）
                _ = check_update_async(_app_cts.Token);

                // ▼ ユーザー確認（MACアドレス認証）
                await check_user_async();

                // ▼ 追加：v0.1.7
                // NAS user_sessions に EA_DailyReport のセッションを記録する
                // CostManager と同じテーブルを共有するが、software='EA_DailyReport' で識別
                // → CostManager のセッション一覧と混ざらない
                try
                {
                    write_log("EA_DailyReport セッション登録開始（NAS user_sessions）");
                    await register_session_async();
                    write_log("EA_DailyReport セッション登録完了");
                }
                catch (Exception ex_sess)
                {
                    // セッション登録失敗してもアプリ起動は継続する
                    write_log($"セッション登録エラー（無視）: {ex_sess.Message}");
                }

                // ▼ NasSyncService: バックグラウンド自動同期開始
                NasSyncService.start(_app_cts.Token);
                write_log("NasSyncService開始");

                // ▼ MainWindow表示
                var main_window = new Views.MainWindow();
                main_window.Show();

                write_log("=== OnStartup完了 ===");
            }
            catch (Exception ex)
            {
                write_log($"【致命的エラー】OnStartup: {ex}");
                MessageBox.Show(
                    $"起動時にエラーが発生しました。\n\nエラー内容：{ex.Message}\n\n" +
                    $"ログファイル：{LOG_PATH}\n\nシステム管理者にログファイルを送付してください。",
                    "起動エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>アプリ終了時のクリーンアップ</summary>
        protected override void OnExit(ExitEventArgs e)
        {
            write_log("=== OnExit開始 ===");

            // ▼ 追加：v0.1.7
            // NAS user_sessions の EA_DailyReport セッションを inactive 化する
            // 失敗してもアプリ終了は妨げない（最終的に NAS 側で古いセッションは整理される想定）
            try
            {
                write_log("EA_DailyReport セッション終了処理開始");
                deactivate_session();  // 同期的に呼ぶ（OnExit は async 不可）
                write_log("EA_DailyReport セッション終了処理完了");
            }
            catch (Exception ex_sess)
            {
                write_log($"セッション終了処理エラー（無視）: {ex_sess.Message}");
            }

            try { _app_cts.Cancel(); } catch { /* ignore */ }
            try { _app_cts.Dispose(); } catch { /* ignore */ }
            write_log("=== OnExit完了 ===");
            base.OnExit(e);
        }

        // ────────────────────────────────────────────────
        // ▼ 追加：v0.1.7 NAS user_sessions セッション管理
        // ────────────────────────────────────────────────

        /// <summary>
        /// NAS user_sessions テーブルに EA_DailyReport のセッションを登録する
        /// software='EA_DailyReport' で記録 → CostManager のセッションと識別される
        ///
        /// 既存セッションがあれば status='active' に更新、無ければ新規 INSERT
        /// （MAC アドレス + software の組合せで一意になるよう運用）
        /// </summary>
        private async Task register_session_async()
        {
            // NAS パスを取得
            string nas_path = "";
            try
            {
                using var conn = database_manager.create_connection();
                nas_path = await conn.ExecuteScalarAsync<string>(
                    "SELECT value FROM app_settings WHERE key = 'nas_path'") ?? "";
            }
            catch { /* 取得失敗時は登録スキップ */ }

            if (string.IsNullOrWhiteSpace(nas_path)) return;
            if (!System.IO.File.Exists(nas_path)) return;
            if (string.IsNullOrWhiteSpace(UserSession.user_name)) return;

            try
            {
                using var conn_nas = database_manager.create_nas_connection(nas_path);

                // 既存の EA_DailyReport セッションを検索
                int existing_id = await conn_nas.ExecuteScalarAsync<int>(@"
                    SELECT COALESCE(MIN(id), 0) FROM user_sessions
                    WHERE mac_address = @mac AND software = 'EA_DailyReport'",
                    new { mac = UserSession.mac_address });

                if (existing_id > 0)
                {
                    // 既存を active に更新
                    await conn_nas.ExecuteAsync(@"
                        UPDATE user_sessions SET
                            user_name = @user_name,
                            status = 'active',
                            started_at = datetime('now','localtime'),
                            updated_at = datetime('now','localtime')
                        WHERE id = @id",
                        new { id = existing_id, user_name = UserSession.user_name });
                }
                else
                {
                    // 新規登録
                    await conn_nas.ExecuteAsync(@"
                        INSERT INTO user_sessions
                            (mac_address, user_name, software, status,
                             started_at, updated_at, current_tab)
                        VALUES
                            (@mac, @user_name, 'EA_DailyReport', 'active',
                             datetime('now','localtime'),
                             datetime('now','localtime'),
                             '')",
                        new
                        {
                            mac = UserSession.mac_address,
                            user_name = UserSession.user_name
                        });
                }
            }
            catch (Exception ex)
            {
                write_log($"NAS user_sessions 登録エラー: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 終了時にセッションを inactive 化する
        /// 同期的に実行（OnExit は async 不可のため）・タイムアウト3秒
        /// </summary>
        private void deactivate_session()
        {
            try
            {
                string nas_path;
                using (var conn = database_manager.create_connection())
                {
                    nas_path = conn.ExecuteScalar<string>(
                        "SELECT value FROM app_settings WHERE key = 'nas_path'") ?? "";
                }
                if (string.IsNullOrWhiteSpace(nas_path)) return;
                if (!System.IO.File.Exists(nas_path)) return;

                using var conn_nas = database_manager.create_nas_connection(nas_path);
                conn_nas.Execute(@"
                    UPDATE user_sessions SET
                        status = 'inactive',
                        updated_at = datetime('now','localtime')
                    WHERE mac_address = @mac
                      AND software = 'EA_DailyReport'",
                    new { mac = UserSession.mac_address });
            }
            catch (Exception ex)
            {
                write_log($"deactivate_session エラー: {ex.Message}");
            }
        }

        private void on_dispatcher_exception(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            write_log($"【UIスレッド例外】{e.Exception}");
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
            if (ex != null)
                ErrorLogService.log_unhandled("AppDomain.UnhandledException", ex);
        }

        private void on_task_exception(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            write_log($"【Task例外】{e.Exception}");
            ErrorLogService.log_caught_async("TaskScheduler.UnobservedTaskException", e.Exception)
                .GetAwaiter().GetResult();
            e.SetObserved();
        }

        /// <summary>
        /// 起動時ユーザー確認
        /// MACアドレスでpc_usersを検索し、未登録ならゲストとして続行
        ///
        /// ▼ 修正：FirstLoginDialogをスタブ化（Sprint 1）
        /// FirstLoginDialog はまだ未実装のため、未登録時は自動でゲスト起動する。
        /// 後続Sprintで FirstLoginDialog を実装したら呼び出しを復活させる。
        /// </summary>
        private static async Task check_user_async()
        {
            try
            {
                string mac = UserSession.fetch_mac_address();
                string pc = UserSession.fetch_pc_name();

                using var conn = database_manager.create_connection();

                var user = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT id, user_name, employee_id, is_admin FROM pc_users WHERE mac_address = @mac",
                    new { mac });

                if (user != null)
                {
                    // 登録済み → ログイン
                    UserSession.set(
                        (int)user.id,
                        (string)user.user_name,
                        (int)user.employee_id,
                        (int)user.is_admin >= 2);

                    // PC名を最新に更新
                    await conn.ExecuteAsync(
                        "UPDATE pc_users SET pc_name = @pc, updated_at = datetime('now','localtime') WHERE mac_address = @mac",
                        new { pc, mac });

                    write_log($"ユーザー確認完了: {user.user_name}（is_admin={user.is_admin}）");
                }
                else
                {
                    // ▼ 修正：FirstLoginDialog未実装のためゲスト起動で続行
                    // 後続Sprintで FirstLoginDialog を実装したら以下を復活：
                    //   var dlg = new Views.FirstLoginDialog { ... };
                    //   bool registered = dlg.ShowDialog() == true;
                    //   if (!registered) { UserSession.set(0, "ゲスト", 0, false); }
                    UserSession.set(0, "ゲスト", 0, false);
                    write_log("未登録ユーザー → ゲストで続行（FirstLoginDialog未実装）");
                }
            }
            catch (Exception ex)
            {
                write_log($"ユーザー確認エラー（ゲストで続行）: {ex.Message}");
                UserSession.set(0, "ゲスト", 0, false);
            }
        }

        /// <summary>
        /// バージョンチェック（NASのversion.txtを確認）
        /// fire-and-forget で実行 → UIをブロックしない
        /// </summary>
        private static async Task check_update_async(CancellationToken ct = default)
        {
            try
            {
                write_log($"バージョンチェック開始（現在: v{AppVersionInfo.CURRENT_VERSION}）");

                string? latest = await UpdateCheckerService.get_latest_version_async(ct);

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
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (Current.MainWindow is Views.MainWindow mw)
                        mw.show_update_banner(latest);
                });
            }
            catch (OperationCanceledException)
            {
                write_log("バージョンチェック：アプリ終了によりキャンセル");
            }
            catch (Exception ex)
            {
                write_log($"バージョンチェックエラー（無視）: {ex.Message}");
            }
        }

        /// <summary>ログをファイルに追記する</summary>
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
        /// 起動時専用の短タイムアウト接続（3秒）
        /// 通常の create_connection() は busy_timeout=30秒だが、
        /// 起動時のNAS Migration処理では他PCがDB使用中だと
        /// 30秒 × 複数箇所 = 長時間待機が発生して起動できなくなる。
        /// </summary>
        private static Microsoft.Data.Sqlite.SqliteConnection create_startup_connection()
        {
            var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                database_manager.get_connection_string());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();

            // 起動時専用：3秒でタイムアウト
            cmd.CommandText = "PRAGMA busy_timeout=3000;";
            cmd.ExecuteNonQuery();

            cmd.CommandText = "PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();

            return conn;
        }
    }
}
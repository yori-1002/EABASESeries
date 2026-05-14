using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using EA_DailyReport.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace EA_DailyReport.Views.Pages
{
    /// <summary>
    /// 設定画面（v0.1.7 新規）
    ///
    /// 機能：
    ///   ・DB モード切替（本番 / テスト）
    ///   ・本番 NAS DB パス・テスト DB パスの設定
    ///   ・接続テスト（指定パスに対して SQLite 接続を試行）
    ///   ・「💾 保存して再起動」で設定保存 → アプリ再起動
    ///
    /// 設定保管：
    ///   ・db_mode：%LOCALAPPDATA%\01_EABASE Series\02_DailyReport\db_config.json
    ///     （DB 初期化前に読む必要があるため JSON ファイル）
    ///   ・nas_path / nas_path_test：ローカル DB の app_settings テーブル
    /// </summary>
    public partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
            Loaded += on_loaded;
        }

        // ────────────────────────────────────────────────
        // 初期化
        // ────────────────────────────────────────────────

        private async void on_loaded(object sender, RoutedEventArgs e)
        {
            // 現在の設定値をロードして画面に反映
            try
            {
                // ① DB モード（JSON から取得）
                var cfg = database_manager.DbConfig.load();
                if (cfg.db_mode == "test")
                    rad_mode_test.IsChecked = true;
                else
                    rad_mode_production.IsChecked = true;

                // ② 本番 / テスト NAS DB パス（app_settings から取得）
                using var conn = database_manager.create_connection();

                string prod_path = await conn.ExecuteScalarAsync<string>(
                    "SELECT value FROM app_settings WHERE key = 'nas_path'") ?? "";
                txt_nas_path_production.Text = prod_path;

                string test_path = await conn.ExecuteScalarAsync<string>(
                    "SELECT value FROM app_settings WHERE key = 'nas_path_test'") ?? "";
                txt_nas_path_test.Text = test_path;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"設定の読込に失敗しました。\n\n{ex.Message}",
                    "読込エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        // ────────────────────────────────────────────────
        // 「参照…」ボタン
        // ────────────────────────────────────────────────

        /// <summary>本番 NAS DB パスをファイル選択ダイアログで選ぶ</summary>
        private void btn_browse_production_Click(object sender, RoutedEventArgs e)
        {
            string? selected = browse_db_file(txt_nas_path_production.Text);
            if (selected != null) txt_nas_path_production.Text = selected;
        }

        /// <summary>テスト DB パスをファイル選択ダイアログで選ぶ</summary>
        private void btn_browse_test_Click(object sender, RoutedEventArgs e)
        {
            string? selected = browse_db_file(txt_nas_path_test.Text);
            if (selected != null) txt_nas_path_test.Text = selected;
        }

        /// <summary>SQLite DB ファイルを選択するダイアログを表示する</summary>
        private string? browse_db_file(string current_path)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "SQLite DB (*.db)|*.db|All files (*.*)|*.*",
                Title = "DB ファイルを選択",
            };

            // 現在のパスから初期ディレクトリを設定
            if (!string.IsNullOrWhiteSpace(current_path))
            {
                try
                {
                    string? dir = Path.GetDirectoryName(current_path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                }
                catch { /* 無視 */ }
            }

            if (dlg.ShowDialog() == true)
                return dlg.FileName;
            return null;
        }

        // ────────────────────────────────────────────────
        // 「🔌 接続テスト」ボタン
        // ────────────────────────────────────────────────

        /// <summary>
        /// 現在選択中のモードに応じた DB パスへの接続テスト
        /// 1. ファイル存在確認
        /// 2. SQLite 接続テスト
        /// 3. テーブル存在確認（daily_reports / employees）
        /// </summary>
        private async void btn_test_connection_Click(object sender, RoutedEventArgs e)
        {
            string target_path;
            string mode_label;
            if (rad_mode_test.IsChecked == true)
            {
                target_path = txt_nas_path_test.Text?.Trim() ?? "";
                mode_label = "テストモード";
            }
            else
            {
                target_path = txt_nas_path_production.Text?.Trim() ?? "";
                mode_label = "本番モード";
            }

            if (string.IsNullOrWhiteSpace(target_path))
            {
                MessageBox.Show(
                    "DB パスが入力されていません。",
                    "入力エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                btn_test_connection.IsEnabled = false;
                btn_test_connection.Content = "🔌 接続中...";

                var (ok, msg) = await Task.Run(() => test_db_connection(target_path));

                if (ok)
                {
                    MessageBox.Show(
                        $"✅ 接続成功（{mode_label}）\n\n{msg}",
                        "接続テスト結果",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"❌ 接続失敗（{mode_label}）\n\n{msg}",
                        "接続テスト結果",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"接続テスト中にエラーが発生しました。\n\n{ex.Message}",
                    "エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                btn_test_connection.IsEnabled = true;
                btn_test_connection.Content = "🔌 接続テスト";
            }
        }

        /// <summary>
        /// 指定パスへの SQLite 接続テスト
        /// 戻り値：(成功か, メッセージ)
        /// </summary>
        private static (bool ok, string msg) test_db_connection(string path)
        {
            // ① ファイル存在
            if (!File.Exists(path))
                return (false, $"ファイルが存在しません：\n{path}");

            // ② SQLite 接続
            try
            {
                using var conn = new SqliteConnection(
                    $"Data Source={path};Mode=ReadOnly;");
                conn.Open();

                // ③ daily_reports テーブル存在確認
                int dr_count = conn.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='daily_reports'");
                if (dr_count == 0)
                    return (false, "daily_reports テーブルが見つかりません。\nDB ファイルが正しいか確認してください。");

                // ④ レコード件数取得（接続が機能していることの確認）
                int rec_count = conn.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM daily_reports");

                return (true,
                    $"パス: {path}\n" +
                    $"daily_reports: {rec_count}件");
            }
            catch (Exception ex)
            {
                return (false, $"SQLite 接続エラー：\n{ex.Message}");
            }
        }

        // ────────────────────────────────────────────────
        // 「💾 保存して再起動」ボタン
        // ────────────────────────────────────────────────

        /// <summary>
        /// 設定を保存してアプリを再起動する
        /// 1. db_mode を db_config.json に保存
        /// 2. nas_path / nas_path_test を app_settings に保存
        /// 3. 再起動確認 → app_restart_helper.restart() で再起動
        /// </summary>
        private async void btn_save_and_restart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // ① 入力検証
                string prod_path = txt_nas_path_production.Text?.Trim() ?? "";
                string test_path = txt_nas_path_test.Text?.Trim() ?? "";
                string new_mode = rad_mode_test.IsChecked == true ? "test" : "production";

                // 選択中のモードのパスは必須
                string required_path = new_mode == "test" ? test_path : prod_path;
                if (string.IsNullOrWhiteSpace(required_path))
                {
                    MessageBox.Show(
                        $"{(new_mode == "test" ? "テスト" : "本番")} DB パスを入力してください。",
                        "入力エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // ② 確認ダイアログ
                var result = MessageBox.Show(
                    $"以下の設定を保存して再起動します。\n\n" +
                    $"DB モード: {(new_mode == "test" ? "テストモード" : "本番モード")}\n" +
                    $"本番パス: {prod_path}\n" +
                    $"テストパス: {test_path}\n\n" +
                    "再起動してよろしいですか？",
                    "保存して再起動",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.OK) return;

                // ③ db_mode を JSON に保存
                database_manager.DbConfig.save(
                    new database_manager.DbConfig.Config { db_mode = new_mode });

                // ④ NAS パスを app_settings に保存
                using (var conn = database_manager.create_connection())
                {
                    await conn.ExecuteAsync(@"
                        INSERT OR REPLACE INTO app_settings (key, value, updated_at)
                        VALUES ('nas_path', @v, datetime('now','localtime'))",
                        new { v = prod_path });

                    await conn.ExecuteAsync(@"
                        INSERT OR REPLACE INTO app_settings (key, value, updated_at)
                        VALUES ('nas_path_test', @v, datetime('now','localtime'))",
                        new { v = test_path });
                }

                // ⑤ 再起動
                // app_restart_helper.restart() は CostManager と共通の再起動ヘルパー
                // ※ EA_DailyReport プロジェクトに app_restart_helper クラスが存在する想定
                try_restart_app();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"保存に失敗しました。\n\n{ex.Message}",
                    "保存エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// アプリ再起動を試みる
        /// app_restart_helper クラスが存在すればそれを使用
        /// 存在しなければ手動で Process.Start + Shutdown
        /// </summary>
        private static void try_restart_app()
        {
            try
            {
                // app_restart_helper.restart() を呼ぶ（CostManager 共通の再起動ヘルパー）
                // ※ namespace は EA_DailyReport を想定（CostManager から流用想定）
                var t = Type.GetType("EA_DailyReport.app_restart_helper, EA_DailyReport");
                var m = t?.GetMethod("restart",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (m != null)
                {
                    m.Invoke(null, null);
                    return;
                }
            }
            catch
            {
                // app_restart_helper 失敗時は次のフォールバック
            }

            // フォールバック：手動で再起動
            try
            {
                string? exe_path = System.Diagnostics.Process.GetCurrentProcess()
                    .MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe_path))
                {
                    System.Diagnostics.Process.Start(exe_path);
                }
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "再起動に失敗しました。手動でアプリを再起動してください。\n\n" + ex.Message,
                    "再起動エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}

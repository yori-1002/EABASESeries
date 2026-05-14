using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace EA_DailyReport
{
    public class UserSettings
    {
        // カラーテーマ: "cyan"=水色(デフォルト) / "blue"=濃い青 / "green"=緑 / "dark"=ダーク
        // ★即時反映
        public string theme { get; set; } = "cyan";

        // フォントサイズ: "small"=小 / "medium"=中(デフォルト) / "large"=大
        // ★即時反映
        public string font_size { get; set; } = "medium";

        // 起動時のページ: "dashboard"=ダッシュボード(デフォルト) / "cost"=原価集計
        // ★次回起動時に反映
        public string startup_page { get; set; } = "dashboard";

        // 現場タブの色帯の幅(px): 4=細 / 6=普通(デフォルト) / 10=太
        // ★即時反映
        public int tab_color_bar_width { get; set; } = 6;

        // 大区分の選択を記憶する
        // ★次回起動時に反映
        public bool remember_group { get; set; } = false;

        // 最後に選択していた大区分
        public string last_group_prefix { get; set; } = "";

        // 選択タブの拡大表示（true=拡大あり（デフォルト）/ false=拡大なし）★即時反映
        public bool tab_enlarge_on_select { get; set; } = true;

        // ▼ 追加：v0.1.4 設計書 10.3
        // ウィンドウ状態（前回終了時の状態を再現するためWindowsアプリ標準挙動として保持）
        // -1 を未設定扱いとする（初回起動時はOSデフォルト位置に配置）

        /// <summary>ウィンドウ状態："Normal" / "Maximized"</summary>
        public string window_state { get; set; } = "Normal";

        /// <summary>ウィンドウ幅（px）。-1 = 未設定</summary>
        public double window_width { get; set; } = -1;

        /// <summary>ウィンドウ高さ（px）。-1 = 未設定</summary>
        public double window_height { get; set; } = -1;

        /// <summary>ウィンドウ Top 座標（px）。-1 = 未設定</summary>
        public double window_top { get; set; } = -1;

        /// <summary>ウィンドウ Left 座標（px）。-1 = 未設定</summary>
        public double window_left { get; set; } = -1;
    }

    public static class UserSettingsManager
    {
        // ▼ 修正：v0.1.4 設計書 9 章・10.1
        // 保存先を %LOCALAPPDATA%\01_EABASE Series\02_DailyReport\ に変更
        // 旧仕様（レジストリ参照 → exe隣フォールバック）は廃止
        // 理由：exe隣（Program Files 配下）は Inno Setup インストール後に
        //       Windows 保護領域となり書き込み拒否されるため（10.1）
        private const string EABASE_DIR_NAME = "01_EABASE Series";
        private const string APP_DIR_NAME = "02_DailyReport";
        private const string SETTINGS_FILE_NAME = "user_settings.json";

        private static readonly string SETTINGS_PATH = resolve_settings_path();

        /// <summary>
        /// ▼ 修正：v0.1.4
        /// 設定ファイルパスを解決する
        /// %LOCALAPPDATA%\01_EABASE Series\02_DailyReport\user_settings.json
        /// 旧仕様（レジストリ参照 → exe隣フォールバック）から完全置換
        /// </summary>
        private static string resolve_settings_path()
        {
            string local_app_data = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            string app_dir = Path.Combine(local_app_data, EABASE_DIR_NAME, APP_DIR_NAME);
            return Path.Combine(app_dir, SETTINGS_FILE_NAME);
        }

        /// <summary>
        /// ▼ 追加：v0.1.4 設計書 10.2
        /// 旧パス（exe隣）から新パス（%LOCALAPPDATA%）への設定ファイルマイグレーション
        /// 起動時に1回だけ実行する。新パスに既にあれば何もしない。
        /// 旧ファイルは削除しない（権限不足の例外回避）
        /// App.xaml.cs の起動フロー④で呼び出すこと
        /// </summary>
        public static void migrate_old_path()
        {
            try
            {
                // 新パスに既にあるなら何もしない（マイグレーション完了済み）
                if (File.Exists(SETTINGS_PATH))
                    return;

                // 旧パス候補：exe同フォルダ
                string old_path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, SETTINGS_FILE_NAME);

                if (!File.Exists(old_path))
                    return;

                // 新パスのフォルダを作成
                string? new_dir = Path.GetDirectoryName(SETTINGS_PATH);
                if (!string.IsNullOrEmpty(new_dir) && !Directory.Exists(new_dir))
                    Directory.CreateDirectory(new_dir);

                // コピー（旧ファイルは削除しない）
                // overwrite: false で新パスに既存ファイルがある場合は上書きしない安全側に倒す
                File.Copy(old_path, SETTINGS_PATH, overwrite: false);
            }
            catch (Exception ex)
            {
                // マイグレーション失敗はアプリ動作を妨げない（新規作成扱いになる）
                System.Diagnostics.Debug.WriteLine(
                    $"設定ファイルマイグレーション失敗（無視）: {ex.Message}");
            }
        }

        private static UserSettings? _cache;

        public static UserSettings current => _cache ??= load();

        public static UserSettings load()
        {
            try
            {
                if (File.Exists(SETTINGS_PATH))
                {
                    string json = File.ReadAllText(SETTINGS_PATH);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json);
                    _cache = settings ?? new UserSettings();
                    return _cache;
                }
            }
            catch { }
            _cache = new UserSettings();
            return _cache;
        }

        public static void save(UserSettings settings)
        {
            try
            {
                // 保存先フォルダが存在しない場合は作成
                string? dir = Path.GetDirectoryName(SETTINGS_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(SETTINGS_PATH, json);
                _cache = settings;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"設定の保存に失敗しました。\n{ex.Message}",
                    "保存エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        public static void clear_cache() => _cache = null;

        /// <summary>
        /// 即時反映できる設定をアプリに適用する
        /// カラーテーマ・フォントサイズ・色帯の幅・選択タブ拡大
        /// </summary>
        public static void apply_immediate(UserSettings settings)
        {
            ThemeManager.apply(settings);
            // 色帯の幅をResourcesに反映（CostPage.xamlのRectangleがバインドで参照）
            Application.Current.Resources["tab_color_bar_width"] = (double)settings.tab_color_bar_width;
            // 選択タブの拡大設定をResourcesに反映
            // true  → リソースを削除：DynamicResourceがデフォルト値に戻りWPFデフォルト拡大挙動が復活
            // false → Thickness(0) をセット：選択タブのMarginを均一化して拡大を抑制
            if (settings.tab_enlarge_on_select)
                Application.Current.Resources.Remove("tab_item_selected_margin");
            else
                Application.Current.Resources["tab_item_selected_margin"] =
                    new System.Windows.Thickness(0);
        }
    }
}
using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace EA_CostManager
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

        // ▼▼▼ 追加：選択タブの拡大表示（true=拡大あり（デフォルト）/ false=拡大なし）★即時反映 ▼▼▼
        public bool tab_enlarge_on_select { get; set; } = true;

        // ▼ 追加 [v1.0.1] 起動時のウィンドウ状態を記憶する（普通のWindowsアプリと同じ挙動）
        //   "Maximized" = 最大化 / "Normal" = 通常サイズ
        //   Minimizedは記憶せずNormalに戻す（最小化したまま閉じても次回は通常で開く）
        //   ★次回起動時に反映
        public string window_state { get; set; } = "Maximized";

        // ▼ 追加 [v1.0.1] Normal時のウィンドウサイズ・位置
        //   起動時に window_state == "Normal" の場合のみ適用される
        //   0以下は「未設定（OS既定にまかせる）」とみなして無視する
        //   top/left は -1 を「未設定」とみなす（マルチモニター環境で 0,0 が有効座標になりうるため）
        public double window_width { get; set; } = 0;
        public double window_height { get; set; } = 0;
        public double window_top { get; set; } = -1;
        public double window_left { get; set; } = -1;
    }

    public static class UserSettingsManager
    {
        // ▼ 修正 [v1.0.1] JSON保存先の方針を全面変更
        //   旧版（v1.0.0以前）の問題：
        //     exe隣（Program Files (x86)\EABASE Series\EA_CostManager\）に保存していたが、
        //     Inno Setup インストーラー導入後、Program Files 配下が
        //     Windowsの保護領域となるため通常権限では書き込み拒否される。
        //   新方式（v1.0.1）：
        //     %LOCALAPPDATA% 配下に保存。
        //     親フォルダ「01_EABASE Series」はNAS命名規約と統一する
        //     （将来の DailyReport 等も同じ親フォルダに同居予定）。
        //   ▼ 削除：レジストリ参照ロジック（HKLM\SOFTWARE\EA_CostManager\InstallDir）
        //     インストール先（Program Files）に書こうとする設計自体が破綻していたため不要。

        // 親フォルダ名（NAS命名規約と統一・変更時はDailyReportとも合わせること）
        private const string EABASE_FOLDER_NAME = "01_EABASE Series";
        // ソフト個別フォルダ名（NAS命名規約と統一）
        private const string SOFTWARE_FOLDER_NAME = "01_CostManager";
        // 設定ファイル名
        private const string SETTINGS_FILE_NAME = "user_settings.json";

        // 新しい保存先：
        //   C:\Users\{user}\AppData\Local\01_EABASE Series\01_CostManager\user_settings.json
        private static readonly string SETTINGS_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            EABASE_FOLDER_NAME,
            SOFTWARE_FOLDER_NAME,
            SETTINGS_FILE_NAME);

        // 旧保存先（v1.0.0以前のexe隣・マイグレーション元としてのみ参照）
        private static readonly string OLD_SETTINGS_PATH = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            SETTINGS_FILE_NAME);

        // マイグレーションを起動時1回だけ実行するためのフラグ
        private static bool _migration_checked = false;

        private static UserSettings? _cache;

        public static UserSettings current => _cache ??= load();

        public static UserSettings load()
        {
            // ▼ 追加 [v1.0.1] 旧パス→新パスのマイグレーション処理
            //   起動時1回だけチェック。新パスに未存在で旧パスに存在すればコピー。
            //   旧ファイルは削除しない（Program Files 配下の場合、削除に管理者権限が必要なため）。
            try_migrate_from_old_path();

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
            catch (Exception ex)
            {
                // ▼ 修正 [v1.0.3] catch{} の握りつぶしを廃止
                //   旧実装は例外を完全に隠していたため、JSON破損などで読み込み失敗しても
                //   サイレントにデフォルト値で復帰していた。これがバグ①「保存して再起動で
                //   接続できない」の根本原因と推定される（破損JSONがデフォルト値に置き換わり、
                //   NASパス設定が失われてローカルDBで起動するため、ユーザーから見ると
                //   業務データが消えたように見える）。
                //   v1.0.3 では例外内容をDebug出力し、破損ファイルを退避して原因追跡可能にする。
                System.Diagnostics.Debug.WriteLine(
                    $"[UserSettingsManager] load 失敗: {ex.GetType().Name} : {ex.Message}");
                try_quarantine_broken_settings(ex);
            }
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

                // ▼ 修正 [v1.0.3] File.WriteAllText → FileStream.Flush(true) で
                //   OSバッファまで強制書き込みする方式に変更。
                //   旧実装は OS のディスクキャッシュにとどまる可能性があり、
                //   この直後にアプリが再起動されると JSON が中途半端な状態でディスクに残り、
                //   次回起動時の load() で JSON 破損として検出される問題があった。
                //   FileStream.Flush(true) は flushToDisk=true でOSバッファをフラッシュし、
                //   再起動シーケンスに対する堅牢性を確保する。
                using (var fs = new FileStream(SETTINGS_PATH, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(true);  // ★ OSバッファまで強制書き込み
                }

                _cache = settings;
            }
            catch (Exception ex)
            {
                // ▼ 修正 [v1.0.3] 例外をDebug出力に記録（旧実装はMessageBoxのみで
                //   ログには残らなかった。再現性の追跡を可能にする）
                System.Diagnostics.Debug.WriteLine(
                    $"[UserSettingsManager] save 失敗: {ex.GetType().Name} : {ex.Message}");

                MessageBox.Show(
                    $"設定の保存に失敗しました。\n{ex.Message}",
                    "保存エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        public static void clear_cache() => _cache = null;

        // ▼ 追加 [v1.0.3] 破損した設定ファイルを退避するヘルパー
        //   load() でJSON破損などの例外が発生したとき、破損ファイルを
        //   .broken_yyyyMMdd_HHmmss にリネームして退避する。
        //   これにより：
        //   ・次回起動時に空の状態（デフォルト値）から再構築されるが、
        //   ・退避ファイルは後から手動で復旧可能（中身を確認・修復してリネーム）
        //   ・サポート時の調査資料としても残せる
        //   退避自体に失敗してもアプリは続行する（致命的ではない）。
        private static void try_quarantine_broken_settings(Exception originalEx)
        {
            try
            {
                if (!File.Exists(SETTINGS_PATH)) return;

                string quarantine_path = SETTINGS_PATH +
                    ".broken_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

                File.Move(SETTINGS_PATH, quarantine_path);

                System.Diagnostics.Debug.WriteLine(
                    $"[UserSettingsManager] 破損設定ファイルを退避: {quarantine_path} (元例外: {originalEx.GetType().Name})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[UserSettingsManager] 破損設定ファイル退避に失敗: {ex.GetType().Name} : {ex.Message}");
            }
        }

        // ▼ 追加 [v1.0.1] 旧パス→新パスへのマイグレーション処理
        //   v1.0.0以前は exe隣（Program Files (x86) 配下）に保存していたため、
        //   旧バージョンのインストール環境を引き継ぐ際、
        //   旧パスに user_settings.json が残っている可能性がある。
        //   その場合、初回起動時に新パスへコピーして設定を引き継ぐ。
        //   失敗しても致命的ではない（新規インストール扱いになるだけ）。
        private static void try_migrate_from_old_path()
        {
            // 起動時1回だけ実行
            if (_migration_checked) return;
            _migration_checked = true;

            try
            {
                // 新パスにすでに設定ファイルがあれば、マイグレーション不要
                if (File.Exists(SETTINGS_PATH)) return;
                // 旧パスに設定ファイルが無ければ、マイグレーション対象なし
                if (!File.Exists(OLD_SETTINGS_PATH)) return;

                // 新パスのフォルダを作成
                string? dir = Path.GetDirectoryName(SETTINGS_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // 旧→新へコピー（旧ファイルは削除しない・権限不足で例外になる可能性があるため）
                File.Copy(OLD_SETTINGS_PATH, SETTINGS_PATH, overwrite: false);

                System.Diagnostics.Debug.WriteLine(
                    $"[UserSettingsManager] マイグレーション完了: {OLD_SETTINGS_PATH} → {SETTINGS_PATH}");
            }
            catch (Exception ex)
            {
                // マイグレーション失敗は致命的ではない（新規インストール扱いになるだけ）
                System.Diagnostics.Debug.WriteLine(
                    $"[UserSettingsManager] マイグレーション失敗: {ex.GetType().Name} : {ex.Message}");
            }
        }

        /// <summary>
        /// 即時反映できる設定をアプリに適用する
        /// カラーテーマ・フォントサイズ・色帯の幅・選択タブ拡大
        /// </summary>
        public static void apply_immediate(UserSettings settings)
        {
            ThemeManager.apply(settings);
            // 色帯の幅をResourcesに反映（CostPage.xamlのRectangleがバインドで参照）
            Application.Current.Resources["tab_color_bar_width"] = (double)settings.tab_color_bar_width;
            // ▼▼▼ 追加：選択タブの拡大設定をResourcesに反映 ▼▼▼
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
using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.ViewModels
{
    public class settings_nav_item
    {
        public string label { get; set; } = "";
        public string key { get; set; } = "";
        public int indent { get; set; } = 0;
        public bool is_header { get; set; } = false;
    }

    /// <summary>
    /// 設定画面 ViewModel
    /// DB設定はDBに保存、詳細設定はローカルJSONに保存（PC個別設定）
    /// </summary>
    public class settings_view_model : base_view_model
    {
        // ===== ログインユーザー情報（ヘッダー表示用）=====
        public string current_user_name => EA_CostManager.UserSession.user_name;
        public string current_user_role => EA_CostManager.UserSession.is_admin ? "管理者" :
                                           EA_CostManager.UserSession.user_id > 0 ? "一般" : "ゲスト";

        // 管理者のみ表示するUI要素の制御
        public bool is_admin_visible => EA_CostManager.UserSession.is_admin;

        // 管理メニューの展開・折りたたみ
        private bool _is_admin_menu_expanded = false;
        public bool is_admin_menu_expanded
        {
            get => _is_admin_menu_expanded;
            set
            {
                if (SetProperty(ref _is_admin_menu_expanded, value))
                {
                    OnPropertyChanged(nameof(is_admin_menu_and_visible));
                    OnPropertyChanged(nameof(is_master_and_admin_visible));
                }
            }
        }
        public bool is_admin_menu_and_visible => EA_CostManager.UserSession.is_admin && _is_admin_menu_expanded;

        // マスタ管理サブナビ：管理者かつ管理メニュー展開中かつマスタ展開中のとき表示
        public bool is_master_and_admin_visible => EA_CostManager.UserSession.is_admin && _is_admin_menu_expanded && is_master_expanded;

        // ===== 左ツリーナビゲーション =====

        public ObservableCollection<settings_nav_item> nav_items { get; } = new();

        private settings_nav_item? _selected_nav;
        public settings_nav_item? selected_nav
        {
            get => _selected_nav;
            set
            {
                if (value?.is_header == true) return;

                // ▼▼▼ 権限チェック：管理者専用ページは非管理者がクリックしたらブロック ▼▼▼
                // 管理メニュー内の項目は全て管理者専用
                var admin_only_keys = new[] { "users", "amount", "employees", "vehicles", "equipment", "legacy", "tab_sort_reset", "category_group", "backup_restore" }; // ▼ [Sprint 5F] backup_restore追加
                if (value != null && System.Array.Exists(admin_only_keys, k => k == value.key)
                    && !EA_CostManager.UserSession.is_admin)
                {
                    System.Windows.MessageBox.Show(
                        "この機能は管理者権限が必要です。\n\n管理者に変更を依頼してください。",
                        "権限エラー",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return; // 選択を変更しない
                }

                if (SetProperty(ref _selected_nav, value))
                {
                    switch_content(value?.key ?? "");
                    OnPropertyChanged(nameof(is_advanced_page));
                    OnPropertyChanged(nameof(is_users_page));
                    OnPropertyChanged(nameof(is_error_log_page)); // ▼ [Sprint 5D] 追加
                    OnPropertyChanged(nameof(is_category_group_page)); // ▼ [Sprint 5E] 追加
                    OnPropertyChanged(nameof(is_backup_restore_page)); // ▼ [Sprint 5F] 追加
                }
            }
        }

        public bool is_advanced_page => _selected_nav?.key == "advanced";
        public bool is_users_page => _selected_nav?.key == "users";
        // ▼▼▼ [Sprint 5D] 追加：エラーログページ判定フラグ ▼▼▼
        public bool is_error_log_page => _selected_nav?.key == "error_log";
        // ▼▼▼ [Sprint 5E] 追加：区分結合設定ページ判定フラグ ▼▼▼
        public bool is_category_group_page => _selected_nav?.key == "category_group";
        // ▼▼▼ [Sprint 5F] 追加：バックアップ復元ページ判定フラグ ▼▼▼
        public bool is_backup_restore_page => _selected_nav?.key == "backup_restore";

        // ===== 右コンテンツ =====

        private object? _current_content;
        public object? current_content
        {
            get => _current_content;
            private set => SetProperty(ref _current_content, value);
        }

        // ===== 各サブVM（遅延初期化） =====

        private master_employees_view_model? _employees_vm;
        private master_vehicles_view_model? _vehicles_vm;
        private master_equipment_view_model? _equipment_vm;
        private master_legacy_name_view_model? _legacy_vm;
        private master_amount_view_model? _amount_vm;
        private master_archived_projects_view_model? _archived_vm;
        private EA_CostManager.ViewModels.user_management_view_model? _users_vm;

        // ▼▼▼ [Sprint 5D] 追加：エラーログVM（XAML側から直接バインド） ▼▼▼
        private ErrorLogViewModel? _error_log_vm_instance;
        public ErrorLogViewModel error_log_vm
        {
            get
            {
                if (_error_log_vm_instance == null)
                    _error_log_vm_instance = new ErrorLogViewModel();
                return _error_log_vm_instance;
            }
        }

        // ▼▼▼ [Sprint 5E] 追加：区分結合設定VM ▼▼▼
        private category_group_view_model? _category_group_vm;
        public category_group_view_model category_group_vm
        {
            get
            {
                if (_category_group_vm == null)
                    _category_group_vm = new category_group_view_model();
                return _category_group_vm;
            }
        }

        // ▼▼▼ [Sprint 5F] 追加：バックアップ復元VM ▼▼▼
        private backup_restore_view_model? _backup_restore_vm;
        public backup_restore_view_model backup_restore_vm
        {
            get
            {
                if (_backup_restore_vm == null)
                    _backup_restore_vm = new backup_restore_view_model();
                return _backup_restore_vm;
            }
        }

        // ===== DB設定 =====

        private string _current_db_path = "";
        public string current_db_path
        {
            get => _current_db_path;
            set => SetProperty(ref _current_db_path, value);
        }

        public string local_db_path => database_manager.get_local_db_path();

        private bool _nas_enabled;
        public bool nas_enabled
        {
            get => _nas_enabled;
            set { if (SetProperty(ref _nas_enabled, value)) OnPropertyChanged(nameof(nas_enabled)); }
        }

        // ▼▼▼ [Sprint 6] デフォルトを新NASパスに設定（DBに保存値があればそちらが優先） ▼▼▼
        private string _nas_path = PRODUCTION_NAS_PATH;
        public string nas_path
        {
            get => _nas_path;
            set => SetProperty(ref _nas_path, value);
        }

        private string _nas_status_message = "";
        public string nas_status_message
        {
            get => _nas_status_message;
            set => SetProperty(ref _nas_status_message, value);
        }

        private Brush _nas_status_color = Brushes.Gray;
        public Brush nas_status_color
        {
            get => _nas_status_color;
            set => SetProperty(ref _nas_status_color, value);
        }

        // ===== 詳細設定（ローカルJSON） =====
        // DBには保存しない。各PCで個別に保持するUI設定

        private EA_CostManager.UserSettings _local = EA_CostManager.UserSettingsManager.load();

        // カラーテーマ: "cyan"=水色(デフォルト) / "blue"=濃い青 / "green"=緑 / "dark"=ダーク
        public string theme
        {
            get => _local.theme;
            set { _local.theme = value; OnPropertyChanged(nameof(theme)); }
        }

        // フォントサイズ: "small"=小 / "medium"=中(デフォルト) / "large"=大
        public string font_size
        {
            get => _local.font_size;
            set { _local.font_size = value; OnPropertyChanged(nameof(font_size)); }
        }

        // 起動時のページ: "dashboard"=ダッシュボード(デフォルト) / "cost"=原価集計
        public string startup_page
        {
            get => _local.startup_page;
            set { _local.startup_page = value; OnPropertyChanged(nameof(startup_page)); }
        }

        // 現場タブの色帯の幅: 4=細 / 6=普通(デフォルト) / 10=太
        public int tab_color_bar_width
        {
            get => _local.tab_color_bar_width;
            set { _local.tab_color_bar_width = value; OnPropertyChanged(nameof(tab_color_bar_width)); }
        }

        // 大区分の選択を記憶する
        public bool remember_group
        {
            get => _local.remember_group;
            set { _local.remember_group = value; OnPropertyChanged(nameof(remember_group)); }
        }

        // ▼▼▼ 追加：選択タブの拡大表示（true=拡大あり（デフォルト）/ false=拡大なし・即時反映）▼▼▼
        public bool tab_enlarge_on_select
        {
            get => _local.tab_enlarge_on_select;
            set { _local.tab_enlarge_on_select = value; OnPropertyChanged(nameof(tab_enlarge_on_select)); }
        }

        // ===== コマンド =====

        public ICommand check_nas_command { get; }
        public ICommand save_settings_command { get; }
        public ICommand save_advanced_command { get; }

        // ===== コンストラクタ =====

        public settings_view_model()
        {
            bool is_admin = EA_CostManager.UserSession.is_admin;

            // ▼▼▼ 全ユーザー共通 ▼▼▼
            nav_items.Add(new settings_nav_item { label = "DB設定", key = "db" });
            nav_items.Add(new settings_nav_item { label = "アーカイブ済み現場", key = "archived" });
            nav_items.Add(new settings_nav_item { label = "詳細設定", key = "advanced" });
            // ▼▼▼ [Sprint 5D] 追加：エラーログ参照（全ユーザー表示） ▼▼▼
            nav_items.Add(new settings_nav_item { label = "エラーログ参照", key = "error_log" });

            if (is_admin)
            {
                // ▼▼▼ 管理者専用：管理メニューツリー ▼▼▼
                nav_items.Add(new settings_nav_item { label = "　マスタ管理", key = "master_header", is_header = true, indent = 1 });
                nav_items.Add(new settings_nav_item { label = "　　人員マスタ", key = "employees", indent = 2 });
                nav_items.Add(new settings_nav_item { label = "　　車両マスタ", key = "vehicles", indent = 2 });
                nav_items.Add(new settings_nav_item { label = "　　機材マスタ", key = "equipment", indent = 2 });
                nav_items.Add(new settings_nav_item { label = "　　氏名変換", key = "legacy", indent = 2 });
                nav_items.Add(new settings_nav_item { label = "　金額設定", key = "amount", indent = 1 });
                // ▼▼▼ 修正：ユーザー管理を一番下に移動 ▼▼▼
                nav_items.Add(new settings_nav_item { label = "ユーザー管理", key = "users", indent = 1 });
                // ▼▼▼ 追加：タブ並び順リセット（ユーザー管理の下） ▼▼▼
                nav_items.Add(new settings_nav_item { label = "　タブ並び順リセット", key = "tab_sort_reset", indent = 1 });
                // ▼▼▼ [Sprint 5E] 追加：区分結合設定（管理者のみ） ▼▼▼
                nav_items.Add(new settings_nav_item { label = "　区分結合設定", key = "category_group", indent = 1 });
                // ▼▼▼ [Sprint 5F] 追加：バックアップ復元（管理者のみ） ▼▼▼
                nav_items.Add(new settings_nav_item { label = "　バックアップ復元", key = "backup_restore", indent = 1 });
            }

            check_nas_command = new RelayCommand(async () => await check_nas_async());
            save_settings_command = new RelayCommand(async () => await save_settings_async());
            save_advanced_command = new RelayCommand(async () => await save_advanced_async());

            _ = load_settings_async();

            // 全ユーザーDB設定から開始
            selected_nav = nav_items[0];
        }

        // ===== マスタ管理の展開・折りたたみ =====

        private bool _is_master_expanded = false;
        public bool is_master_expanded
        {
            get => _is_master_expanded;
            set => SetProperty(ref _is_master_expanded, value);
        }

        public void toggle_admin_menu_expanded()
        {
            // ▼▼▼ 管理者以外は展開不可・ダイアログ表示 ▼▼▼
            if (!EA_CostManager.UserSession.is_admin)
            {
                System.Windows.MessageBox.Show(
                    "管理メニューは管理者権限が必要です。\n\n管理者に変更を依頼してください。",
                    "権限エラー",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            is_admin_menu_expanded = !is_admin_menu_expanded;
        }

        public void toggle_master_expanded()
        {
            is_master_expanded = !is_master_expanded;
            OnPropertyChanged(nameof(is_master_and_admin_visible));
        }

        // ===== ナビゲーション切替 =====

        private void switch_content(string key)
        {
            switch (key)
            {
                case "db":
                case "advanced":
                    current_content = null;
                    break;

                // ▼▼▼ [Sprint 5D] 追加：エラーログ参照 ▼▼▼
                // current_content=null のまま is_error_log_page フラグで表示切替
                // ページを開くたびにユーザー一覧を再取得する
                case "error_log":
                    current_content = null;
                    _ = error_log_vm.load_users_async();
                    break;

                // ▼▼▼ [Sprint 5E] 追加：区分結合設定 ▼▼▼
                case "category_group":
                    current_content = null;
                    _ = category_group_vm.load_async();
                    break;

                // ▼▼▼ [Sprint 5F] 追加：バックアップ復元 ▼▼▼
                case "backup_restore":
                    current_content = null;
                    _ = backup_restore_vm.load_async();
                    break;

                case "employees":
                    if (_employees_vm == null) _employees_vm = new master_employees_view_model();
                    else _ = _employees_vm.load_async();
                    current_content = _employees_vm;
                    break;

                case "vehicles":
                    if (_vehicles_vm == null) _vehicles_vm = new master_vehicles_view_model();
                    else _ = _vehicles_vm.load_async();
                    current_content = _vehicles_vm;
                    break;

                case "equipment":
                    if (_equipment_vm == null) _equipment_vm = new master_equipment_view_model();
                    else _ = _equipment_vm.load_async();
                    current_content = _equipment_vm;
                    break;

                case "legacy":
                    if (_legacy_vm == null) _legacy_vm = new master_legacy_name_view_model();
                    else _ = _legacy_vm.load_async();
                    current_content = _legacy_vm;
                    break;

                case "amount":
                    if (_amount_vm == null) _amount_vm = new master_amount_view_model();
                    else _ = _amount_vm.load_async();
                    current_content = _amount_vm;
                    break;

                case "archived":
                    if (_archived_vm == null) _archived_vm = new master_archived_projects_view_model();
                    else _ = _archived_vm.load_async();
                    current_content = _archived_vm;
                    break;

                case "users":
                    // ▼▼▼ ユーザー管理：全員アクセス可・管理者以外は閲覧のみ ▼▼▼
                    if (_users_vm is null) _users_vm = new EA_CostManager.ViewModels.user_management_view_model();
                    else _ = _users_vm.load_async();
                    current_content = _users_vm;
                    break;

                // ▼▼▼ 追加：タブ並び順リセット ▼▼▼
                case "tab_sort_reset":
                    current_content = new tab_sort_reset_view_model();
                    break;

                default:
                    current_content = null;
                    break;
            }
        }

        // ===== DB設定の読み込み =====

        private async Task load_settings_async()
        {
            try
            {
                current_db_path = database_manager.get_db_path();

                // ▼▼▼ 修正：ローカルDBから読み込む（保存と読み込みを一致させる）▼▼▼
                // 起動時はローカルDBのnas_pathを読んでNASに接続する設計のため
                // 設定画面もローカルDBから読むことで表示と実際の設定を一致させる
                string local_path = database_manager.get_local_db_path();
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                    $"Data Source={local_path}");
                conn.Open();
                var settings = await conn.QueryAsync<(string key, string value)>(
                    "SELECT key, value FROM app_settings WHERE key IN ('nas_enabled','nas_path')");

                foreach (var (key, value) in settings)
                {
                    if (key == "nas_enabled") _nas_enabled = value == "1";
                    if (key == "nas_path")
                    {
                        // ▼▼▼ [Sprint 6] 古いパスは新パスに自動置換 ▼▼▼
                        _nas_path = value == @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\00_Database\ea_core.db"
                            ? PRODUCTION_NAS_PATH
                            : value;
                    }
                }

                OnPropertyChanged(nameof(nas_enabled));
                OnPropertyChanged(nameof(nas_path));
            }
            catch (Exception ex)
            {
                status_message = $"設定読み込みエラー：{ex.Message}";
            }
        }

        // ===== NAS接続確認 =====

        private async Task check_nas_async()
        {
            if (string.IsNullOrWhiteSpace(nas_path))
            {
                nas_status_message = "NASパスを入力してください";
                nas_status_color = Brushes.OrangeRed;
                return;
            }

            await Task.Run(() =>
            {
                string? dir = Path.GetDirectoryName(nas_path);
                bool ok = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (ok) { nas_status_message = "✅ 接続できます"; nas_status_color = Brushes.Green; }
                    else { nas_status_message = "❌ 接続できません（パスを確認してください）"; nas_status_color = Brushes.OrangeRed; }
                });
            });
        }

        // ===== DB設定の保存 =====

        private async Task save_settings_async()
        {
            if (is_busy) return;
            is_busy = true;
            status_message = "設定を保存しています...";
            try
            {
                // ▼▼▼ 修正：ローカルDBに明示的に保存する ▼▼▼
                // 起動時は必ずローカルDBからnas_pathを読んでNASに接続する設計のため
                // 設定保存も必ずローカルDBに書く必要がある
                // 旧実装は create_connection()（現在の接続先＝本番NAS DB）に保存していたため
                // 再起動後もローカルDBのnas_pathが変わらず切り替えが効かないバグがあった
                string local_path = database_manager.get_local_db_path();
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                    $"Data Source={local_path}");
                conn.Open();

                await conn.ExecuteAsync(
                    "INSERT OR REPLACE INTO app_settings (key, value) VALUES ('nas_enabled', @v)",
                    new { v = nas_enabled ? "1" : "0" });
                await conn.ExecuteAsync(
                    "INSERT OR REPLACE INTO app_settings (key, value) VALUES ('nas_path', @v)",
                    new { v = nas_path });

                if (nas_enabled && !string.IsNullOrWhiteSpace(nas_path))
                {
                    status_message = "✅ 設定を保存しました。再起動後に新しいDBに接続します。";
                }
                else
                {
                    status_message = "✅ 設定を保存しました。再起動後にローカルDBで動作します。";
                }

                current_db_path = database_manager.get_db_path();
            }
            catch (Exception ex)
            {
                status_message = $"❌ 保存エラー：{ex.Message}";
            }
            finally
            {
                is_busy = false;
            }
        }

        // ===== 詳細設定の保存（ローカルJSONに保存） =====

        /// <summary>
        /// 詳細設定をローカルJSONファイルに保存する
        /// 即時反映：カラーテーマ・フォントサイズ・色帯の幅
        /// 次回起動時反映：タブ表示モード・起動ページ・大区分記憶
        /// </summary>
        private async Task save_advanced_async()
        {
            if (is_busy) return;
            is_busy = true;
            status_message = "詳細設定を保存しています...";
            try
            {
                // JSONに保存
                EA_CostManager.UserSettingsManager.save(_local);

                // ▼▼▼ 即時反映（再起動不要）
                EA_CostManager.UserSettingsManager.apply_immediate(_local);

                // 次回起動時に反映される設定があるか判定してメッセージを分ける
                bool needs_restart = true; // タブモード・起動ページ・大区分記憶は再起動必要
                status_message = needs_restart
                    ? "✅ 保存しました。カラー・フォント・色帯は即時反映済み。タブ表示・起動ページは次回起動時に反映されます。"
                    : "✅ 詳細設定を保存しました。";

                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                status_message = $"❌ 保存エラー：{ex.Message}";
            }
            finally
            {
                is_busy = false;
            }
        }

        // ===== クイックセット用パス定数 =====

        public const string PRODUCTION_NAS_PATH =
            @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\00_Database\ea_core.db";

        public const string TEST_NAS_PATH =
            @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\99_Test\01_CostManager\ea_core.db";

        public void set_production_nas_path() { nas_path = PRODUCTION_NAS_PATH; nas_enabled = true; }
        public void set_test_nas_path() { nas_path = TEST_NAS_PATH; nas_enabled = true; }
    }

    // ================================================================
    // ▼▼▼ 追加：タブ並び順リセット用ViewModel ▼▼▼
    // 管理メニュー内の「タブ並び順リセット」画面に対応する
    // 大区分ごとに sort_order=0 にリセット → 属性カラー順に戻る
    // ================================================================
    public class tab_sort_reset_view_model : base_view_model
    {
        private System.Collections.ObjectModel.ObservableCollection<tab_sort_reset_group> _groups = new();
        public System.Collections.ObjectModel.ObservableCollection<tab_sort_reset_group> groups => _groups;

        private string _status = "";
        public string status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public tab_sort_reset_view_model()
        {
            _ = load_async();
        }

        // DB から実在する大区分を収集してグループ一覧を構築する
        public async Task load_async()
        {
            try
            {
                using var conn = EA_CostManager.Data.database_manager.create_connection();
                var codes = (await conn.QueryAsync<string>(
                    "SELECT DISTINCT category_code FROM projects WHERE is_active = 1")).ToList();

                var main_prefixes = new[] { "EA", "RD", "SM" };
                var group_keys = new System.Collections.Generic.HashSet<string>();

                foreach (var code in codes)
                {
                    bool matched = false;
                    foreach (var pfx in main_prefixes)
                    {
                        if (code.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                        {
                            group_keys.Add(pfx);
                            matched = true;
                            break;
                        }
                    }
                    if (!matched) group_keys.Add("その他");
                }

                _groups.Clear();

                // ▼▼▼ 追加：「すべて」を一番上に追加（全グループ一括リセット）▼▼▼
                _groups.Add(new tab_sort_reset_group("すべて", "ALL", this));

                foreach (var pfx in main_prefixes)
                    if (group_keys.Contains(pfx))
                        _groups.Add(new tab_sort_reset_group(pfx, pfx, this));

                if (group_keys.Contains("その他"))
                    _groups.Add(new tab_sort_reset_group("その他", "OTHER", this));
            }
            catch (Exception ex)
            {
                status = $"読み込みエラー：{ex.Message}";
            }
        }

        // 指定グループの sort_order を 0 にリセットする
        public async Task reset_group_async(string group_label, string group_prefix)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"「{group_label}」グループのタブ並び順をリセットします。\n属性カラー順（デフォルト）に戻ります。\nこの操作は元に戻せません。",
                "並び順リセット確認",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.OK) return;

            try
            {
                using var conn = EA_CostManager.Data.database_manager.create_connection();
                // ▼▼▼ 追加：「すべて」は全プロジェクトの sort_order を一括リセット ▼▼▼
                if (group_prefix == "ALL")
                {
                    await conn.ExecuteAsync(
                        "UPDATE projects SET sort_order = 0 WHERE is_active = 1");
                }
                else if (group_prefix == "OTHER")
                {
                    await conn.ExecuteAsync(@"
                        UPDATE projects SET sort_order = 0
                        WHERE is_active = 1
                          AND category_code NOT LIKE 'EA%'
                          AND category_code NOT LIKE 'RD%'
                          AND category_code NOT LIKE 'SM%'");
                }
                else
                {
                    await conn.ExecuteAsync(
                        "UPDATE projects SET sort_order = 0 WHERE is_active = 1 AND category_code LIKE @prefix",
                        new { prefix = group_prefix + "%" });
                }
                status = $"✅ 「{group_label}」グループの並び順をリセットしました。";

                // ▼▼▼ 追加：操作ログ書き込み ▼▼▼
                try
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO operation_logs
                            (log_datetime, pc_user_id, operator_name, operation_type, detail)
                        VALUES
                            (datetime('now','localtime'), @uid, @name, '並び順リセット', @detail)",
                        new
                        {
                            uid = EA_CostManager.UserSession.user_id,
                            name = EA_CostManager.UserSession.user_name,
                            detail = $"{group_label} グループの並び順をリセット"
                        });
                }
                catch { /* ログ失敗は無視 */ }
            }
            catch (Exception ex)
            {
                status = $"❌ リセットエラー：{ex.Message}";
            }
        }
    }

    /// <summary>リセット画面の大区分1行分（ラベル＋リセットボタン用）</summary>
    public class tab_sort_reset_group : base_view_model
    {
        public string label { get; }
        public string prefix { get; }
        private readonly tab_sort_reset_view_model _parent;
        public System.Windows.Input.ICommand reset_command { get; }

        public tab_sort_reset_group(string label, string prefix, tab_sort_reset_view_model parent)
        {
            this.label = label;
            this.prefix = prefix;
            _parent = parent;
            reset_command = new CommunityToolkit.Mvvm.Input.RelayCommand(
                async () => await _parent.reset_group_async(label, prefix));
        }
    }
}
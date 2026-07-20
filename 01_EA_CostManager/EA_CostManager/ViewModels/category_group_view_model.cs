using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Services;

namespace EA_CostManager.ViewModels
{
    /// <summary>オートコンプリート候補1件（コード＋現場名）</summary>
    public class code_suggestion_item
    {
        public string category_code { get; set; } = "";
        public string site_name { get; set; } = "";
        /// <summary>ListBoxに表示するテキスト：「コード ／ 現場名」形式</summary>
        public string display_text =>
            string.IsNullOrWhiteSpace(site_name) || site_name == category_code
                ? category_code
                : $"{category_code}  ／  {site_name}";
    }
    /// <summary>
    /// Sprint 5E: 区分結合設定画面のViewModel
    /// 設定画面の管理メニュー内「区分結合設定」に対応
    /// </summary>
    public class category_group_view_model : base_view_model
    {
        // ── バインディングプロパティ ─────────────────────────────────────────────

        /// <summary>結合設定一覧</summary>
        public ObservableCollection<category_group_item> group_items { get; } = new();

        /// <summary>新規追加：親区分コード入力欄</summary>
        private string _new_parent_code = "";
        public string new_parent_code
        {
            get => _new_parent_code;
            set
            {
                if (SetProperty(ref _new_parent_code, value))
                    update_parent_suggestions(value);
            }
        }

        /// <summary>新規追加：子区分コード入力欄</summary>
        private string _new_child_code = "";
        public string new_child_code
        {
            get => _new_child_code;
            set
            {
                if (SetProperty(ref _new_child_code, value))
                    update_child_suggestions(value);
            }
        }

        // ── オートコンプリート ─────────────────────────────────────────────────

        /// <summary>全区分コード＋現場名一覧（DB projects テーブルから取得）</summary>
        private System.Collections.Generic.List<code_suggestion_item> _all_codes = new();

        /// <summary>親コード入力欄の候補リスト</summary>
        public ObservableCollection<code_suggestion_item> parent_suggestions { get; } = new();

        /// <summary>子コード入力欄の候補リスト</summary>
        public ObservableCollection<code_suggestion_item> child_suggestions { get; } = new();

        /// <summary>親コード候補を表示するか（1件以上かつ入力中）</summary>
        public bool show_parent_suggestions => parent_suggestions.Count > 0
            && !string.IsNullOrWhiteSpace(_new_parent_code);

        /// <summary>子コード候補を表示するか</summary>
        public bool show_child_suggestions => child_suggestions.Count > 0
            && !string.IsNullOrWhiteSpace(_new_child_code);

        /// <summary>
        /// 親コード候補を更新（区分コード前方一致 OR 現場名部分一致）
        /// </summary>
        private void update_parent_suggestions(string input)
        {
            parent_suggestions.Clear();
            if (string.IsNullOrWhiteSpace(input))
            {
                OnPropertyChanged(nameof(show_parent_suggestions));
                return;
            }
            var q = input.Trim();
            foreach (var c in _all_codes)
            {
                if (c.category_code.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                    || c.site_name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    parent_suggestions.Add(c);
            }
            OnPropertyChanged(nameof(show_parent_suggestions));
        }

        /// <summary>
        /// 子コード候補を更新（区分コード前方一致 OR 現場名部分一致）
        /// </summary>
        private void update_child_suggestions(string input)
        {
            child_suggestions.Clear();
            if (string.IsNullOrWhiteSpace(input))
            {
                OnPropertyChanged(nameof(show_child_suggestions));
                return;
            }
            var q = input.Trim();
            foreach (var c in _all_codes)
            {
                if (c.category_code.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                    || c.site_name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    child_suggestions.Add(c);
            }
            OnPropertyChanged(nameof(show_child_suggestions));
        }

        /// <summary>親コード候補を選択したときの処理</summary>
        private code_suggestion_item? _selected_parent_suggestion;
        public code_suggestion_item? selected_parent_suggestion
        {
            get => _selected_parent_suggestion;
            set
            {
                _selected_parent_suggestion = value;
                if (value != null)
                {
                    // category_codeのみを入力欄にセット（display_textではなく）
                    _new_parent_code = value.category_code;
                    OnPropertyChanged(nameof(new_parent_code));
                    parent_suggestions.Clear();
                    OnPropertyChanged(nameof(show_parent_suggestions));
                    _selected_parent_suggestion = null;
                }
            }
        }

        /// <summary>子コード候補を選択したときの処理</summary>
        private code_suggestion_item? _selected_child_suggestion;
        public code_suggestion_item? selected_child_suggestion
        {
            get => _selected_child_suggestion;
            set
            {
                _selected_child_suggestion = value;
                if (value != null)
                {
                    _new_child_code = value.category_code;
                    OnPropertyChanged(nameof(new_child_code));
                    child_suggestions.Clear();
                    OnPropertyChanged(nameof(show_child_suggestions));
                    _selected_child_suggestion = null;
                }
            }
        }

        // ── コマンド ──────────────────────────────────────────────────────────

        public ICommand add_command { get; }
        public ICommand delete_command { get; }
        public ICommand reload_command { get; }

        // ── コンストラクタ ────────────────────────────────────────────────────

        public category_group_view_model()
        {
            add_command = new RelayCommand(async () => await add_async());
            delete_command = new RelayCommand<category_group_item?>(
                async item => { if (item != null) await delete_async(item); });
            reload_command = new RelayCommand(async () => await load_async());

            _ = load_async();
        }

        // ── データ操作 ────────────────────────────────────────────────────────

        /// <summary>結合設定一覧を読み込む</summary>
        public async Task load_async()
        {
            is_busy = true;
            try
            {
                // ▼ 区分コード＋現場名一覧をDBから取得（オートコンプリート用）
                using var conn = EA_CostManager.Data.database_manager.create_connection();
                var code_rows = await conn.QueryAsync<code_suggestion_item>(
                    "SELECT category_code, site_name FROM projects WHERE is_active = 1 ORDER BY category_code");
                _all_codes = code_rows.ToList();

                var items = await CategoryGroupService.get_all_async();
                group_items.Clear();
                foreach (var item in items)
                    group_items.Add(item);

                status_message = group_items.Count == 0
                    ? "結合設定はまだありません"
                    : $"{group_items.Count}件の結合設定";
            }
            catch (Exception ex)
            {
                status_message = $"読み込みエラー：{ex.Message}";
            }
            finally { is_busy = false; }
        }

        /// <summary>結合設定を追加する</summary>
        private async Task add_async()
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            var parent = new_parent_code.Trim().ToUpper();
            var child = new_child_code.Trim().ToUpper();

            var (success, error) = await CategoryGroupService.add_async(parent, child);

            if (!success)
            {
                status_message = $"❌ {error}";
                return;
            }

            // 入力欄をクリアして再読み込み
            new_parent_code = "";
            new_child_code = "";
            await load_async();
            status_message = $"✅ 結合設定を追加しました（{parent} ← {child}）";
        }

        /// <summary>結合設定を削除する</summary>
        private async Task delete_async(category_group_item item)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            var result = System.Windows.MessageBox.Show(
                $"「{item.parent_code} ← {item.child_code}」の結合設定を削除しますか？\n\n" +
                "削除後、原価集計画面を再読み込みすると個別のタブに戻ります。",
                "結合設定の削除",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);

            if (result != System.Windows.MessageBoxResult.OK) return;

            var (success, error) = await CategoryGroupService.delete_async(item.id);

            if (!success)
            {
                status_message = $"❌ 削除エラー：{error}";
                return;
            }

            await load_async();
            status_message = "✅ 削除しました";
        }
    }
}
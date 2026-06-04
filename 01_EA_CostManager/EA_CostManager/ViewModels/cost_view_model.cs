using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Services;

namespace EA_CostManager.ViewModels
{
    /// <summary>
    /// 大区分グループ（左サイドバー用）
    /// </summary>
    public class project_group_item : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void notify(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        public string label { get; set; } = "";
        public string prefix { get; set; } = "";
        public int count { get; set; } = 0;
        public string display_label => count > 0 ? $"{label}  ({count})" : label;

        private bool _is_selected;
        public bool is_selected
        {
            get => _is_selected;
            set { if (_is_selected != value) { _is_selected = value; notify(nameof(is_selected)); } }
        }
    }

    /// <summary>
    /// 原価集計ページのViewModel
    /// ▼▼▼ 修正①：タブが動く問題の根本解決
    /// 問題の原因：rebuild_filtered_tabs() で _filtered_tabs.Clear() → 再追加するたびに
    ///             WPFの TabControl が選択タブを表示位置に自動スクロールして「動いて見える」
    /// 解決方針：
    ///   ① load_projects_async() でロード後に1回だけ sort_key を設定してソート済みリストを保持
    ///   ② rebuild_filtered_tabs() では Clear() を使わず、Remove/Move/Add で差分更新する
    ///   ③ 大区分フィルタ変更時も既存タブの順序を維持したまま不要タブだけ除外する
    /// ▼▼▼ 修正②：N+1クエリ問題の解消
    /// 変更前：現場N件 × (load_records_async + load_filter_tabs_async) = N×2回以上の直列DB呼び出し
    /// 変更後：起動時に全現場の cost_records / cost_filter_tabs / category_groups を
    ///         3クエリで一括取得して各VMに配る方式に変更
    ///         → 現場数に関わらずDB呼び出しは3回固定
    /// </summary>
    public class cost_view_model : base_view_model
    {
        private readonly CostAggregationService _service = new();

        // ---- 現場タブ（全件・ソート済み） ----
        public ObservableCollection<project_cost_view_model> project_tabs { get; } = new();

        // ---- フィルタ済みタブ（大区分で絞り込んだ表示用） ----
        // ▼▼▼ 変更：Clear()廃止 → Remove/Move/Add差分更新でTabControlのスクロールを抑制
        private ObservableCollection<project_cost_view_model> _filtered_tabs = new();
        public ObservableCollection<project_cost_view_model> filtered_tabs => _filtered_tabs;

        private project_cost_view_model? _selected_tab;
        public project_cost_view_model? selected_tab
        {
            get => _selected_tab;
            set => SetProperty(ref _selected_tab, value);
        }

        // ---- 現場タブ検索 ----
        // ヘッダーバーの検索欄でタブを検索してジャンプする機能

        // 検索文字列（入力のたびにsearch_suggestionsを更新）
        private string _tab_search_text = "";
        public string tab_search_text
        {
            get => _tab_search_text;
            set
            {
                if (SetProperty(ref _tab_search_text, value))
                    update_search_suggestions();
            }
        }

        // 候補リスト（区分コード・現場名で部分一致）
        private ObservableCollection<project_cost_view_model> _search_suggestions = new();
        public ObservableCollection<project_cost_view_model> search_suggestions => _search_suggestions;

        // 選択された候補 → selected_tab をそのタブに切り替える
        private project_cost_view_model? _selected_search_item;
        public project_cost_view_model? selected_search_item
        {
            get => _selected_search_item;
            set
            {
                if (SetProperty(ref _selected_search_item, value) && value != null)
                    jump_to_tab(value);
            }
        }

        // 検索候補を更新（区分コード or 現場名の部分一致）
        private void update_search_suggestions()
        {
            _search_suggestions.Clear();
            if (string.IsNullOrWhiteSpace(_tab_search_text)) return;

            string query = _tab_search_text.Trim().ToLower();
            foreach (var tab in project_tabs)
            {
                if (tab.category_code.ToLower().Contains(query)
                    || tab.site_name.ToLower().Contains(query))
                    _search_suggestions.Add(tab);
            }
        }

        // 指定タブにジャンプ（大区分を「すべて」に戻してselected_tabを切り替える）
        private void jump_to_tab(project_cost_view_model target)
        {
            // 「すべて」グループに切り替えてからタブを選択
            var all_group = group_items.FirstOrDefault(g => g.prefix == "");
            if (all_group != null && _selected_group != all_group)
                selected_group = all_group;

            // filtered_tabsにいなければ追加（rebuild_filtered_tabsで入るはず）
            if (!_filtered_tabs.Contains(target))
                rebuild_filtered_tabs();

            selected_tab = target;

            // 検索テキスト・候補リストをリセット
            _tab_search_text = "";
            OnPropertyChanged(nameof(tab_search_text));
            _search_suggestions.Clear();
            _selected_search_item = null;
        }

        // ---- 大区分グループ（左サイドバー） ----
        public ObservableCollection<project_group_item> group_items { get; } = new();

        private project_group_item? _selected_group;
        public project_group_item? selected_group
        {
            get => _selected_group;
            set
            {
                if (SetProperty(ref _selected_group, value))
                {
                    foreach (var g in group_items)
                        g.is_selected = g == value;
                    rebuild_filtered_tabs();
                }
            }
        }

        // ---- 現場なし表示フラグ ----
        public bool has_no_projects => project_tabs.Count == 0;

        // ---- コマンド ----
        public ICommand aggregate_all_command { get; }
        public ICommand reload_command { get; }

        public cost_view_model()
        {
            project_tabs.CollectionChanged += (_, _) =>
                OnPropertyChanged(nameof(has_no_projects));

            aggregate_all_command = new RelayCommand(async () => await aggregate_all_async());
            reload_command = new RelayCommand(async () => await load_projects_async());
        }

        public async Task initialize_async()
        {
            await load_projects_async();
        }

        // ---- 現場リストロード ----
        private async Task load_projects_async()
        {
            if (is_busy) return;
            is_busy = true;
            status_message = "現場情報を読み込み中...";
            try
            {
                // ▼▼▼ 追加：リロード前に選択状態を保存 ▼▼▼
                // initialize_async() 呼び出し後も選択グループ・タブを維持するために使用
                // 属性変更・現場名編集・アーカイブ後のリロード時に「すべて」に戻らないようにする
                string? saved_group_prefix = _selected_group?.prefix;
                string? saved_tab_code = selected_tab?.category_code;

                using var conn = database_manager.create_connection();

                // ▼▼▼ 追加(B)：projects.agg_mode 列の存在を保証する（マイグレーション未適用のDBでもSELECTで落ちないように）▼▼▼
                // FilterTabDialog 等と同じ防御パターン。既に存在すれば SqliteException を握りつぶす（冪等）。
                try { await conn.ExecuteAsync("ALTER TABLE projects ADD COLUMN agg_mode TEXT DEFAULT 'daily'"); }
                catch { /* 既に存在する場合は無視 */ }

                // ▼▼▼ 修正：一括取得① - アクティブ現場の基本情報 ▼▼▼
                var projects = (await conn.QueryAsync(@"
                    SELECT id, category_code, site_name, company_name, tab_color, detail, sort_order, agg_mode
                    FROM projects
                    WHERE is_active = 1
                    ORDER BY category_code")).ToList();

                // ▼▼▼ 修正：一括取得② - 全現場の category_groups（子コード）を一括取得 ▼▼▼
                // 変更前：各VMのload_records_async/load_filter_tabs_asyncで現場ごとに個別取得
                // 変更後：ここで1回だけ取得してDictionaryに変換して配布する
                var all_groups = (await conn.QueryAsync(@"
                    SELECT parent_code, child_code
                    FROM category_groups")).ToList();

                // parent_code → child_codes[] のDictionaryに変換
                var child_codes_map = all_groups
                    .GroupBy(g => (string)(g.parent_code ?? ""))
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(x => (string)(x.child_code ?? "")).ToList()
                    );

                // ▼▼▼ 修正：一括取得③ - 子コードを含む全コードリストを構築 ▼▼▼
                // 子コードとして登録されているコードのセット（タブ除外用）
                var child_code_set = new HashSet<string>(
                    all_groups.Select(g => (string)(g.child_code ?? "")));

                // ▼▼▼ 修正：一括取得④ - 全現場の cost_records を一括取得 ▼▼▼
                // 変更前：各VMがload_records_asyncで1現場ずつSELECT（現場N件 = N回）
                // 変更後：アクティブ現場の全レコードを1クエリで取得してDictionaryに変換
                var active_codes = projects
                    .Select(p => (string)(p.category_code ?? ""))
                    .Concat(child_code_set)
                    .Distinct()
                    .ToList();

                IEnumerable<EA_CostManager.Models.cost_record> all_records_raw = new List<EA_CostManager.Models.cost_record>();
                if (active_codes.Count > 0)
                {
                    // Dapperのパラメータ展開でIN句を生成
                    all_records_raw = await conn.QueryAsync<EA_CostManager.Models.cost_record>(@"
                        SELECT * FROM cost_records
                        WHERE category_code IN @codes
                        ORDER BY record_date",
                        new { codes = active_codes });
                }

                // category_code → records[] のDictionaryに変換（子コード含む）
                var records_map = all_records_raw
                    .GroupBy(r => r.category_code ?? "")
                    .ToDictionary(g => g.Key, g => g.ToList());

                // ▼▼▼ 修正：一括取得⑤ - 全現場の cost_filter_tabs を一括取得 ▼▼▼
                // 変更前：各VMがload_filter_tabs_asyncで1現場ずつSELECT（現場N件 = N回）
                // 変更後：全アクティブ現場のフィルタタブを1クエリで取得してDictionaryに変換
                var active_project_ids = projects
                    .Select(p => (int)(p.id ?? 0))
                    .ToList();

                IEnumerable<EA_CostManager.Models.cost_filter_tab> all_filter_tabs_raw = new List<EA_CostManager.Models.cost_filter_tab>();
                if (active_project_ids.Count > 0)
                {
                    all_filter_tabs_raw = await conn.QueryAsync<EA_CostManager.Models.cost_filter_tab>(@"
                        SELECT * FROM cost_filter_tabs
                        WHERE project_id IN @ids AND is_archived = 0
                        ORDER BY id",
                        new { ids = active_project_ids });
                }

                // project_id → filter_tabs[] のDictionaryに変換
                var filter_tabs_map = all_filter_tabs_raw
                    .GroupBy(t => t.project_id)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // ---- VMを生成してデータを配布 ----
                project_tabs.Clear();

                var seen_codes = new HashSet<string>();
                foreach (var p in projects)
                {
                    string code = (string)(p.category_code ?? "");
                    if (!seen_codes.Add(code)) continue;

                    // ▼▼▼ [Sprint 5E] 子コードはタブを作らない（親タブに合算表示される） ▼▼▼
                    if (child_code_set.Contains(code)) continue;

                    var vm = new project_cost_view_model(
                        (int)(p.id ?? 0),
                        code,
                        (string)(p.site_name ?? ""),
                        (string)(p.company_name ?? ""),
                        (string)(p.tab_color ?? ""),
                        (string)(p.detail ?? ""),
                        (string)(p.agg_mode ?? "daily"));   // ▼追加(B)：現場の既定モード
                    vm.sort_order = (int)(p.sort_order ?? 0);

                    // ▼▼▼ 修正：一括取得したデータを各VMに配布（DB呼び出しなし） ▼▼▼
                    var my_child_codes = child_codes_map.TryGetValue(code, out var cc) ? cc : new List<string>();
                    var my_records = records_map.TryGetValue(code, out var rr) ? rr : new List<EA_CostManager.Models.cost_record>();
                    var my_child_recs = my_child_codes
                        .SelectMany(c => records_map.TryGetValue(c, out var cr) ? cr : new List<EA_CostManager.Models.cost_record>())
                        .ToList();
                    var my_filter_tabs = filter_tabs_map.TryGetValue((int)(p.id ?? 0), out var ft) ? ft : new List<EA_CostManager.Models.cost_filter_tab>();

                    // VMにデータを直接セット（load_records_async / load_filter_tabs_async を呼ばない）
                    vm.set_records_bulk(my_records, my_child_codes, my_child_recs);
                    await vm.set_filter_tabs_bulk_async(my_filter_tabs, code, my_child_codes, records_map);

                    project_tabs.Add(vm);
                }

                // ▼▼▼ 追加：ロード直後に1回だけ属性カラー順にソート
                apply_sort_order_to_project_tabs();

                build_group_items();
                rebuild_filtered_tabs();

                // ▼▼▼ 修正：保存したタブコードで選択を復元（なければ先頭）▼▼▼
                selected_tab = project_tabs.FirstOrDefault(t => t.category_code == saved_tab_code)
                            ?? project_tabs.FirstOrDefault();

                // ▼▼▼ 追加：保存したグループを復元（なければ「すべて」のまま）▼▼▼
                if (saved_group_prefix != null)
                {
                    var restored = group_items.FirstOrDefault(g => g.prefix == saved_group_prefix);
                    if (restored != null)
                        selected_group = restored;
                }

                status_message = project_tabs.Count == 0
                    ? "現場が登録されていません"
                    : $"{project_tabs.Count}件の現場を読み込みました";
            }
            catch (Exception ex)
            {
                status_message = $"読み込みエラー：{ex.Message}";
            }
            finally
            {
                is_busy = false;
            }
        }

        /// <summary>
        /// ▼▼▼ 修正：project_tabs のソート順を決定する（グローバル管理に変更）
        /// 手動並び順なし（sort_order=0のみ）→ 属性カラー順 → 区分コード順（既存動作を維持）
        /// 手動並び順あり（sort_order>0が1つでも存在する）→ 全タブをグローバルな sort_order で管理
        /// ※ 大区分を跨ぐドラッグ並び替えに対応するため、グループ単位ではなく全タブを一元管理する
        /// </summary>
        private void apply_sort_order_to_project_tabs()
        {
            // 手動並び順が1つでも存在するか確認（グループ問わず全タブを対象）
            bool has_any_manual = project_tabs.Any(t => t.sort_order > 0);

            List<project_cost_view_model> sorted;

            if (!has_any_manual)
            {
                // 手動並び順なし：既存の属性カラー順 → 区分コード順（動作変更なし）
                sorted = project_tabs
                    .OrderBy(t => get_attribute_sort_order(t.tab_color))
                    .ThenBy(t => t.category_code)
                    .ToList();
            }
            else
            {
                // 手動並び順あり：全タブをグローバルな sort_order で並べる
                // sort_order=0 のタブは末尾に属性カラー順で配置
                sorted = project_tabs
                    .OrderBy(t => t.sort_order > 0 ? (long)t.sort_order : (long)int.MaxValue)
                    .ThenBy(t => get_attribute_sort_order(t.tab_color))
                    .ThenBy(t => t.category_code)
                    .ToList();
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                int current_idx = project_tabs.IndexOf(sorted[i]);
                if (current_idx != i)
                    project_tabs.Move(current_idx, i);
            }
        }

        /// <summary>
        /// ▼▼▼ 修正：ドラッグ&ドロップによるタブ並び替え（大区分跨ぎ対応）▼▼▼
        /// source を target の位置に移動し、全タブのグローバル sort_order を DB に保存する
        /// </summary>
        public async Task reorder_tab_async(project_cost_view_model source, project_cost_view_model target)
        {
            int source_idx = project_tabs.IndexOf(source);
            int target_idx = project_tabs.IndexOf(target);
            if (source_idx < 0 || target_idx < 0 || source_idx == target_idx) return;

            project_tabs.Move(source_idx, target_idx);
            rebuild_filtered_tabs();
            // ▼▼▼ 修正：グループ単位保存 → 全タブグローバル保存に変更 ▼▼▼
            await save_all_sort_order_async();
        }

        /// <summary>
        /// ▼▼▼ 修正：全タブのグローバル sort_order を DB に一括保存 ▼▼▼
        /// project_tabs の現在の順序を正として全タブを 1 始まりで採番して保存する
        /// 大区分を跨ぐ並び替えに対応するため、グループ単位ではなく全タブを一元管理する
        /// </summary>
        private async Task save_all_sort_order_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                using var tx = conn.BeginTransaction();

                // project_tabs の現在の並び順を 1 始まりのグローバル連番で保存
                for (int i = 0; i < project_tabs.Count; i++)
                {
                    int new_order = i + 1;
                    project_tabs[i].sort_order = new_order;
                    await conn.ExecuteAsync(
                        "UPDATE projects SET sort_order = @order WHERE id = @id",
                        new { order = new_order, id = project_tabs[i].project_id }, tx);
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"タブ並び順保存エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// tab_color から並び優先順位を返す（数値が小さいほど先頭）
        /// 契約済(1) → 契約前(2) → 第一土木(3) → 自社案件(4) → セミナー(5) → 追加(6) → その他(7)
        /// </summary>
        // カラー順：契約済→契約前→追加→第一土木→自社案件→その他
        private static int get_attribute_sort_order(string tab_color) => tab_color switch
        {
            "#E67E22" => 1,  // 契約済（オレンジ）
            "#E91E8C" => 2,  // 契約前（ピンク）
            "#E8543A" => 3,  // 追加（コーラル）
            "#27AE60" => 4,  // 第一土木（緑）
            "#2E75B6" => 5,  // 自社案件（青）
            "#8E44AD" => 6,  // セミナー（紫）
            _ => 7,  // その他・グレー・未設定
        };

        // ---- 全現場集計 ----
        private async Task aggregate_all_async()
        {
            if (is_busy) return;

            if (project_tabs.Count == 0)
            {
                status_message = "集計対象の現場がありません";
                return;
            }

            is_busy = true;
            try
            {
                const string ALL_START = "2000-01-01";
                const string ALL_END = "2099-12-31";

                int total = 0;
                foreach (var tab in project_tabs)
                {
                    status_message = $"{tab.tab_name} を集計中...";
                    var (cnt, err) = await _service.aggregate_async(
                        tab.category_code, ALL_START, ALL_END);

                    if (!string.IsNullOrEmpty(err))
                    {
                        status_message = $"集計エラー（{tab.tab_name}）：{err}";
                        return;
                    }

                    total += cnt;
                    await tab.load_records_async();
                    await tab.load_filter_tabs_async();
                }

                status_message = total == 0
                    ? "集計完了：対象日報なし"
                    : $"集計完了（{project_tabs.Count}現場）：{total}件";
            }
            catch (Exception ex)
            {
                status_message = $"集計中に予期せぬエラー：{ex.Message}";
            }
            finally
            {
                is_busy = false;
            }
        }

        // ---- 大区分グループを構築 ----
        // ▼▼▼ 修正⑦：EA/RD/SM のみ独立表示、VT/PR/DA/IS はその他に格納
        private void build_group_items()
        {
            var main_prefixes = new[] { "EA", "RD", "SM" };

            group_items.Clear();

            group_items.Add(new project_group_item
            {
                label = "すべて",
                prefix = "",
                count = project_tabs.Count,
            });

            foreach (var pfx in main_prefixes)
            {
                int cnt = project_tabs.Count(t =>
                    t.category_code.StartsWith(pfx, StringComparison.OrdinalIgnoreCase));
                if (cnt == 0) continue;
                group_items.Add(new project_group_item { label = pfx, prefix = pfx, count = cnt });
            }

            int other_cnt = project_tabs.Count(t =>
                !main_prefixes.Any(p =>
                    t.category_code.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            if (other_cnt > 0)
                group_items.Add(new project_group_item
                {
                    label = "その他",
                    prefix = "OTHER",
                    count = other_cnt
                });

            _selected_group = group_items.FirstOrDefault();
            OnPropertyChanged(nameof(selected_group));
            foreach (var g in group_items)
                g.is_selected = g == _selected_group;
        }

        // ---- 選択グループでフィルタ済みタブを再構築 ----
        // ▼▼▼ 修正①：Clear() 廃止 → Remove/Move/Add 差分更新
        // project_tabs はロード時にソート済みなので再ソート不要
        // 差分更新により TabControl の自動スクロールを抑制する
        private void rebuild_filtered_tabs()
        {
            var main_prefixes = new[] { "EA", "RD", "SM" };

            // 今回表示すべきタブの集合を決定（project_tabs の並び順を維持）
            List<project_cost_view_model> target_list;

            if (_selected_group == null || _selected_group.prefix == "")
            {
                // すべて：ソート済みの project_tabs をそのまま使う
                target_list = project_tabs.ToList();
            }
            else if (_selected_group.prefix == "OTHER")
            {
                // その他 = EA/RD/SM 以外
                target_list = project_tabs
                    .Where(t => !main_prefixes.Any(p =>
                        t.category_code.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
            else
            {
                // 特定の大区分（EA / RD / SM）
                target_list = project_tabs
                    .Where(t => t.category_code.StartsWith(
                        _selected_group.prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // ▼▼▼ 差分更新ステップ1：不要なタブを後ろから削除
            for (int i = _filtered_tabs.Count - 1; i >= 0; i--)
            {
                if (!target_list.Contains(_filtered_tabs[i]))
                    _filtered_tabs.RemoveAt(i);
            }

            // ▼▼▼ 差分更新ステップ2：不足タブを追加・順序をMove()で修正
            for (int i = 0; i < target_list.Count; i++)
            {
                if (i < _filtered_tabs.Count)
                {
                    if (_filtered_tabs[i] != target_list[i])
                    {
                        int existing_idx = _filtered_tabs.IndexOf(target_list[i]);
                        if (existing_idx >= 0)
                            _filtered_tabs.Move(existing_idx, i);
                        else
                            _filtered_tabs.Insert(i, target_list[i]);
                    }
                }
                else
                {
                    _filtered_tabs.Add(target_list[i]);
                }
            }

            // tab_number を連番でセット
            for (int i = 0; i < _filtered_tabs.Count; i++)
                _filtered_tabs[i].tab_number = i + 1;

            // 選択タブが新しいリストにあれば維持、なければ先頭
            if (selected_tab == null || !_filtered_tabs.Contains(selected_tab))
                selected_tab = _filtered_tabs.FirstOrDefault();
        }
    }
}
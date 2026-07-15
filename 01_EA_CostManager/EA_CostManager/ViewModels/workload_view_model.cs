using CommunityToolkit.Mvvm.Input; // RelayCommand
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Models;
using EA_CostManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;         // ▼ 追加 [7C-fix5]：明細のグループ化（GroupDescription）
using System.Windows.Data;           // ▼ 追加 [7C-fix5]：CollectionViewSource
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace EA_CostManager.ViewModels
{
    /// <summary>
    /// ▼ 修正 [Sprint 9A]：工数表ページのViewModel
    ///
    /// 【7B からの変更点】
    /// ・案件選択を ComboBox から「大区分（会社）＋ 現場タブ」方式へ変更（原価集計と同じ操作感）。
    ///   毎回コンボから選び直す必要がなくなり、対象案件の有無も一目で分かる。
    /// ・大区分の構築・判定は ProjectGroupService に委譲（原価集計と定義を共通化）。
    ///   ただし selected_group は本VMが独自に保持するため、原価集計側の選択とは連動しない。
    ///
    /// 【役割】
    /// ・業務単位集計（agg_mode='task'）の案件のみを現場タブとして表示
    /// ・WorkloadAggregationService の集計結果を「全体／区分ごと／未分類」のタブに組み立てる
    /// ・区分タブの行 ＝ サブ分類（＋サブ未分類＋合計行）。集計期間は案件の累計
    /// ・業務区分・キーワード・サブ分類の編集は「分類設定」ダイアログで行う（Sprint 7C-1）。
    ///   業務区分は案件ごとに大きく異なるため、固定の標準セットは配らず、
    ///   案件ごとに区分を作る（他案件からのコピーも可能）方式としている。
    /// </summary>
    public class workload_view_model : base_view_model
    {
        // ▼ 追加 [7C-fix5]：どのサブ分類にも該当しなかった行のグループ名
        //   集計表の行名と同じ文字列にして、集計と明細の対応が分かるようにする。
        private const string SUBGROUP_NONE = "（サブ未分類）";

        private readonly WorkloadAggregationService _service = new();
        private bool _initialized = false;

        // ============================================================
        // 大区分（会社）— サイドバー
        // ※ 選択状態は本VMが保持するため、原価集計側の大区分選択とは独立している
        // ============================================================
        public ObservableCollection<project_group_item> group_items { get; } = new();

        private project_group_item? _selected_group;
        public project_group_item? selected_group
        {
            get => _selected_group;
            set
            {
                if (_selected_group == value) return;
                _selected_group = value;
                OnPropertyChanged(nameof(selected_group));
                // 選択中の見た目を更新（サイドバーのハイライト）
                foreach (var g in group_items)
                    g.is_selected = g == value;
                // 表示する現場タブを絞り込む
                rebuild_filtered_projects();
            }
        }

        // ============================================================
        // 現場タブ
        // ============================================================
        /// <summary>工数表の対象となる全案件（agg_mode='task'）。大区分フィルタ前の全件</summary>
        private readonly List<workload_project_item> _all_projects = new();

        /// <summary>大区分で絞り込んだ後の現場タブ（画面に表示されるもの）</summary>
        public ObservableCollection<workload_project_item> filtered_projects { get; } = new();

        private workload_project_item? _selected_project;
        public workload_project_item? selected_project
        {
            get => _selected_project;
            set
            {
                if (_selected_project == value) return;
                _selected_project = value;
                OnPropertyChanged(nameof(selected_project));
                // ▼ 修正 [Sprint 9D-2]：ボタン文言の更新は selected_tab の切替時に行う。
                //   現場タブを切り替えると load_workload_async がタブを作り直して
                //   selected_tab を設定し直すため、そちらの通知で足りる。
                // 現場タブを切り替えたら即集計（多重実行は is_loading でガード）
                if (value != null) _ = load_workload_async();
                else tabs.Clear();
            }
        }

        // ============================================================
        // 業務区分タブ（全体／区分ごと／未分類）
        // ============================================================
        public ObservableCollection<workload_tab_item> tabs { get; } = new();

        private workload_tab_item? _selected_tab;
        public workload_tab_item? selected_tab
        {
            get => _selected_tab;
            set
            {
                _selected_tab = value;
                OnPropertyChanged(nameof(selected_tab));
                // ▼ 追加 [Sprint 7D-1]：グループ表示のタブでのみ開閉ボタンを有効にする
                OnPropertyChanged(nameof(can_toggle_expand));
                // ▼ 追加 [Sprint 9D-2]：開閉状態は区分タブごとに持つため、
                //   区分タブの切替でボタンの文言も切替先の状態に合わせる。
                OnPropertyChanged(nameof(expand_toggle_text));
            }
        }

        // ============================================================
        // 状態表示
        // ============================================================
        private bool _is_loading;
        public bool is_loading
        {
            get => _is_loading;
            set { _is_loading = value; OnPropertyChanged(nameof(is_loading)); }
        }

        /// <summary>工数表の対象案件が1件も無い場合 true（案内文の表示用）</summary>
        private bool _has_no_projects = true;
        public bool has_no_projects
        {
            get => _has_no_projects;
            set { _has_no_projects = value; OnPropertyChanged(nameof(has_no_projects)); }
        }

        // ▼ 修正 [7C-fix1]：status_message は base_view_model に定義済みのため重複宣言を削除
        //   （CS0108 警告：継承メンバーを隠していた。継承側をそのまま使用する）

        // ▼ 追加 [Sprint 7D-1]：サブ分類グループの一括開閉
        //   明細のグループ見出し（Expander）の IsExpanded がこの値を参照している。
        //   「すべて展開／すべて折りたたむ」ボタンから切り替える。
        //   バインドは OneWay のため、利用者が個別にグループを開閉してもこの値は変わらず、
        //   ボタンを押したときだけ全グループが一括で切り替わる。
        //
        // ▼ 修正 [Sprint 9D-2]：開閉状態を「現場タブ × 区分タブ」ごとに保持する
        //   経緯：
        //     ・[7D-1] 旧実装は VM に bool を1つ持つだけで、状態が工数表全体で共有されていた。
        //     ・[9D]   案件（現場タブ）ごとに持つようにしたが、まだ案件内で1つだったため、
        //              同じ案件の別の区分タブに切り替えても折りたたまれたままだった。
        //     ・[9D-2] 区分タブごとに独立させる（本実装）。ボタンは「今見ている区分タブ」
        //              にだけ効く。区分タブごとに見たい／畳みたいが変わるため。
        //   状態は各 workload_tab_item が持つ（＝Expander は自分のタブの値だけを見る）が、
        //   タブは再集計のたびに作り直されるため、VM側の辞書にも控えて生成時に復元する。
        //   キー ＝ 案件ID + 区分ID。これにより現場タブを往復しても状態が保たれる。
        //
        // ▼ 修正 [Sprint 7E-2]：折りたたまれているグループのキー集合。
        //   「案件 × 区分タブ × サブ分類」の1グループ単位で持つ。
        //   既定は展開のため、折りたたまれているものだけを入れる（入っていない＝展開）。
        //   DB（workload_collapse_states）にも保存し、再起動後も同じ状態で開けるようにする。
        private readonly HashSet<string> _collapsed_groups = new();

        /// <summary>折りたたみ状態のキー（案件ID＋区分ID＋サブ分類ID）</summary>
        private static string collapse_key(int project_id, int? category_id, int? subgroup_id)
            => $"{project_id}|{category_id?.ToString() ?? "-"}|{subgroup_id?.ToString() ?? "-"}";

        /// <summary>
        /// ▼ 追加 [Sprint 7E]：折りたたみ状態をDBから読み込む。
        /// 表示上の好みであり業務データではないので、失敗しても画面は出す
        /// （その場合は既定の全展開になるだけ）。
        /// </summary>
        private async Task load_collapse_states_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var rows = await conn.QueryAsync<(int project_id, int category_id, int subgroup_id)>(@"
                    SELECT project_id, category_id, subgroup_id FROM workload_collapse_states
                     WHERE pc_user_id = @uid AND is_collapsed = 1",
                    new { uid = UserSession.user_id });

                _collapsed_groups.Clear();
                foreach (var r in rows)
                {
                    int? cat = r.category_id == ViewStateMigration.NO_CATEGORY ? (int?)null : r.category_id;
                    int? sub = r.subgroup_id == ViewStateMigration.NO_SUBGROUP ? (int?)null : r.subgroup_id;
                    _collapsed_groups.Add(collapse_key(r.project_id, cat, sub));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"工数表の折りたたみ状態復元エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// ▼ 修正 [Sprint 7E-2]：1グループ分の折りたたみ状態をDBへ保存する。
        /// 展開に戻したときは行を消す（既定＝展開のため、行を残す意味がない）。
        /// </summary>
        private static async Task save_collapse_state_async(
            int project_id, int? category_id, int? subgroup_id, bool expanded)
        {
            try
            {
                int cat = category_id ?? ViewStateMigration.NO_CATEGORY;
                int sub = subgroup_id ?? ViewStateMigration.NO_SUBGROUP;
                using var conn = database_manager.create_connection();

                if (expanded)
                {
                    await conn.ExecuteAsync(@"
                        DELETE FROM workload_collapse_states
                         WHERE pc_user_id = @uid AND project_id = @pid
                           AND category_id = @cid AND subgroup_id = @sid",
                        new { uid = UserSession.user_id, pid = project_id, cid = cat, sid = sub });
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        INSERT OR REPLACE INTO workload_collapse_states
                            (pc_user_id, project_id, category_id, subgroup_id, is_collapsed, updated_at)
                        VALUES (@uid, @pid, @cid, @sid, 1, datetime('now','localtime'))",
                        new { uid = UserSession.user_id, pid = project_id, cid = cat, sid = sub });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"工数表の折りたたみ状態保存エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// ▼ 追加 [Sprint 7E-2]：グループを1つ作る。
        /// 前回の折りたたみ状態を復元したうえで、以降の開閉を保存へつなぐ。
        /// 見出しクリックによる個別の開閉も、「すべて折りたたむ」ボタンも、
        /// どちらも最終的にここで結んだ expanded_changed を通って保存される。
        /// </summary>
        private workload_group make_group(
            int project_id, int? category_id, int? subgroup_id, string display_text)
        {
            string key = collapse_key(project_id, category_id, subgroup_id);
            var g = new workload_group
            {
                subgroup_id = subgroup_id,
                display_text = display_text,
                is_expanded = !_collapsed_groups.Contains(key),   // 既定は展開
            };
            g.expanded_changed = grp =>
            {
                if (grp.is_expanded) _collapsed_groups.Remove(key);
                else _collapsed_groups.Add(key);

                // 表示上の好みのため、保存の完了を待たずに操作へ反応を返す
                _ = save_collapse_state_async(project_id, category_id, subgroup_id, grp.is_expanded);
                OnPropertyChanged(nameof(expand_toggle_text));   // 全開/全閉の判定が変わりうる
            };
            return g;
        }

        /// <summary>
        /// 開閉ボタンの表示文言（選択中の区分タブの状態と逆の操作を示す）。
        /// ▼ 修正 [Sprint 7E-2]：1つでも閉じていれば「すべて展開」を出す
        /// （個別に閉じたグループがある状態から、まとめて開き直せるようにするため）。
        /// </summary>
        public string expand_toggle_text =>
            (selected_tab?.all_expanded ?? true) ? "⊟ すべて折りたたむ" : "⊞ すべて展開";

        /// <summary>グループ表示中のタブでのみ開閉ボタンを出す</summary>
        public bool can_toggle_expand => selected_tab?.is_grouped == true;

        // ---- 明細行の選択（右クリックからキーワード登録するために保持） ----
        private workload_detail_row? _selected_detail_row;
        public workload_detail_row? selected_detail_row
        {
            get => _selected_detail_row;
            set { _selected_detail_row = value; OnPropertyChanged(nameof(selected_detail_row)); }
        }

        // ---- コマンド ----
        public ICommand reload_command { get; }
        // ▼ 修正 [Sprint 7C-1]：「標準区分セット作成」を廃止し「分類設定」に置き換え
        public ICommand open_classify_command { get; }
        // ▼ 追加 [Sprint 7C-1]：明細行からキーワードを登録する
        public ICommand add_keyword_from_detail_command { get; }
        // ▼ 追加 [Sprint 7D-1]：サブ分類グループの一括開閉
        public ICommand toggle_expand_command { get; }

        public workload_view_model()
        {
            reload_command = new RelayCommand(async () => await reload_async());
            open_classify_command = new RelayCommand(async () => await open_classify_async());
            add_keyword_from_detail_command =
                new RelayCommand(async () => await add_keyword_from_detail_async());
            // ▼ 修正 [Sprint 9D-2]：押すたびに「全展開」と「全折りたたみ」を切り替える。
            //   効かせる対象は「今見ている区分タブ」だけ（他の区分タブ・他の現場タブには波及しない）。
            toggle_expand_command = new RelayCommand(() => toggle_expand());
        }

        // ============================================================
        // サブ分類グループの一括開閉（選択中の区分タブのみ）
        // ============================================================
        /// <summary>
        /// ▼ 追加 [Sprint 9D-2]
        /// 選択中の区分タブについて、サブ分類グループをまとめて開閉する。
        /// タブ自身の all_expanded を書き換えることで、そのタブの Expander だけが反応する。
        /// 併せてVM側の辞書にも控え、再集計・現場タブ往復後も状態が戻るようにする。
        /// </summary>
        private void toggle_expand()
        {
            var tab = selected_tab;
            if (tab == null || tab.groups.Count == 0) return;

            // 1つでも閉じていれば「すべて展開」、全部開いていれば「すべて折りたたむ」
            bool next = !tab.all_expanded;

            // ▼ 修正 [Sprint 7E-2]：各グループへ反映する。
            //   保存は make_group で結んだ expanded_changed が行うため、ここでは呼ばない
            //  （個別クリックと同じ経路を通す＝保存漏れが起きない）。
            foreach (var g in tab.groups) g.is_expanded = next;

            OnPropertyChanged(nameof(expand_toggle_text));   // ボタン文言を更新
        }

        // ============================================================
        // 分類設定ダイアログを開く（Sprint 7C-1）
        // ============================================================
        /// <summary>
        /// 業務区分・キーワード・サブ分類を編集するダイアログを開く。
        /// ダイアログは保存反映方式のため、「保存」が押された場合のみ再集計する。
        /// </summary>
        private async Task open_classify_async()
        {
            var project = selected_project;
            if (project == null || is_loading) return;

            var dlg = new Views.WorkloadClassifyDialog(project.id, project.display_name)
            {
                Owner = Application.Current?.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.IsActive),
            };
            if (dlg.ShowDialog() == true)
                await load_workload_async();   // 保存された場合のみ再集計
        }

        // ============================================================
        // 明細行からキーワードを登録する（Sprint 7C-1）
        // ============================================================
        /// <summary>
        /// 明細行（未分類・各区分タブ共通）を右クリックして開く。
        /// 業務内容を見ながらキーワードを切り出して登録できる。
        /// こちらは登録した時点でDBに反映される（次々に追加する使い方を想定）。
        /// </summary>
        private async Task add_keyword_from_detail_async()
        {
            var project = selected_project;
            var row = selected_detail_row;
            if (project == null || row == null || is_loading) return;

            var dlg = new Views.WorkloadKeywordDialog(project.id, row.task)
            {
                Owner = Application.Current?.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.IsActive),
            };
            if (dlg.ShowDialog() == true)
                await load_workload_async();   // 登録された場合のみ再集計
        }

        // ============================================================
        // 初期化（ページ初回表示時に1回だけ実行）
        // ============================================================
        public async Task ensure_initialized_async()
        {
            if (_initialized) return;
            _initialized = true;
            // ▼ 追加 [Sprint 7E]：タブを組み立てる前に、前回の折りたたみ状態を読み込む
            //   （タブ生成時に _collapsed_groups から復元されるため、順序が重要）
            await load_collapse_states_async();
            await load_projects_async();
        }

        /// <summary>案件一覧の再読込＋現在の案件を再集計</summary>
        private async Task reload_async()
        {
            await load_projects_async();
        }

        // ============================================================
        // 案件一覧の読み込み（業務単位集計（agg_mode='task'）の有効案件のみ）
        // ============================================================
        private async Task load_projects_async()
        {
            if (is_loading) return;
            is_loading = true;
            try
            {
                int? keep_project_id = _selected_project?.id;      // 再読込時は選択を維持する
                string? keep_group_prefix = _selected_group?.prefix;

                using var conn = database_manager.create_connection();

                // ▼ 修正 [9A-fix2]：業務単位集計の設定先は2箇所あるため、両方を見る
                //   ・全件タブに適用した場合   → projects.agg_mode に保存される
                //   ・絞り込みタブ（複製タブ等）に適用した場合
                //                              → cost_filter_tabs.agg_mode に保存される
                //   （project_cost_view_model.apply_mode_to_current_async の分岐に対応）
                //   旧実装は projects.agg_mode しか見ておらず、実際の運用（複製タブで
                //   業務単位にする）では対象が0件になっていた。
                //   DISTINCT を付けているのは、1つの現場に業務単位の絞り込みタブが
                //   複数ある場合に現場タブが重複して並ぶのを防ぐため。
                //   is_archived = 0 の条件で、アーカイブ済みの絞り込みタブは対象外とする。
                var rows = (await conn.QueryAsync<workload_project_item>(@"
                    SELECT DISTINCT
                           p.id, p.category_code, p.site_name, p.company_name,
                           p.detail, p.tab_color, p.sort_order
                    FROM projects p
                    LEFT JOIN cost_filter_tabs f
                           ON f.project_id = p.id
                          AND f.agg_mode = 'task'
                          AND f.is_archived = 0
                    WHERE p.is_active = 1
                      AND (p.agg_mode = 'task' OR f.id IS NOT NULL)
                    ORDER BY p.sort_order, p.id")).ToList();

                _all_projects.Clear();
                _all_projects.AddRange(rows);

                has_no_projects = _all_projects.Count == 0;

                // ---- 大区分を構築（ProjectGroupService に委譲＝原価集計と同一定義） ----
                var groups = ProjectGroupService.build_groups(
                    _all_projects.Select(p => p.category_code));

                group_items.Clear();
                foreach (var (label, prefix, count) in groups)
                    group_items.Add(new project_group_item
                    {
                        label = label,
                        prefix = prefix,
                        count = count,
                    });

                // 前回選択していた大区分を維持。無ければ先頭（＝すべて）
                var target_group =
                    group_items.FirstOrDefault(g => g.prefix == keep_group_prefix)
                    ?? group_items.FirstOrDefault();

                // setter を経由せずに設定する
                // （setter 経由だと選択案件の維持より先に rebuild が走ってしまうため、
                //   ここでは値だけ入れて、直後に keep_project_id 付きで明示的に再構築する）
                _selected_group = target_group;
                OnPropertyChanged(nameof(selected_group));
                foreach (var g in group_items)
                    g.is_selected = g == target_group;

                rebuild_filtered_projects(keep_project_id);
            }
            catch (Exception ex)
            {
                status_message = $"案件一覧の読み込みに失敗いたしました：{ex.Message}";
            }
            finally
            {
                is_loading = false;
            }
        }

        /// <summary>
        /// 選択中の大区分で現場タブを絞り込む。
        /// 判定は ProjectGroupService.matches に委譲する
        /// （大区分の構築とフィルタの判定基準を必ず一致させるため）。
        /// </summary>
        /// <param name="keep_project_id">維持したい選択案件のID（省略時は現在の選択を維持）</param>
        private void rebuild_filtered_projects(int? keep_project_id = null)
        {
            int? keep = keep_project_id ?? _selected_project?.id;
            string prefix = _selected_group?.prefix ?? ProjectGroupService.PREFIX_ALL;

            var target = _all_projects
                .Where(p => ProjectGroupService.matches(p.category_code, prefix))
                .ToList();

            filtered_projects.Clear();
            foreach (var p in target)
                filtered_projects.Add(p);

            if (filtered_projects.Count == 0)
            {
                // この大区分には対象案件が無い
                selected_project = null;   // setter 経由で tabs もクリアされる
                status_message = has_no_projects
                    ? ""
                    : "この区分には工数表の対象案件（業務単位集計）がございません。";
                return;
            }

            // 前回の選択を維持。大区分の切替で消えた場合は先頭を選択
            var next = filtered_projects.FirstOrDefault(p => p.id == keep) ?? filtered_projects[0];

            if (_selected_project != null && _selected_project.id == next.id)
            {
                // 同じ案件が選ばれ続ける場合は setter が発火しないため、明示的に再集計する
                // （再読込ボタンで最新のDB内容を反映させるために必要）
                _selected_project = next;
                OnPropertyChanged(nameof(selected_project));
                _ = load_workload_async();
            }
            else
            {
                selected_project = next;   // setter 経由で集計が走る
            }
        }

        // ============================================================
        // 集計本体：サービスの結果をタブ・表示行に組み立てる
        // ============================================================
        public async Task load_workload_async()
        {
            var project = selected_project;
            if (project == null) return;

            is_loading = true;
            try
            {
                var result = await _service.aggregate_async(project.id);
                if (!string.IsNullOrEmpty(result.error_message))
                {
                    tabs.Clear();
                    status_message = result.error_message;
                    return;
                }

                var new_tabs = new List<workload_tab_item>();

                // ---- [全体] タブ：行＝業務区分（＋未分類＋合計） ----
                var overall = new workload_tab_item { name = "全体", is_detail = false };
                foreach (var cat in result.categories)
                {
                    var rows = result.rows.Where(r => r.category_id == cat.id);
                    overall.summary_rows.Add(make_summary_row(cat.name, rows));
                }
                overall.summary_rows.Add(
                    make_summary_row("未分類", result.rows.Where(r => r.category_id == null)));
                overall.summary_rows.Add(make_summary_row("合計", result.rows, is_total: true));
                new_tabs.Add(overall);

                // ---- 区分ごとのタブ ----
                // ▼ 修正 [9A-fix3]：集計表と明細一覧の「両方」を持たせる。
                //   旧実装はサブ分類の集計行のみだったため、サブ分類が未登録の区分では
                //   合計行しか表示されず中身が見えなかった。
                //   上段＝サブ分類別の集計（未登録なら非表示）、下段＝業務行の明細一覧。
                foreach (var cat in result.categories)
                {
                    // ▼ 追加 [Sprint 9D-2]：開閉状態の保存キーに使うため区分IDを持たせる
                    var tab = new workload_tab_item
                    {
                        name = cat.name,
                        is_detail = false,
                        category_id = cat.id,
                    };
                    var cat_rows = result.rows.Where(r => r.category_id == cat.id).ToList();

                    // --- 上段：サブ分類別の集計表 ---
                    // サブ分類セット（モード解決済み）は models 側の共通ロジックで取得
                    // → 集計時の振り分けと表示行のセットが必ず一致する
                    var sub_set = result.get_subgroup_set(cat.id);
                    foreach (var sub in sub_set)
                    {
                        tab.summary_rows.Add(make_summary_row(
                            sub.name, cat_rows.Where(r => r.subgroup_id == sub.id)));
                    }
                    if (sub_set.Count > 0)
                    {
                        // サブ分類を使う区分のみ「サブ未分類」行を出す
                        tab.summary_rows.Add(make_summary_row(
                            "（サブ未分類）", cat_rows.Where(r => r.subgroup_id == null)));
                    }
                    tab.summary_rows.Add(make_summary_row("合計", cat_rows, is_total: true));

                    // --- 下段：この区分に振り分けられた業務行の明細一覧 ---
                    // ▼ 修正 [7C-fix5]：サブ分類ごとにグループ化して並べる。
                    //   集計Excelの区分シート（橋名ごとに明細がまとまっている形）を再現する。
                    //   サブ分類の登録順に並べ、最後に「（サブ未分類）」を置く。
                    //   サブ分類を使わない区分（mode=none や未登録）はグループ名を空にし、
                    //   グループ化せずに従来どおり日付順で並べる。
                    if (sub_set.Count > 0)
                    {
                        // サブ分類ごとに、その分類に属する行だけを順に詰める。
                        // グループ見出しには小計（件数・時間・人日・原価）を含めた文字列を入れる。
                        // ※ XAML 側でグループ内の行を再集計すると、表示用文字列からの
                        //    数値復元が必要になり誤差やバグの原因になるため、
                        //    ここで正確な数値から見出しを作ってしまう。
                        foreach (var sub in sub_set)
                        {
                            var rows_of_sub = cat_rows.Where(r => r.subgroup_id == sub.id).ToList();
                            if (rows_of_sub.Count == 0) continue;   // 該当0件のサブ分類は明細に出さない

                            // ▼ 修正 [Sprint 7E-2]：グループ1つにつきオブジェクトを1つ作り、
                            //   そのグループの全行に同じインスタンスを持たせる（＝グループキー）
                            var g = make_group(project.id, cat.id, sub.id,
                                               make_group_header(sub.name, rows_of_sub));
                            tab.groups.Add(g);
                            foreach (var r in rows_of_sub)
                                tab.detail_rows.Add(make_detail_row(r, g));
                        }

                        // どのサブ分類にも該当しなかった行
                        var rows_none = cat_rows.Where(r => r.subgroup_id == null).ToList();
                        if (rows_none.Count > 0)
                        {
                            // サブ分類IDを持たないグループ（保存時は NO_SUBGROUP になる）
                            var g = make_group(project.id, cat.id, null,
                                               make_group_header(SUBGROUP_NONE, rows_none));
                            tab.groups.Add(g);
                            foreach (var r in rows_none)
                                tab.detail_rows.Add(make_detail_row(r, g));
                        }

                        tab.is_grouped = true;   // 明細をグループ表示する
                    }
                    else
                    {
                        // サブ分類なし → グループ化せず日付順のまま
                        foreach (var r in cat_rows)
                            tab.detail_rows.Add(make_detail_row(r));
                    }

                    // ▼ 追加 [7C-fix2]：明細の合計を計算する（明細一覧の下端に表示）
                    set_detail_total(tab, cat_rows);

                    // ▼ 修正 [Sprint 7E-2]：開閉状態の復元は make_group が行うため、ここでは不要
                    //   （タブは再集計のたびに作り直されるが、_collapsed_groups から戻る）

                    // タブ名に件数を付けて、どの区分にどれだけ入ったかを一目で分かるようにする
                    tab.name = $"{cat.name} ({cat_rows.Count})";
                    new_tabs.Add(tab);
                }

                // ---- [未分類] タブ：区分判定できなかった日報の明細一覧 ----
                // ▼ 修正 [9A-fix3]：明細行の生成を make_detail_row に共通化
                //   （区分タブと未分類タブで同じ列・同じ書式にするため）
                var uncls = new workload_tab_item { name = "未分類", is_detail = true };
                var uncls_rows = result.rows.Where(r => r.category_id == null).ToList();
                foreach (var r in uncls_rows)
                    uncls.detail_rows.Add(make_detail_row(r));
                // ▼ 追加 [7C-fix2]：未分類の明細にも合計を出す
                set_detail_total(uncls, uncls_rows);
                uncls.name = $"未分類 ({uncls.detail_rows.Count})";
                new_tabs.Add(uncls);

                // タブ差し替え（選択は先頭＝全体に戻す）
                tabs.Clear();
                foreach (var t in new_tabs) tabs.Add(t);
                selected_tab = tabs.FirstOrDefault();

                // ステータスバーのサマリ
                decimal total_cost = result.rows.Sum(r => r.total_cost);
                double eng = result.rows.Sum(r => r.engineer_days);
                double ast = result.rows.Sum(r => r.assistant_days);
                double unk = result.rows.Sum(r => r.unknown_days);
                status_message =
                    $"総原価 {total_cost:N0} 円／延べ人数 技師 {eng:0.##}・助手 {ast:0.##}" +
                    (unk > 0 ? $"・不明 {unk:0.##}" : "") +
                    $"　（業務行 {result.rows.Count} 件）" +
                    (result.categories.Count == 0
                        ? "　※業務区分が未登録です。「⚙ 分類設定」から区分を作成してください。" : "");
            }
            catch (Exception ex)
            {
                status_message = $"集計に失敗いたしました：{ex.Message}";
            }
            finally
            {
                is_loading = false;
            }
        }

        // ▼ 追加 [7C-fix5]：明細のグループ見出し文字列を作る
        //   例：「笙の川　121 件　／　1,000 h　／　81.81 人日　／　2,396,050 円」
        //   小計をここで確定させることで、XAML側での再集計（表示文字列からの数値復元）を
        //   不要にし、値のズレやパースエラーを防いでいる。
        private static string make_group_header(string name, IList<workload_task_row> rows)
        {
            double hours = rows.Sum(r => r.hours);
            double days = rows.Sum(r => r.engineer_days + r.assistant_days + r.unknown_days);
            decimal cost = rows.Sum(r => r.total_cost);
            return $"{name}　　{rows.Count} 件　／　{hours:0.##} h　／　{days:0.####} 人日　／　{cost:N0} 円";
        }

        // ▼ 追加 [7C-fix2]：明細一覧の合計（時間・人日・原価）をタブに設定する
        //   人日は技師/助手/不明のいずれか1つにしか入らないため、3つを足すと総人日になる。
        //   ここで出す原価の合計は、その区分の集計表の「合計」行と必ず一致する
        //   （同じ業務行の集合から計算しているため）。
        private static void set_detail_total(workload_tab_item tab, IList<workload_task_row> rows)
        {
            double hours = rows.Sum(r => r.hours);
            double days = rows.Sum(r => r.engineer_days + r.assistant_days + r.unknown_days);
            decimal cost = rows.Sum(r => r.total_cost);
            tab.detail_total_hours = hours.ToString("0.##");
            tab.detail_total_days = days.ToString("0.####");
            tab.detail_total_cost = cost.ToString("N0");
        }

        // ▼ 追加 [9A-fix3]：業務行から明細表示行を作る
        //   区分タブ・未分類タブの両方で使用し、列と書式を統一する。
        //   人日は技師/助手/不明のいずれか1つにしか入らないため、単純加算で総人日になる。
        private static workload_detail_row make_detail_row(
            workload_task_row r, workload_group? group = null)
        {
            return new workload_detail_row
            {
                group = group,   // ▼ 修正 [Sprint 7E-2]：null＝グループ化しない
                record_date = r.record_date,
                employee_name = r.employee_name,
                job_type = r.job_type,
                task = r.task,
                hours_display = r.hours.ToString("0.##"),
                days_display = (r.engineer_days + r.assistant_days + r.unknown_days).ToString("0.####"),
                cost_display = r.total_cost.ToString("N0"),
            };
        }

        /// <summary>行の集合から集計表示行（原価・人日）を作る</summary>
        private static workload_summary_row make_summary_row(
            string name, IEnumerable<workload_task_row> rows, bool is_total = false)
        {
            var list = rows as IList<workload_task_row> ?? rows.ToList();
            decimal cost = list.Sum(r => r.total_cost);
            double eng = list.Sum(r => r.engineer_days);
            double ast = list.Sum(r => r.assistant_days);
            double unk = list.Sum(r => r.unknown_days);
            return new workload_summary_row
            {
                name = name,
                is_total = is_total,
                cost_display = cost.ToString("N0"),
                engineer_display = eng.ToString("0.##"),
                assistant_display = ast.ToString("0.##"),
                unknown_display = unk.ToString("0.##"),
                row_count = list.Count,
            };
        }

    }

    // ================================================================
    // 表示用の補助クラス
    // ================================================================

    /// <summary>
    /// ▼ 修正 [Sprint 9A]：工数表の現場タブ1つ分
    /// 現場情報バー・タブの色帯を原価集計と揃えるため、company_name / detail / tab_color を追加
    /// </summary>
    public class workload_project_item
    {
        public int id { get; set; }
        public string category_code { get; set; } = "";
        public string site_name { get; set; } = "";
        public string company_name { get; set; } = "";
        public string detail { get; set; } = "";
        /// <summary>タブ左端の色帯（原価集計と同じ tab_color を使用）</summary>
        public string tab_color { get; set; } = "";
        // ▼ 追加 [9A-fix2]：現場タブの並び順（原価集計のタブ順と揃えるため）
        public int sort_order { get; set; }

        /// <summary>タブに表示する名称（例：EA45_北陸_日本ピーエス）</summary>
        public string tab_name => string.IsNullOrEmpty(site_name)
            ? category_code
            : $"{category_code}_{site_name}";

        /// <summary>ダイアログ等で使う表示名</summary>
        public string display_name => $"{category_code}　{site_name}";
    }

    /// <summary>
    /// ▼ 修正 [9A-fix3]：工数表のタブ1枚分
    /// タブの種類ごとに、集計表と明細一覧のどちらを（あるいは両方を）出すかが変わる。
    ///   [全体]   … 集計表のみ（行＝業務区分）
    ///   [区分]   … 集計表（サブ分類別。未登録なら非表示）＋ 明細一覧
    ///   [未分類] … 明細一覧のみ
    /// 表示可否は has_summary / has_detail で判定する（コレクションの中身の有無で決まる）。
    /// </summary>
    /// ▼ 修正 [Sprint 7E-2]：INotifyPropertyChanged を外した。
    ///   開閉状態を持っていた all_expanded が「グループ側の状態から算出する値」に
    ///   変わり、変更を通知するプロパティが1つも無くなったため。
    ///   タブの各プロパティは、タブを tabs へ追加する前に確定する（＝通知不要）。
    public class workload_tab_item
    {
        public string name { get; set; } = "";

        /// <summary>true＝明細のみのタブ（未分類）。集計表は出さない</summary>
        public bool is_detail { get; set; }

        // ▼ 追加 [Sprint 9D-2]：このタブが表す業務区分のID
        //   開閉状態の保存キーに使う。[全体]・[未分類]タブは区分に紐づかないため null。
        //   （両タブは is_grouped = false でグループ表示しないため開閉の対象外）
        public int? category_id { get; set; }

        // ▼ 修正 [Sprint 7E-2]：このタブのサブ分類グループ一覧（明細の表示順）。
        //   開閉状態は各 workload_group が個別に持つ。
        //   旧実装はタブに bool を1つ持つだけ（all_expanded）で、
        //   Expander とは OneWay で結んでいたため、
        //   ・個別に開閉しても状態を取得できない
        //   ・したがって個別の開閉を保存できない
        //   という制限があった。グループごとに状態を持たせて双方向で結ぶ。
        public List<workload_group> groups { get; } = new();

        /// <summary>このタブのグループがすべて開いているか（ボタン文言の判定に使う）</summary>
        public bool all_expanded => groups.Count > 0 && groups.All(g => g.is_expanded);

        public ObservableCollection<workload_summary_row> summary_rows { get; } = new();
        public ObservableCollection<workload_detail_row> detail_rows { get; } = new();

        /// <summary>
        /// 集計表を表示するか。
        /// 未分類タブは常に非表示。区分タブでは、サブ分類が未登録だと行が「合計」1件だけに
        /// なり情報量が無いため、2行以上（＝サブ分類が1つ以上ある）場合のみ表示する。
        /// </summary>
        public bool has_summary => !is_detail && summary_rows.Count > 1;

        /// <summary>明細一覧を表示するか（明細行が1件でもあれば表示）</summary>
        public bool has_detail => detail_rows.Count > 0;

        /// <summary>[全体]タブ用：集計表のみで明細を持たないタブか</summary>
        public bool is_overall => !is_detail && detail_rows.Count == 0;

        /// <summary>[全体]タブの集計表は行数に関係なく常に表示する</summary>
        public bool show_summary => is_overall || has_summary;

        // ▼ 追加 [7C-fix2]：明細一覧の合計（区分タブ・未分類タブの下端に表示する）
        //   Excelの各区分シートと同様、明細の一番下に総計が並ぶようにする。
        //   DataGrid の行として混ぜると並び替えで合計行が動いてしまうため、
        //   グリッドの外（直下）に独立した合計バーとして表示する。
        public string detail_total_hours { get; set; } = "";
        public string detail_total_days { get; set; } = "";
        public string detail_total_cost { get; set; } = "";
        /// <summary>明細の件数（合計バーに「n 件」と表示する）</summary>
        public int detail_count => detail_rows.Count;

        // ▼ 追加 [7C-fix5]：明細をサブ分類ごとにグループ表示するか
        //   true のとき、明細一覧にサブ分類ごとの見出し（小計付き）が入る。
        //   サブ分類が未登録の区分や未分類タブでは false（従来どおり日付順の一覧）。
        public bool is_grouped { get; set; }

        private CollectionViewSource? _grouped_detail;
        /// <summary>
        /// ▼ 追加 [7C-fix5]：明細一覧の表示ソース。
        /// is_grouped が true のときだけ workload_group ごとにグループ化する。
        /// グループ見出しの文字列（小計込み）は VM 側で組み立て済みのため、
        /// XAML では見出しをそのまま表示するだけでよい。
        /// ※ グループ化しない場合も同じ CollectionViewSource を通す。
        ///    こうすることで XAML 側のバインド先を1つにでき、
        ///    タブの種類による分岐が不要になる。
        /// </summary>
        public CollectionViewSource grouped_detail
        {
            get
            {
                if (_grouped_detail == null)
                {
                    _grouped_detail = new CollectionViewSource { Source = detail_rows };
                    if (is_grouped)
                    {
                        // ▼ 修正 [Sprint 7E-2]：グループキーは workload_group オブジェクト。
                        //   同じグループの行には同一インスタンスを入れているため、
                        //   参照の同一性でグループがまとまる。
                        //   CollectionViewGroup.Name にこのインスタンスが入る。
                        _grouped_detail.GroupDescriptions.Add(
                            new PropertyGroupDescription(nameof(workload_detail_row.group)));
                    }
                }
                return _grouped_detail;
            }
        }
    }

    /// <summary>▼ 追加 [Sprint 7B]：集計表タブの1行（区分 or サブ分類単位の集計）</summary>
    public class workload_summary_row
    {
        public string name { get; set; } = "";
        public string cost_display { get; set; } = "";
        public string engineer_display { get; set; } = "";
        public string assistant_display { get; set; } = "";
        public string unknown_display { get; set; } = "";
        public int row_count { get; set; }
        /// <summary>合計行（太字表示用）</summary>
        public bool is_total { get; set; }
    }

    /// <summary>
    /// ▼ 追加 [Sprint 7E-2]：明細のサブ分類グループ（見出し＋開閉状態）。
    ///
    /// 明細行はこのオブジェクトでグループ化する（同じグループの行は同一インスタンスを参照）。
    /// WPF の CollectionViewGroup.Name にこのインスタンスが入るため、
    /// XAML からは Name.display_text（見出し）と Name.is_expanded（開閉）を直接触れる。
    ///
    /// 【なぜ文字列でグループ化しないか】
    /// 以前はグループキーが「笙の川　121 件／…／2,396,050 円」という小計込みの
    /// 見出し文字列そのものだった。集計値が変わればキーも変わるため、
    /// 開閉状態を覚えておくための安定した目印にできなかった。
    /// サブ分類ID を持たせることで、データが変わっても同じグループだと分かる。
    /// </summary>
    public class workload_group : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>サブ分類ID。null＝「（サブ未分類）」グループ</summary>
        public int? subgroup_id { get; init; }

        /// <summary>グループ見出し（小計込みの文字列）</summary>
        public string display_text { get; init; } = "";

        private bool _is_expanded = true;
        /// <summary>
        /// 開いているか。XAML の Expander と双方向で結ぶ。
        /// 利用者が見出しをクリックして開閉すると、ここに入ってくる
        /// （以前は OneWay だったため、個別の開閉を保存できなかった）。
        /// </summary>
        public bool is_expanded
        {
            get => _is_expanded;
            set
            {
                if (_is_expanded == value) return;
                _is_expanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(is_expanded)));
                expanded_changed?.Invoke(this);
            }
        }

        /// <summary>開閉が変わったことをVMへ伝える（保存とボタン文言の更新に使う）</summary>
        public Action<workload_group>? expanded_changed;
    }

    /// <summary>▼ 追加 [Sprint 7B]：未分類タブの明細1行</summary>
    public class workload_detail_row
    {
        // ▼ 修正 [Sprint 7E-2]：この業務行が属するサブ分類グループ（明細のグループ化に使う）。
        //   サブ分類を使わない区分・未分類タブでは null のままとし、グループ化しない。
        //   以前は見出し文字列（subgroup_name）でグループ化していたが、
        //   小計込みの文字列はデータが変わると変化し、開閉状態の目印にできなかった。
        public workload_group? group { get; set; }

        public string record_date { get; set; } = "";
        public string employee_name { get; set; } = "";
        public string job_type { get; set; } = "";
        public string task { get; set; } = "";
        public string hours_display { get; set; } = "";
        public string days_display { get; set; } = "";
        public string cost_display { get; set; } = "";
    }
}
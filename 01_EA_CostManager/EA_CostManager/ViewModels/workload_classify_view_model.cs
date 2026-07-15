using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Models;
using EA_CostManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace EA_CostManager.ViewModels
{
    // ================================================================
    // ▼ 追加 [Sprint 7C-1]：分類設定ダイアログの編集用モデル
    //   DBのモデル（workload_category 等）をそのまま編集すると、
    //   キャンセル時に元へ戻すのが難しい。そのため編集専用のクラスを用意し、
    //   「保存」を押したときにだけDBへ反映する（保存反映方式）。
    // ================================================================

    /// <summary>編集中の業務区分1件</summary>
    public class edit_category : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void notify([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        /// <summary>DB上のID。新規追加した区分は 0（保存時にINSERTされる）</summary>
        public int id { get; set; }

        private string _name = "";
        /// <summary>区分名。この語がそのまま第1キーワードとして判定に使われる</summary>
        public string name
        {
            get => _name;
            set { if (_name == value) return; _name = value; notify(); name_changed?.Invoke(); }
        }

        private bool _use_name_as_keyword = true;
        /// <summary>
        /// 区分名をキーワードとして使うか。
        /// 「その他」のように区分名が判定語として機能しない区分では false にする。
        /// </summary>
        public bool use_name_as_keyword
        {
            get => _use_name_as_keyword;
            set { if (_use_name_as_keyword == value) return; _use_name_as_keyword = value; notify(); name_changed?.Invoke(); }
        }

        /// <summary>追加キーワード（区分名以外に拾いたい語）</summary>
        public ObservableCollection<edit_keyword> keywords { get; } = new();

        private int _hit_count;
        /// <summary>現在のキーワード設定で何件の業務行が該当するか（保存前のプレビュー）</summary>
        public int hit_count
        {
            get => _hit_count;
            set { if (_hit_count == value) return; _hit_count = value; notify(); }
        }

        /// <summary>区分名・キーワード設定が変わったときに件数を再計算させるための通知</summary>
        public Action? name_changed;
    }

    /// <summary>編集中のキーワード1件</summary>
    public class edit_keyword : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void notify([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public int id { get; set; }
        public string keyword { get; set; } = "";

        private int _hit_count;
        /// <summary>このキーワード単体で何件の業務行が該当するか</summary>
        public int hit_count
        {
            get => _hit_count;
            set { if (_hit_count == value) return; _hit_count = value; notify(); }
        }
    }

    /// <summary>編集中のサブ分類1件</summary>
    public class edit_subgroup : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void notify([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public int id { get; set; }

        /// <summary>NULL＝案件共通セット／値あり＝その区分専用セット</summary>
        public int? category_id { get; set; }

        private string _name = "";
        public string name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                notify();
                changed?.Invoke();   // ▼ 追加 [7C-fix4]：キーワード空欄時は名前で判定するため
            }
        }

        private string _keywords_text = "";
        /// <summary>
        /// キーワードをカンマ区切りで編集する。
        /// 空欄の場合はサブ分類名そのものをキーワードとして扱う
        /// （区分名＝キーワードの考え方をサブ分類にも適用する）。
        /// </summary>
        public string keywords_text
        {
            get => _keywords_text;
            set
            {
                if (_keywords_text == value) return;
                _keywords_text = value;
                notify();
                changed?.Invoke();   // ▼ 追加 [7C-fix4]：該当件数を再計算
            }
        }

        private int _hit_count;
        /// <summary>
        /// ▼ 追加 [7C-fix4]：このサブ分類のキーワードが何件の業務行に該当するか。
        /// 保存前に効果を確認できるようにする（区分の該当件数と同じ考え方）。
        /// </summary>
        public int hit_count
        {
            get => _hit_count;
            set { if (_hit_count == value) return; _hit_count = value; notify(); }
        }

        /// <summary>名前やキーワードが変わったときに該当件数を再計算させるための通知</summary>
        public Action? changed;

        /// <summary>実際に判定に使うキーワードの一覧を取り出す</summary>
        public List<string> resolve_keywords()
        {
            var list = (keywords_text ?? "")
                .Split(new[] { ',', '，', '、' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            // 未入力ならサブ分類名を使う
            if (list.Count == 0 && !string.IsNullOrWhiteSpace(name))
                list.Add(name.Trim());
            return list;
        }
    }

    // ================================================================
    // ▼ 追加 [Sprint 7C-1]：分類設定ダイアログのViewModel
    // ================================================================
    public class workload_classify_view_model : base_view_model
    {
        private readonly int _project_id;

        /// <summary>
        /// 該当件数プレビュー用の業務内容一覧（正規化済み）。
        /// 対象案件の日報を「日付×作業者×業務内容」で束ねた業務行の内容。
        /// 工数表の集計と同じ粒度なので、ここで数えた件数は実際の集計件数と一致する。
        /// </summary>
        private List<string> _normalized_tasks = new();

        public string project_title { get; }

        public ObservableCollection<edit_category> categories { get; } = new();
        public ObservableCollection<edit_subgroup> subgroups { get; } = new();

        /// <summary>削除された区分・キーワード・サブ分類のID（保存時にDELETEする）</summary>
        private readonly List<int> _deleted_category_ids = new();
        private readonly List<int> _deleted_keyword_ids = new();
        private readonly List<int> _deleted_subgroup_ids = new();

        // ---- 選択状態 ----
        private edit_category? _selected_category;
        public edit_category? selected_category
        {
            get => _selected_category;
            set
            {
                if (_selected_category == value) return;
                _selected_category = value;
                OnPropertyChanged(nameof(selected_category));
                OnPropertyChanged(nameof(has_selection));
                OnPropertyChanged(nameof(is_mode_common));
                OnPropertyChanged(nameof(is_mode_custom));
                OnPropertyChanged(nameof(is_mode_none));
                OnPropertyChanged(nameof(show_subgroups));
                refresh_visible_subgroups(); // ▼ 修正 [7C-fix1]
            }
        }

        public bool has_selection => _selected_category != null;

        private edit_keyword? _selected_keyword;
        public edit_keyword? selected_keyword
        {
            get => _selected_keyword;
            set { _selected_keyword = value; OnPropertyChanged(nameof(selected_keyword)); }
        }

        private edit_subgroup? _selected_subgroup;
        public edit_subgroup? selected_subgroup
        {
            get => _selected_subgroup;
            set { _selected_subgroup = value; OnPropertyChanged(nameof(selected_subgroup)); }
        }

        // ---- 新規キーワードの入力欄 ----
        private string _new_keyword = "";
        public string new_keyword
        {
            get => _new_keyword;
            set
            {
                if (_new_keyword == value) return;
                _new_keyword = value;
                OnPropertyChanged(nameof(new_keyword));
                update_new_keyword_hint();   // 入力の都度、該当件数をプレビュー
            }
        }

        private string _new_keyword_hint = "";
        /// <summary>入力中のキーワードが何件に該当するかのヒント表示</summary>
        public string new_keyword_hint
        {
            get => _new_keyword_hint;
            set { _new_keyword_hint = value; OnPropertyChanged(nameof(new_keyword_hint)); }
        }

        // ▼ 修正 [7C-fix1]：status_message は base_view_model に定義済みのため重複宣言を削除
        //   （CS0108 警告：継承メンバーを隠していた。継承側をそのまま使用する）

        // ---- サブ分類のモード ----
        // 選択中の区分ごとに保持する（category_id → mode）
        private readonly Dictionary<int, string> _mode_by_category = new();
        /// <summary>新規区分（id=0）のモードを一時的に保持するためのキー採番用</summary>
        private int _temp_id_seq = -1;

        private string current_mode
        {
            get
            {
                if (_selected_category == null) return workload_subgroup_mode.MODE_COMMON;
                int key = mode_key(_selected_category);
                return _mode_by_category.TryGetValue(key, out var m)
                    ? m : workload_subgroup_mode.MODE_COMMON;
            }
            set
            {
                if (_selected_category == null) return;
                _mode_by_category[mode_key(_selected_category)] = value;
                OnPropertyChanged(nameof(is_mode_common));
                OnPropertyChanged(nameof(is_mode_custom));
                OnPropertyChanged(nameof(is_mode_none));
                OnPropertyChanged(nameof(show_subgroups));
                refresh_visible_subgroups(); // ▼ 修正 [7C-fix1]
            }
        }

        /// <summary>新規区分はDBのIDを持たないため、負の一時IDでモードを管理する</summary>
        private readonly Dictionary<edit_category, int> _temp_ids = new();
        private int mode_key(edit_category cat)
        {
            if (cat.id != 0) return cat.id;
            if (!_temp_ids.TryGetValue(cat, out int t))
            {
                t = _temp_id_seq--;
                _temp_ids[cat] = t;
            }
            return t;
        }

        public bool is_mode_common
        {
            get => current_mode == workload_subgroup_mode.MODE_COMMON;
            set { if (value) current_mode = workload_subgroup_mode.MODE_COMMON; }
        }
        public bool is_mode_custom
        {
            get => current_mode == workload_subgroup_mode.MODE_CUSTOM;
            set { if (value) current_mode = workload_subgroup_mode.MODE_CUSTOM; }
        }
        public bool is_mode_none
        {
            get => current_mode == workload_subgroup_mode.MODE_NONE;
            set { if (value) current_mode = workload_subgroup_mode.MODE_NONE; }
        }

        /// <summary>サブ分類の一覧を表示するか（「使わない」モードでは隠す）</summary>
        public bool show_subgroups =>
            has_selection && current_mode != workload_subgroup_mode.MODE_NONE;

        /// <summary>
        /// ▼ 修正 [7C-fix1]：現在のモードに応じて表示するサブ分類。
        ///   以前は LINQ（IEnumerable）を返していたが、LINQ の結果は読み取り専用のため
        ///   DataGrid でセルを編集すると「EditItem は、このビューに対して許可されていません」
        ///   というエラーになった。編集可能にするため ObservableCollection を保持し、
        ///   区分の選択やモードが変わるたびに refresh_visible_subgroups() で中身を詰め直す。
        ///   common＝案件共通セット（category_id が null）／custom＝この区分専用セット。
        /// </summary>
        public ObservableCollection<edit_subgroup> visible_subgroups { get; } = new();

        /// <summary>
        /// 表示用のサブ分類コレクションを現在の選択・モードに合わせて作り直す。
        ///  ※ 実体（subgroups）に入っているオブジェクトをそのまま参照として詰めるため、
        ///    ここで編集した内容は subgroups 側にも反映される（保存時にDBへ書き込まれる）。
        /// </summary>
        private void refresh_visible_subgroups()
        {
            visible_subgroups.Clear();
            if (_selected_category == null) return;
            if (current_mode == workload_subgroup_mode.MODE_NONE) return;

            IEnumerable<edit_subgroup> src;
            if (current_mode == workload_subgroup_mode.MODE_CUSTOM)
            {
                // 区分専用セット。未保存の区分（id=0）は専用セットを持てない
                src = _selected_category.id == 0
                    ? Enumerable.Empty<edit_subgroup>()
                    : subgroups.Where(s => s.category_id == _selected_category.id);
            }
            else
            {
                // 案件共通セット
                src = subgroups.Where(s => s.category_id == null);
            }
            foreach (var s in src) visible_subgroups.Add(s);
        }

        // ---- コマンド ----
        public ICommand add_category_command { get; }
        public ICommand delete_category_command { get; }
        public ICommand move_up_command { get; }
        public ICommand move_down_command { get; }
        public ICommand add_keyword_command { get; }
        public ICommand delete_keyword_command { get; }
        public ICommand add_subgroup_command { get; }
        public ICommand delete_subgroup_command { get; }
        public ICommand copy_from_project_command { get; }

        public workload_classify_view_model(int project_id, string project_title)
        {
            _project_id = project_id;
            this.project_title = project_title;

            add_category_command = new RelayCommand(add_category);
            delete_category_command = new RelayCommand(delete_category);
            move_up_command = new RelayCommand(() => move_category(-1));
            move_down_command = new RelayCommand(() => move_category(+1));
            add_keyword_command = new RelayCommand(add_keyword);
            delete_keyword_command = new RelayCommand(delete_keyword);
            add_subgroup_command = new RelayCommand(add_subgroup);
            delete_subgroup_command = new RelayCommand(delete_subgroup);
            copy_from_project_command = new RelayCommand(async () => await copy_from_project_async());
        }

        // ============================================================
        // 読み込み
        // ============================================================
        public async Task load_async()
        {
            try
            {
                using var conn = database_manager.create_connection();

                // ---- プレビュー用に、対象案件の業務行（正規化済み）を用意する ----
                await load_tasks_async(conn);

                // ---- 区分 ----
                var cats = (await conn.QueryAsync<workload_category>(@"
                    SELECT * FROM workload_categories
                    WHERE project_id = @p AND is_active = 1
                    ORDER BY sort_order, id", new { p = _project_id })).ToList();

                var kws = (await conn.QueryAsync<workload_category_keyword>(@"
                    SELECT k.* FROM workload_category_keywords k
                    JOIN workload_categories c ON c.id = k.category_id
                    WHERE c.project_id = @p AND c.is_active = 1
                    ORDER BY k.priority, k.id", new { p = _project_id })).ToList();

                categories.Clear();
                foreach (var c in cats)
                {
                    var ec = new edit_category { id = c.id, name = c.name };

                    // 区分名と同じキーワードが登録されていれば、それは「区分名キーワード」とみなす。
                    // 見つからない場合は、区分名をキーワードとして使わない設定だったと判断する。
                    // （旧データとの互換：標準区分セットで作られた区分は区分名キーワードを持たない）
                    var own = kws.FirstOrDefault(
                        k => k.category_id == c.id &&
                             string.Equals(k.keyword, c.name, StringComparison.OrdinalIgnoreCase));
                    ec.use_name_as_keyword = own != null;

                    foreach (var k in kws.Where(k => k.category_id == c.id))
                    {
                        // 区分名キーワードは「追加キーワード」一覧には出さない（重複表示を防ぐ）
                        if (own != null && k.id == own.id) continue;
                        ec.keywords.Add(new edit_keyword { id = k.id, keyword = k.keyword });
                    }

                    ec.name_changed = () => recalc_hits(ec);
                    categories.Add(ec);
                }

                // ---- サブ分類 ----
                var subs = (await conn.QueryAsync<workload_subgroup>(@"
                    SELECT * FROM workload_subgroups
                    WHERE project_id = @p AND is_active = 1
                    ORDER BY sort_order, id", new { p = _project_id })).ToList();

                var sub_kws = (await conn.QueryAsync<workload_subgroup_keyword>(@"
                    SELECT k.* FROM workload_subgroup_keywords k
                    JOIN workload_subgroups s ON s.id = k.subgroup_id
                    WHERE s.project_id = @p AND s.is_active = 1
                    ORDER BY k.priority, k.id", new { p = _project_id })).ToList();

                subgroups.Clear();
                foreach (var s in subs)
                {
                    var words = sub_kws.Where(k => k.subgroup_id == s.id)
                                       .Select(k => k.keyword).ToList();
                    var es = new edit_subgroup
                    {
                        id = s.id,
                        category_id = s.category_id,
                        name = s.name,
                        // サブ分類名と同じキーワード1件だけなら、名前で判定しているとみなして空欄にする
                        keywords_text = (words.Count == 1 &&
                                         string.Equals(words[0], s.name, StringComparison.OrdinalIgnoreCase))
                                        ? "" : string.Join(", ", words),
                    };
                    // ▼ 追加 [7C-fix4]：編集のたびに該当件数を再計算させる
                    es.changed = () => recalc_subgroup_hits(es);
                    subgroups.Add(es);
                }

                // ---- サブ分類モード ----
                var modes = (await conn.QueryAsync<workload_subgroup_mode>(
                    "SELECT * FROM workload_subgroup_modes WHERE project_id = @p",
                    new { p = _project_id })).ToList();
                _mode_by_category.Clear();
                foreach (var m in modes)
                    _mode_by_category[m.category_id] = m.mode;

                // ---- 全区分・全サブ分類の該当件数を計算 ----
                foreach (var c in categories) recalc_hits(c);
                foreach (var s in subgroups) recalc_subgroup_hits(s); // ▼ 追加 [7C-fix4]

                selected_category = categories.FirstOrDefault();
                update_status();
            }
            catch (Exception ex)
            {
                status_message = $"分類設定の読み込みに失敗いたしました：{ex.Message}";
            }
        }

        /// <summary>
        /// プレビュー用の業務行を読み込む。
        /// 工数表の集計と同じ粒度（日付×作業者×業務内容）で束ねることで、
        /// ここで数えた該当件数が実際の集計件数と必ず一致するようにしている。
        /// </summary>
        private async Task load_tasks_async(Microsoft.Data.Sqlite.SqliteConnection conn)
        {
            string code = await conn.ExecuteScalarAsync<string>(
                "SELECT category_code FROM projects WHERE id = @id", new { id = _project_id }) ?? "";
            if (string.IsNullOrEmpty(code)) { _normalized_tasks = new(); return; }

            var child_codes = await CategoryGroupService.get_child_codes_async(code);
            var all_codes = new List<string> { code };
            all_codes.AddRange(child_codes);

            var rows = (await conn.QueryAsync<dynamic>(@"
                SELECT report_date, employee_name, detail
                FROM daily_reports
                WHERE category_code IN @codes
                  AND category_code NOT IN ('有給','有休')",
                new { codes = all_codes })).ToList();

            _normalized_tasks = rows
                .Select(r => (
                    date: (string)r.report_date,
                    emp: (string)(r.employee_name ?? ""),
                    task: ((string?)r.detail ?? "").Replace("、", "・").Trim()))
                .GroupBy(x => (x.date, x.emp, x.task))   // 集計と同一の粒度
                .Select(g => WorkloadAggregationService.normalize_public(g.Key.task))
                .ToList();
        }

        // ============================================================
        // 該当件数のプレビュー計算
        // ============================================================

        /// <summary>
        /// 区分の該当件数を再計算する。
        /// 判定ルールは集計本体（WorkloadAggregationService）と同じ「正規化＋部分一致」。
        /// ただしここでは他区分との優先順位は考慮せず、「この区分のキーワードに何件当たるか」を数える
        /// （キーワードの効き具合を確かめるための目安として使う）。
        /// </summary>
        private void recalc_hits(edit_category cat)
        {
            var words = resolve_keywords(cat)
                .Select(WorkloadAggregationService.normalize_public)
                .Where(w => w.Length > 0)
                .ToList();

            cat.hit_count = words.Count == 0
                ? 0
                : _normalized_tasks.Count(t => words.Any(w => t.Contains(w, StringComparison.Ordinal)));

            foreach (var k in cat.keywords)
            {
                string w = WorkloadAggregationService.normalize_public(k.keyword);
                k.hit_count = w.Length == 0
                    ? 0
                    : _normalized_tasks.Count(t => t.Contains(w, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// ▼ 追加 [7C-fix4]：サブ分類の該当件数を再計算する。
        /// キーワード欄が空欄ならサブ分類名で判定する（resolve_keywords がその処理を行う）。
        /// 判定は集計本体と同じ正規化＋部分一致。
        /// ※ ここでは区分による絞り込みは行わず、案件全体の業務行に対して数えている。
        ///    実際の集計では「区分に振り分けられた行の中で」さらにサブ分類判定が走るため、
        ///    実際の件数はこの数以下になる（キーワードが効いているかの目安として使う）。
        /// </summary>
        private void recalc_subgroup_hits(edit_subgroup sub)
        {
            var words = sub.resolve_keywords()
                .Select(WorkloadAggregationService.normalize_public)
                .Where(w => w.Length > 0)
                .ToList();

            sub.hit_count = words.Count == 0
                ? 0
                : _normalized_tasks.Count(t => words.Any(w => t.Contains(w, StringComparison.Ordinal)));
        }

        /// <summary>この区分が判定に使う全キーワード（区分名＋追加キーワード）</summary>
        private static List<string> resolve_keywords(edit_category cat)
        {
            var list = new List<string>();
            if (cat.use_name_as_keyword && !string.IsNullOrWhiteSpace(cat.name))
                list.Add(cat.name.Trim());
            list.AddRange(cat.keywords.Select(k => k.keyword).Where(k => !string.IsNullOrWhiteSpace(k)));
            return list;
        }

        /// <summary>入力中のキーワードが何件に該当するかを表示する</summary>
        private void update_new_keyword_hint()
        {
            string w = WorkloadAggregationService.normalize_public(_new_keyword);
            if (w.Length == 0) { new_keyword_hint = ""; return; }
            int n = _normalized_tasks.Count(t => t.Contains(w, StringComparison.Ordinal));
            new_keyword_hint = $"該当 {n} 件";
        }

        private void update_status()
        {
            int total = _normalized_tasks.Count;
            status_message = $"対象の業務行：{total} 件　／　区分：{categories.Count} 件"
                           + "　※該当件数は現在のキーワードで何件に当たるかの目安です（保存すると集計に反映されます）。";
        }

        // ============================================================
        // 区分の操作
        // ============================================================
        private void add_category()
        {
            var ec = new edit_category { id = 0, name = "新しい区分" };
            ec.name_changed = () => recalc_hits(ec);
            categories.Add(ec);
            selected_category = ec;
            recalc_hits(ec);
            update_status();
        }

        private void delete_category()
        {
            if (_selected_category == null) return;
            var confirm = MessageBox.Show(
                $"区分「{_selected_category.name}」を削除いたします。\n" +
                "この区分に振り分けられていた業務は「未分類」になります。\n\nよろしいでしょうか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            // DBに存在する区分は保存時にDELETEする（キーワード・専用サブ分類も一緒に消す）
            if (_selected_category.id != 0)
                _deleted_category_ids.Add(_selected_category.id);

            // この区分専用のサブ分類も削除対象に含める
            foreach (var s in subgroups.Where(s => s.category_id == _selected_category.id).ToList())
            {
                if (s.id != 0) _deleted_subgroup_ids.Add(s.id);
                subgroups.Remove(s);
            }

            categories.Remove(_selected_category);
            selected_category = categories.FirstOrDefault();
            update_status();
        }

        /// <summary>区分の並び順を入れ替える（並び順＝キーワード判定の優先順）</summary>
        private void move_category(int delta)
        {
            if (_selected_category == null) return;
            int i = categories.IndexOf(_selected_category);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= categories.Count) return;
            categories.Move(i, j);
            selected_category = categories[j];   // 選択を維持
        }

        // ============================================================
        // キーワードの操作
        // ============================================================
        private void add_keyword()
        {
            if (_selected_category == null) return;
            string w = (_new_keyword ?? "").Trim();
            if (w.Length == 0) return;

            // 同じ語の二重登録を防ぐ（区分名と同じ語も不要）
            if (string.Equals(w, _selected_category.name, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("区分名と同じ語は、区分名キーワードとして既に使用されております。",
                    "確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_selected_category.keywords.Any(
                    k => string.Equals(k.keyword, w, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("同じキーワードが既に登録されております。",
                    "確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _selected_category.keywords.Add(new edit_keyword { id = 0, keyword = w });
            new_keyword = "";
            recalc_hits(_selected_category);
        }

        private void delete_keyword()
        {
            if (_selected_category == null || _selected_keyword == null) return;
            if (_selected_keyword.id != 0) _deleted_keyword_ids.Add(_selected_keyword.id);
            _selected_category.keywords.Remove(_selected_keyword);
            selected_keyword = null;
            recalc_hits(_selected_category);
        }

        // ============================================================
        // サブ分類の操作
        // ============================================================
        private void add_subgroup()
        {
            if (_selected_category == null) return;
            // custom モードのときはこの区分専用、common のときは案件共通として追加する
            int? cat_id = current_mode == workload_subgroup_mode.MODE_CUSTOM
                ? _selected_category.id : (int?)null;

            if (current_mode == workload_subgroup_mode.MODE_CUSTOM && _selected_category.id == 0)
            {
                MessageBox.Show(
                    "この区分はまだ保存されていないため、区分専用のサブ分類を追加できません。\n" +
                    "先に「保存」を実行してから、専用サブ分類を追加してください。",
                    "確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // ▼ 修正 [7C-fix4]：新規サブ分類にも該当件数の再計算を配線する
            var ns = new edit_subgroup { id = 0, category_id = cat_id, name = "新しいサブ分類" };
            ns.changed = () => recalc_subgroup_hits(ns);
            subgroups.Add(ns);
            recalc_subgroup_hits(ns);
            refresh_visible_subgroups(); // ▼ 修正 [7C-fix1]
            selected_subgroup = ns;      // 追加した行を選択状態にする
        }

        private void delete_subgroup()
        {
            if (_selected_subgroup == null) return;
            if (_selected_subgroup.id != 0) _deleted_subgroup_ids.Add(_selected_subgroup.id);
            subgroups.Remove(_selected_subgroup);
            selected_subgroup = null;
            refresh_visible_subgroups(); // ▼ 修正 [7C-fix1]
        }

        // ============================================================
        // 他案件からコピー
        // ============================================================
        private async Task copy_from_project_async()
        {
            var dlg = new Views.WorkloadCopyDialog(_project_id)
            {
                Owner = Application.Current?.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.IsActive),
            };
            if (dlg.ShowDialog() != true || dlg.selected_categories.Count == 0) return;

            // 選ばれた区分を、この案件の編集中リストに追加する（保存時にINSERTされる）
            foreach (var src in dlg.selected_categories)
            {
                var ec = new edit_category
                {
                    id = 0,                       // 新規として追加
                    name = src.name,
                    use_name_as_keyword = src.use_name_as_keyword,
                };
                foreach (var w in src.keywords)
                    ec.keywords.Add(new edit_keyword { id = 0, keyword = w });

                ec.name_changed = () => recalc_hits(ec);
                categories.Add(ec);
                recalc_hits(ec);
            }
            update_status();
            status_message = $"{dlg.selected_categories.Count} 件の区分を取り込みました。「保存」で確定されます。";
        }

        // ============================================================
        // 保存（ここで初めてDBへ反映する）
        // ============================================================
        public async Task<bool> save_async()
        {
            // 入力チェック：区分名が空、または重複していないか
            foreach (var c in categories)
            {
                if (string.IsNullOrWhiteSpace(c.name))
                {
                    MessageBox.Show("区分名が空欄の区分がございます。名称をご入力ください。",
                        "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            var dup = categories.GroupBy(c => c.name.Trim(), StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault(g => g.Count() > 1);
            if (dup != null)
            {
                MessageBox.Show($"区分名「{dup.Key}」が重複しております。名称を変更してください。",
                    "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                using var conn = database_manager.create_connection();
                using var tx = conn.BeginTransaction();

                // ---- 削除 ----
                // 区分を削除するときは、ぶら下がるキーワード・専用サブ分類・モードも消す
                foreach (int cid in _deleted_category_ids)
                {
                    await conn.ExecuteAsync(
                        "DELETE FROM workload_category_keywords WHERE category_id = @id",
                        new { id = cid }, tx);
                    await conn.ExecuteAsync(@"
                        DELETE FROM workload_subgroup_keywords
                         WHERE subgroup_id IN (SELECT id FROM workload_subgroups WHERE category_id = @id)",
                        new { id = cid }, tx);
                    await conn.ExecuteAsync(
                        "DELETE FROM workload_subgroups WHERE category_id = @id",
                        new { id = cid }, tx);
                    await conn.ExecuteAsync(
                        "DELETE FROM workload_subgroup_modes WHERE category_id = @id",
                        new { id = cid }, tx);
                    await conn.ExecuteAsync(
                        "DELETE FROM workload_categories WHERE id = @id",
                        new { id = cid }, tx);
                }
                foreach (int kid in _deleted_keyword_ids)
                    await conn.ExecuteAsync(
                        "DELETE FROM workload_category_keywords WHERE id = @id",
                        new { id = kid }, tx);
                foreach (int sid in _deleted_subgroup_ids)
                {
                    await conn.ExecuteAsync(
                        "DELETE FROM workload_subgroup_keywords WHERE subgroup_id = @id",
                        new { id = sid }, tx);
                    await conn.ExecuteAsync(
                        "DELETE FROM workload_subgroups WHERE id = @id",
                        new { id = sid }, tx);
                }

                // ---- 区分（追加・更新） ----
                // 画面上の並び順をそのまま sort_order にする（＝判定の優先順）
                int sort = 10;
                foreach (var c in categories)
                {
                    if (c.id == 0)
                    {
                        c.id = (int)await conn.ExecuteScalarAsync<long>(@"
                            INSERT INTO workload_categories (project_id, name, sort_order)
                            VALUES (@p, @n, @s);
                            SELECT last_insert_rowid();",
                            new { p = _project_id, n = c.name.Trim(), s = sort }, tx);
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE workload_categories
                               SET name = @n, sort_order = @s,
                                   updated_at = datetime('now','localtime')
                             WHERE id = @id",
                            new { n = c.name.Trim(), s = sort, id = c.id }, tx);
                    }
                    sort += 10;

                    // ---- キーワードは毎回入れ直す（差分管理をせず、確実に画面と一致させる） ----
                    await conn.ExecuteAsync(
                        "DELETE FROM workload_category_keywords WHERE category_id = @id",
                        new { id = c.id }, tx);

                    int pri = 10;
                    // 区分名キーワード（ONのときのみ）を先頭に登録する
                    if (c.use_name_as_keyword)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO workload_category_keywords (category_id, keyword, priority)
                            VALUES (@c, @k, @pr)",
                            new { c = c.id, k = c.name.Trim(), pr = pri }, tx);
                        pri += 10;
                    }
                    foreach (var k in c.keywords)
                    {
                        if (string.IsNullOrWhiteSpace(k.keyword)) continue;
                        await conn.ExecuteAsync(@"
                            INSERT INTO workload_category_keywords (category_id, keyword, priority)
                            VALUES (@c, @k, @pr)",
                            new { c = c.id, k = k.keyword.Trim(), pr = pri }, tx);
                        pri += 10;
                    }
                }

                // ---- サブ分類（追加・更新） ----
                int ssort = 10;
                foreach (var s in subgroups)
                {
                    if (string.IsNullOrWhiteSpace(s.name)) continue;

                    if (s.id == 0)
                    {
                        s.id = (int)await conn.ExecuteScalarAsync<long>(@"
                            INSERT INTO workload_subgroups (project_id, category_id, name, sort_order)
                            VALUES (@p, @c, @n, @s);
                            SELECT last_insert_rowid();",
                            new { p = _project_id, c = s.category_id, n = s.name.Trim(), s = ssort }, tx);
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE workload_subgroups
                               SET name = @n, sort_order = @s,
                                   updated_at = datetime('now','localtime')
                             WHERE id = @id",
                            new { n = s.name.Trim(), s = ssort, id = s.id }, tx);
                    }
                    ssort += 10;

                    // サブ分類のキーワードも入れ直す
                    await conn.ExecuteAsync(
                        "DELETE FROM workload_subgroup_keywords WHERE subgroup_id = @id",
                        new { id = s.id }, tx);

                    int pri = 10;
                    foreach (var w in s.resolve_keywords())
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO workload_subgroup_keywords (subgroup_id, keyword, priority)
                            VALUES (@s, @k, @pr)",
                            new { s = s.id, k = w, pr = pri }, tx);
                        pri += 10;
                    }
                }

                // ---- サブ分類モード ----
                foreach (var c in categories)
                {
                    int key = mode_key(c);
                    string mode = _mode_by_category.TryGetValue(key, out var m)
                        ? m : workload_subgroup_mode.MODE_COMMON;

                    // UNIQUE(project_id, category_id) があるため UPSERT で書き込む
                    await conn.ExecuteAsync(@"
                        INSERT INTO workload_subgroup_modes (project_id, category_id, mode)
                        VALUES (@p, @c, @m)
                        ON CONFLICT(project_id, category_id)
                        DO UPDATE SET mode = @m, updated_at = datetime('now','localtime')",
                        new { p = _project_id, c = c.id, m = mode }, tx);
                }

                tx.Commit();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"分類設定の保存に失敗いたしました。\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
    }
}
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace EA_CostManager.Views
{
    /// <summary>登録先として選べる区分1件</summary>
    public class keyword_target_category
    {
        public int id { get; set; }
        public string name { get; set; } = "";
    }

    /// <summary>
    /// ▼ 追加 [Sprint 7C-1]：明細行からキーワードを登録する小ダイアログ
    ///
    /// 【使い方】
    /// 未分類タブや各区分タブの明細行を右クリックして開く。
    /// 元の業務内容が表示されるので、その一部を切り出してキーワードにし、
    /// 登録先の区分（既存 or 新規）を選んで登録する。
    ///
    /// 【即時反映】
    /// このダイアログは分類設定ダイアログ（保存反映方式）とは異なり、
    /// 「登録」を押した時点でDBに書き込む。明細を見ながら次々にキーワードを
    /// 足していく使い方を想定しているため、その都度確定させたほうが分かりやすい。
    ///
    /// 【該当件数プレビュー】
    /// 入力中のキーワードが何件の業務行に当たるかを即時表示する。
    /// 判定は集計本体と同じ正規化（normalize_public）を使うため、
    /// ここで表示される件数と実際の集計件数は一致する。
    /// </summary>
    public partial class WorkloadKeywordDialog : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void notify([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        private readonly int _project_id;
        private List<string> _normalized_tasks = new();

        /// <summary>右クリックした明細行の業務内容（そのまま表示する）</summary>
        public string source_task { get; }

        public ObservableCollection<keyword_target_category> categories { get; } = new();

        private keyword_target_category? _selected_category;
        public keyword_target_category? selected_category
        {
            get => _selected_category;
            set { _selected_category = value; notify(); }
        }

        private string _keyword = "";
        public string keyword
        {
            get => _keyword;
            set
            {
                if (_keyword == value) return;
                _keyword = value;
                notify();
                update_hint();
            }
        }

        private string _hit_hint = "";
        public string hit_hint
        {
            get => _hit_hint;
            set { _hit_hint = value; notify(); }
        }

        private bool _create_new_category;
        /// <summary>新しい区分を作ってそこに登録するか</summary>
        public bool create_new_category
        {
            get => _create_new_category;
            set { _create_new_category = value; notify(); }
        }

        private string _new_category_name = "";
        public string new_category_name
        {
            get => _new_category_name;
            set { _new_category_name = value; notify(); }
        }

        private string _status_message = "";
        public string status_message
        {
            get => _status_message;
            set { _status_message = value; notify(); }
        }

        public WorkloadKeywordDialog(int project_id, string source_task)
        {
            InitializeComponent();
            _project_id = project_id;
            this.source_task = source_task ?? "";
            DataContext = this;

            Loaded += async (_, _) =>
            {
                await load_async();
                keyword_box.Focus();
            };
        }

        private async System.Threading.Tasks.Task load_async()
        {
            try
            {
                using var conn = database_manager.create_connection();

                // 登録先の候補となる区分
                var cats = (await conn.QueryAsync<keyword_target_category>(@"
                    SELECT id, name FROM workload_categories
                    WHERE project_id = @p AND is_active = 1
                    ORDER BY sort_order, id", new { p = _project_id })).ToList();

                categories.Clear();
                foreach (var c in cats) categories.Add(c);
                selected_category = categories.FirstOrDefault();

                // 区分が1つも無い場合は、新規作成しか選べない
                if (categories.Count == 0)
                {
                    create_new_category = true;
                    status_message = "この案件にはまだ業務区分がございません。新しい区分を作成してください。";
                }

                // 該当件数プレビュー用の業務行を用意する（集計と同一粒度）
                string code = await conn.ExecuteScalarAsync<string>(
                    "SELECT category_code FROM projects WHERE id = @id",
                    new { id = _project_id }) ?? "";
                if (!string.IsNullOrEmpty(code))
                {
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
                        .GroupBy(x => (x.date, x.emp, x.task))
                        .Select(g => WorkloadAggregationService.normalize_public(g.Key.task))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                status_message = $"読み込みに失敗いたしました：{ex.Message}";
            }
        }

        /// <summary>入力中のキーワードが何件の業務行に該当するかを表示する</summary>
        private void update_hint()
        {
            string w = WorkloadAggregationService.normalize_public(_keyword);
            if (w.Length == 0) { hit_hint = ""; return; }
            int n = _normalized_tasks.Count(t => t.Contains(w, StringComparison.Ordinal));
            hit_hint = $"該当 {n} 件";
        }

        private async void ok_click(object sender, RoutedEventArgs e)
        {
            if (ReadOnlyGuard.block_if_read_only(this)) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            string w = (keyword ?? "").Trim();
            if (w.Length == 0)
            {
                MessageBox.Show("キーワードをご入力ください。", "確認",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                using var conn = database_manager.create_connection();
                using var tx = conn.BeginTransaction();

                int target_id;

                if (create_new_category)
                {
                    string cname = (new_category_name ?? "").Trim();
                    if (cname.Length == 0)
                    {
                        MessageBox.Show("新しい区分の名前をご入力ください。", "確認",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // 末尾に追加する（＝判定は最後。既存区分の判定を邪魔しない）
                    int max_sort = await conn.ExecuteScalarAsync<int>(
                        "SELECT COALESCE(MAX(sort_order), 0) FROM workload_categories WHERE project_id = @p",
                        new { p = _project_id }, tx);

                    target_id = (int)await conn.ExecuteScalarAsync<long>(@"
                        INSERT INTO workload_categories (project_id, name, sort_order)
                        VALUES (@p, @n, @s);
                        SELECT last_insert_rowid();",
                        new { p = _project_id, n = cname, s = max_sort + 10 }, tx);

                    // 区分名をそのまま第1キーワードとして登録する
                    await conn.ExecuteAsync(@"
                        INSERT INTO workload_category_keywords (category_id, keyword, priority)
                        VALUES (@c, @k, 10)",
                        new { c = target_id, k = cname }, tx);
                }
                else
                {
                    if (selected_category == null)
                    {
                        MessageBox.Show("登録先の区分をお選びください。", "確認",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    target_id = selected_category.id;
                }

                // 同じキーワードが既に登録されていないか確認する（二重登録の防止）
                int exists = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM workload_category_keywords
                     WHERE category_id = @c AND keyword = @k COLLATE NOCASE",
                    new { c = target_id, k = w }, tx);

                if (exists == 0)
                {
                    int max_pri = await conn.ExecuteScalarAsync<int>(
                        "SELECT COALESCE(MAX(priority), 0) FROM workload_category_keywords WHERE category_id = @c",
                        new { c = target_id }, tx);

                    await conn.ExecuteAsync(@"
                        INSERT INTO workload_category_keywords (category_id, keyword, priority)
                        VALUES (@c, @k, @pr)",
                        new { c = target_id, k = w, pr = max_pri + 10 }, tx);
                }

                tx.Commit();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"キーワードの登録に失敗いたしました。\n{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

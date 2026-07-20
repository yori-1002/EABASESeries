using System.Windows;
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Models;

namespace EA_CostManager.Views
{
    public partial class FilterTabDialog : Window
    {
        private readonly int _project_id;

        public cost_filter_tab? created_tab { get; private set; }

        public FilterTabDialog(
            int project_id,
            System.Collections.Generic.List<string> available_months,
            string default_agg_mode = "daily")   // ▼追加：呼び出し元タブの集計モードを引き継ぐ初期値
        {
            InitializeComponent();
            _project_id = project_id;

            // 月度コンボボックスに選択肢を追加
            cmb_month.Items.Add("（全月度）");
            foreach (var m in available_months)
                cmb_month.Items.Add(m);
            cmb_month.SelectedIndex = 0;

            // ▼追加：業務単位タブから絞り込む場合は「業務単位」を初期チェック（ユーザーは外せる）
            chk_task_mode.IsChecked = (default_agg_mode == "task");
        }

        // ▼▼▼ 追加：期間指定チェックボックスのON/OFF切替 ▼▼▼
        // チェックONでカレンダーを表示、OFFで非表示にする
        private void chk_use_date_range_Changed(object sender, RoutedEventArgs e)
        {
            if (calendar_picker == null) return;

            if (chk_use_date_range.IsChecked == true)
            {
                // ▼▼▼ 追加：期間指定ONにしたら月度をリセット（両方同時選択を防ぐ） ▼▼▼
                cmb_month.SelectedIndex = 0; // 「（全月度）」に戻す
                calendar_picker.Visibility = Visibility.Visible;
                // ▼▼▼ 修正：ウィンドウ高さは変えずにScrollViewerでスクロールする ▼▼▼
            }
            else
            {
                calendar_picker.Visibility = Visibility.Collapsed;
            }
            schedule_count_update();
        }

        // ---- 件数自動更新（debounce付き） ----
        // 入力が止まって400ms後に実行する（連打・高速入力でのDB過負荷防止）
        private System.Threading.CancellationTokenSource? _count_cts;

        // TextBox の TextChanged イベント用
        private void filter_condition_changed_text(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => schedule_count_update();

        // ComboBox の SelectionChanged / KeyUp イベント用
        private void filter_condition_changed(object sender, System.Windows.RoutedEventArgs e)
            => schedule_count_update();

        private void filter_condition_changed_key(object sender, System.Windows.Input.KeyEventArgs e)
            => schedule_count_update();

        private void schedule_count_update()
        {
            // 既存のタイマーをキャンセル
            _count_cts?.Cancel();
            _count_cts = new System.Threading.CancellationTokenSource();
            var token = _count_cts.Token;

            // 400ms後に実行
            _ = System.Threading.Tasks.Task.Delay(400, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Dispatcher.Invoke(() => _ = run_count_async());
            });
        }

        // ▼▼▼ 追加：件数確認ボタン（ボタンクリック用・後方互換のため残す） ▼▼▼
        private async void btn_check_count_Click(object sender, RoutedEventArgs e)
            => await run_count_async();

        // ---- 実際のカウント処理 ----
        private async System.Threading.Tasks.Task run_count_async()
        {
            if (txt_count_result == null) return;
            txt_count_result.Text = "確認中...";
            txt_count_result.Foreground = System.Windows.Media.Brushes.Gray;

            try
            {
                string month = cmb_month.SelectedIndex <= 0
                    ? "" : cmb_month.SelectedItem?.ToString() ?? "";
                string match = (cmb_match.SelectedIndex == 1) ? "exact" : "partial";
                string content = txt_content.Text.Trim();
                string names_str = txt_names.Text.Trim();
                string date_from = "";
                string date_to = "";
                if (chk_use_date_range.IsChecked == true
                    && calendar_picker.SelectedDateFrom != null
                    && calendar_picker.SelectedDateTo != null)
                {
                    date_from = calendar_picker.SelectedDateFrom.Value.ToString("yyyy-MM-dd");
                    date_to = calendar_picker.SelectedDateTo.Value.ToString("yyyy-MM-dd");
                }

                using var conn = database_manager.create_connection();
                var rows = (await conn.QueryAsync<dynamic>(@"
                    SELECT cr.record_date, cr.fiscal_month, cr.work_content,
                           cr.engineer_names, cr.assistant_names
                    FROM cost_records cr
                    JOIN projects p ON cr.category_code = p.category_code
                    WHERE p.id = @pid
                    ORDER BY cr.record_date",
                    new { pid = _project_id })).ToList();

                if (!string.IsNullOrWhiteSpace(month))
                    rows = rows.Where(r => (string?)r.fiscal_month == month).ToList();

                if (!string.IsNullOrWhiteSpace(date_from) && !string.IsNullOrWhiteSpace(date_to))
                    rows = rows.Where(r =>
                        string.Compare((string?)r.record_date, date_from) >= 0 &&
                        string.Compare((string?)r.record_date, date_to) <= 0).ToList();

                if (!string.IsNullOrWhiteSpace(content))
                {
                    // ▼▼▼ 修正：カンマ区切りでOR検索に対応 ▼▼▼
                    var keywords = content
                        .Split(',')
                        .Select(k => k.Trim())
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .ToList();

                    bool is_partial = match != "exact";
                    rows = rows.Where(r =>
                    {
                        string wc = (string?)r.work_content ?? "";
                        return keywords.Any(keyword =>
                            is_partial
                                ? wc.Contains(keyword)
                                : wc.Split('・').Any(p => p.Trim() == keyword));
                    }).ToList();
                }

                if (!string.IsNullOrWhiteSpace(names_str))
                {
                    var names = names_str.Split(',')
                        .Select(n => n.Trim())
                        .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    rows = rows.Where(r =>
                        names.Any(n =>
                            ((string?)r.engineer_names ?? "").Contains(n) ||
                            ((string?)r.assistant_names ?? "").Contains(n))).ToList();
                }

                int count = rows.Count;
                txt_count_result.Text = count == 0
                    ? "⚠ 絞り込み結果：0件（条件に合うデータが見つかりません）"
                    : $"✅ 絞り込み結果：{count} 件";
                txt_count_result.Foreground = count == 0
                    ? System.Windows.Media.Brushes.OrangeRed
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(39, 174, 96));
            }
            catch (System.Exception ex)
            {
                txt_count_result.Text = $"確認エラー：{ex.Message}";
                txt_count_result.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private async void btn_create_Click(object sender, RoutedEventArgs e)
        {
            if (ReadOnlyGuard.block_if_read_only(this)) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (string.IsNullOrWhiteSpace(txt_tab_name.Text))
            {
                txt_error.Text = "タブ名は必須です";
                txt_error.Visibility = Visibility.Visible;
                return;
            }

            // ▼▼▼ 追加：期間指定チェックON時は開始日・終了日の両方が必要 ▼▼▼
            if (chk_use_date_range.IsChecked == true)
            {
                if (calendar_picker.SelectedDateFrom == null || calendar_picker.SelectedDateTo == null)
                {
                    txt_error.Text = "期間指定をONにした場合は開始日と終了日の両方を選択してください";
                    txt_error.Visibility = Visibility.Visible;
                    return;
                }
            }

            txt_error.Visibility = Visibility.Collapsed;

            string month = cmb_month.SelectedIndex <= 0
                ? ""
                : cmb_month.SelectedItem?.ToString() ?? "";
            string match = (cmb_match.SelectedIndex == 1) ? "exact" : "partial";

            // ▼▼▼ 追加：日付範囲を取得（未設定の場合は空文字） ▼▼▼
            string date_from = "";
            string date_to = "";
            if (chk_use_date_range.IsChecked == true
                && calendar_picker.SelectedDateFrom != null
                && calendar_picker.SelectedDateTo != null)
            {
                date_from = calendar_picker.SelectedDateFrom.Value.ToString("yyyy-MM-dd");
                date_to = calendar_picker.SelectedDateTo.Value.ToString("yyyy-MM-dd");
            }

            var model = new cost_filter_tab
            {
                project_id = _project_id,
                tab_name = txt_tab_name.Text.Trim(),
                filter_month = month,
                filter_content = txt_content.Text.Trim(),
                filter_match = match,
                filter_names = txt_names.Text.Trim(),
                is_single_mode = (chk_single_mode.IsChecked == true) ? 1 : 0,
                // ▼▼▼ 追加(B)：1業務ごと(1人ずつ)モード ▼▼▼
                agg_mode = (chk_task_mode.IsChecked == true) ? "task" : "daily",
                // ▼▼▼ 追加：日付範囲フィルター ▼▼▼
                filter_date_from = date_from,
                filter_date_to = date_to,
                // use_custom_rates はCostPage.xaml.csで現在のタブから自動継承する
            };

            try
            {
                using var conn = database_manager.create_connection();

                // ▼▼▼ 追加：INSERT前にカラムの存在を保証（Sprint 3.6/3.7） ▼▼▼
                // database_manager の migrate_cost_records より前に呼ばれる場合に備えて
                // ここでもカラム追加を試みる（既存の場合は SQLiteException を無視）
                var ensure_cols = new[]
                {
                    "ALTER TABLE cost_filter_tabs ADD COLUMN is_archived INTEGER DEFAULT 0",
                    "ALTER TABLE cost_filter_tabs ADD COLUMN filter_date_from TEXT DEFAULT ''",
                    "ALTER TABLE cost_filter_tabs ADD COLUMN filter_date_to TEXT DEFAULT ''",
                    "ALTER TABLE cost_filter_tabs ADD COLUMN use_custom_rates INTEGER DEFAULT 0",
                    "ALTER TABLE cost_filter_tabs ADD COLUMN agg_mode TEXT DEFAULT 'daily'",   // ▼追加(B)
                };
                foreach (var sql in ensure_cols)
                {
                    try { await conn.ExecuteAsync(sql); }
                    catch { /* 既に存在する場合は無視 */ }
                }

                var new_id = await conn.QuerySingleAsync<int>(@"
                    INSERT INTO cost_filter_tabs
                        (project_id, tab_name, filter_month, filter_content, filter_match,
                         filter_names, is_single_mode, filter_date_from, filter_date_to, agg_mode)
                    VALUES
                        (@project_id, @tab_name, @filter_month, @filter_content, @filter_match,
                         @filter_names, @is_single_mode, @filter_date_from, @filter_date_to, @agg_mode);
                    SELECT last_insert_rowid();", model);

                model.id = new_id;
                created_tab = model;
                DialogResult = true;
                Close();
            }
            catch (System.Exception ex)
            {
                txt_error.Text = $"作成エラー：{ex.Message}";
                txt_error.Visibility = Visibility.Visible;
            }
        }

        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
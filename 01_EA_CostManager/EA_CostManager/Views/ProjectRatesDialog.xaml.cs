using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.Views
{
    // ---- 個人別単価の行モデル（DataGridにバインド） ----
    public class individual_rate_row
    {
        public int id { get; set; } = 0;
        public string employee_name { get; set; } = "";
        public decimal daily_rate { get; set; } = 0;
    }

    /// <summary>
    /// 現場別単価設定ダイアログ
    /// 技師・助手・個人別の上書き単価を設定する
    /// project_rates テーブルに保存。集計時に app_settings より優先参照される。
    /// </summary>
    public partial class ProjectRatesDialog : Window
    {
        /// <summary>保存後にユーザーが選択した操作</summary>
        public RatesDialogAction selected_action { get; private set; } = RatesDialogAction.None;
        private readonly int _project_id;
        private readonly string _project_name;
        // ▼▼▼ 追加：既存設定を読み込むかのフラグ ▼▼▼
        // false の場合（全件タブ等から開いた場合）はデフォルト状態（空欄）で表示
        private readonly bool _load_existing;
        private decimal _default_engineer_rate;
        private decimal _default_assistant_rate;

        // 個人別単価のコレクション（DataGridにバインド）
        private ObservableCollection<individual_rate_row> _individual_rows = new();

        public ProjectRatesDialog(int project_id, string project_name, bool load_existing = true)
        {
            InitializeComponent();
            _project_id = project_id;
            _project_name = project_name;
            _load_existing = load_existing;

            txt_project_name.Text = project_name;
            grid_individual.ItemsSource = _individual_rows;

            _ = load_async();
        }

        // ---- 設定値の読み込み ----
        private async Task load_async()
        {
            try
            {
                using var conn = database_manager.create_connection();

                // デフォルト単価を app_settings から取得
                var settings = await conn.QueryAsync<(string key, string value)>(
                    "SELECT key, value FROM app_settings WHERE key IN ('engineer_daily_rate','assistant_daily_rate')");
                foreach (var (key, value) in settings)
                {
                    if (key == "engineer_daily_rate" && decimal.TryParse(value, out var er))
                        _default_engineer_rate = er;
                    if (key == "assistant_daily_rate" && decimal.TryParse(value, out var ar))
                        _default_assistant_rate = ar;
                }

                // デフォルト値の表示（デフォルトが0の場合はapp_settingsのフォールバック値を使用）
                if (_default_engineer_rate == 0) _default_engineer_rate = 34800m;
                if (_default_assistant_rate == 0) _default_assistant_rate = 28000m;
                txt_engineer_default.Text = $"（デフォルト：{_default_engineer_rate:#,##0}円）";
                txt_assistant_default.Text = $"（デフォルト：{_default_assistant_rate:#,##0}円）";

                // ▼▼▼ 修正：load_existing=falseの場合（全件タブ等）は既存設定を読み込まない ▼▼▼
                // 全件タブから開いた場合はデフォルト状態（空欄）で表示する
                if (!_load_existing)
                {
                    // 社員一覧だけ表示（単価は空欄）
                    var name_rows_only = await conn.QueryAsync<string>(@"
                        SELECT engineer_names || '・' || assistant_names
                        FROM cost_records
                        WHERE category_code = (
                            SELECT category_code FROM projects WHERE id = @pid
                        )
                        AND (engineer_names != '' OR assistant_names != '')",
                        new { pid = _project_id });

                    var all_names_only = new System.Collections.Generic.HashSet<string>();
                    foreach (var name_str in name_rows_only)
                        foreach (var n in name_str.Split('・'))
                        {
                            var trimmed = n.Trim();
                            if (!string.IsNullOrWhiteSpace(trimmed))
                                all_names_only.Add(trimmed);
                        }

                    foreach (var name in System.Linq.Enumerable.OrderBy(all_names_only, n => n))
                        _individual_rows.Add(new individual_rate_row { employee_name = name, daily_rate = 0 });

                    return;
                }

                // 既存の project_rates を読み込み
                var existing = await conn.QueryAsync<dynamic>(
                    "SELECT id, rate_type, employee_name, daily_rate FROM project_rates WHERE project_id = @pid",
                    new { pid = _project_id });

                // 既存設定をDictionary化（個人名→設定済み単価）
                var saved_individual = new System.Collections.Generic.Dictionary<string, (int id, decimal rate)>();

                foreach (var row in existing)
                {
                    string rate_type = (string)row.rate_type;
                    decimal daily_rate = (decimal)row.daily_rate;

                    if (rate_type == "技師")
                        txt_engineer_rate.Text = daily_rate.ToString();
                    else if (rate_type == "助手")
                        txt_assistant_rate.Text = daily_rate.ToString();
                    else if (rate_type == "個人")
                        saved_individual[(string)(row.employee_name ?? "")] = ((int)row.id, daily_rate);
                }

                // ▼▼▼ 追加：この現場に登場した社員を cost_records から取得して一覧に表示 ▼▼▼
                // engineer_names・assistant_names を「・」で分割して重複除去
                var name_rows = await conn.QueryAsync<string>(@"
                    SELECT engineer_names || '・' || assistant_names
                    FROM cost_records
                    WHERE category_code = (
                        SELECT category_code FROM projects WHERE id = @pid
                    )
                    AND (engineer_names != '' OR assistant_names != '')",
                    new { pid = _project_id });

                var all_names = new System.Collections.Generic.HashSet<string>();
                foreach (var name_str in name_rows)
                {
                    foreach (var n in name_str.Split('・'))
                    {
                        var trimmed = n.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                            all_names.Add(trimmed);
                    }
                }

                // 社員一覧を個人別単価グリッドに追加
                // 既存設定がある場合はその単価を表示、なければ0（空欄扱い）
                foreach (var name in System.Linq.Enumerable.OrderBy(all_names, n => n))
                {
                    saved_individual.TryGetValue(name, out var saved);
                    _individual_rows.Add(new individual_rate_row
                    {
                        id = saved.id,
                        employee_name = name,
                        daily_rate = saved.rate,  // 未設定は0（保存時にスキップ）
                    });
                }

                // 既存設定があるが cost_records に登場しない名前も追加（削除されてない場合など）
                foreach (var kv in saved_individual)
                {
                    if (!all_names.Contains(kv.Key))
                    {
                        _individual_rows.Add(new individual_rate_row
                        {
                            id = kv.Value.id,
                            employee_name = kv.Key,
                            daily_rate = kv.Value.rate,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                txt_error.Text = $"読み込みエラー：{ex.Message}";
                txt_error.Visibility = Visibility.Visible;
            }
        }

        // ---- 保存 ----
        private async void btn_save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var conn = database_manager.create_connection();
                using var tx = conn.BeginTransaction();
                try
                {
                    // 既存のこの現場の project_rates を全削除してから再INSERT（シンプルな洗い替え）
                    await conn.ExecuteAsync(
                        "DELETE FROM project_rates WHERE project_id = @pid",
                        new { pid = _project_id }, tx);

                    // 技師単価（空欄ならスキップ→デフォルト使用）
                    if (!string.IsNullOrWhiteSpace(txt_engineer_rate.Text)
                        && decimal.TryParse(txt_engineer_rate.Text, out decimal eng_rate))
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO project_rates (project_id, rate_type, daily_rate, updated_at)
                            VALUES (@pid, '技師', @rate, datetime('now','localtime'))",
                            new { pid = _project_id, rate = eng_rate }, tx);
                    }

                    // 助手単価（空欄ならスキップ→デフォルト使用）
                    if (!string.IsNullOrWhiteSpace(txt_assistant_rate.Text)
                        && decimal.TryParse(txt_assistant_rate.Text, out decimal ast_rate))
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO project_rates (project_id, rate_type, daily_rate, updated_at)
                            VALUES (@pid, '助手', @rate, datetime('now','localtime'))",
                            new { pid = _project_id, rate = ast_rate }, tx);
                    }

                    // 個人別単価（氏名・金額が両方入力済みの行のみ保存）
                    foreach (var row in _individual_rows)
                    {
                        if (string.IsNullOrWhiteSpace(row.employee_name) || row.daily_rate <= 0)
                            continue;

                        await conn.ExecuteAsync(@"
                            INSERT INTO project_rates (project_id, rate_type, employee_name, daily_rate, updated_at)
                            VALUES (@pid, '個人', @name, @rate, datetime('now','localtime'))",
                            new { pid = _project_id, name = row.employee_name.Trim(), rate = row.daily_rate }, tx);
                    }

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }

                // ▼▼▼ 追加：保存後の4択ダイアログ ▼▼▼
                var action_dlg = new rates_action_dialog { Owner = this };
                action_dlg.ShowDialog();
                selected_action = action_dlg.selected_action;

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                txt_error.Text = $"保存エラー：{ex.Message}";
                txt_error.Visibility = Visibility.Visible;
            }
        }

        // ---- 全タブリセット ----
        private void btn_reset_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                $"「{_project_name}」の単価設定をすべて削除して\n全タブをデフォルト単価で再集計します。",
                "全タブリセット",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;

            selected_action = RatesDialogAction.ResetAll;
            DialogResult = true;
            Close();
        }

        // ---- このタブのみリセット ----
        private void btn_reset_current_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                $"現在のタブのみをデフォルト単価に戻します。\n（単価設定はそのまま保持されます）",
                "このタブのみリセット",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;

            selected_action = RatesDialogAction.ResetCurrentTab;
            DialogResult = true;
            Close();
        }

        // ---- キャンセル ----
        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
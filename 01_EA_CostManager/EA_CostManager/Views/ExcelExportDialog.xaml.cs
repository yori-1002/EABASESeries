using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EA_CostManager.Services;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Views
{
    /// <summary>
    /// Excel書き出しダイアログ
    /// 左：設定（対象タブ・期間・列選択・出力先）
    /// 右：DataGridによるプレビュー（設定変更で自動更新）
    /// </summary>
    public partial class ExcelExportDialog : Window
    {
        private readonly project_cost_view_model _project_vm;
        private readonly List<filter_tab_check_item> _check_items = new();

        // デフォルト出力先：デスクトップ
        private string _output_dir =
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        public ExcelExportDialog(project_cost_view_model project_vm)
        {
            // ▼▼▼ 重要：InitializeComponent前にセット ▼▼▼
            _project_vm = project_vm;
            InitializeComponent();

            foreach (var ft in project_vm.filter_tabs)
                _check_items.Add(new filter_tab_check_item(ft));
            list_filter_tabs.ItemsSource = _check_items;

            txt_output_dir.Text = _output_dir;

            // 初期プレビューを表示
            Loaded += (_, _) => update_preview();
        }

        // ---- オプション変更の統合ハンドラー ----
        private void option_Changed(object sender, RoutedEventArgs e)
        {
            if (_project_vm == null || rb_all_tab == null || rb_filter_select == null) return; // ▼▼▼ 修正：rb_filter_select null ガード追加（XAML初期化中の NullReferenceException 対策）▼▼▼

            border_tab_list.Visibility = rb_filter_select.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
            grid_period.Visibility = chk_period.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;

            update_preview();
        }

        // ---- プレビュー更新 ----
        private void update_preview()
        {
            if (preview_grid == null || _project_vm == null) return;

            var tabs = get_selected_tabs();
            var cols = get_selected_columns();

            // 件数テキスト更新
            if (txt_info != null)
            {
                int total = tabs.Sum(t => filter_records(t.raw_records).Count);
                txt_info.Text = tabs.Count == 0
                    ? "⚠ 書き出すタブを選択してください"
                    : $"書き出し対象：{tabs.Count}シート / {total}行";
            }

            if (tabs.Count == 0)
            {
                preview_grid.Columns.Clear();
                preview_grid.ItemsSource = null;
                return;
            }

            // 最初のタブのデータでプレビューを生成
            var first_tab = tabs[0];
            var records = filter_records(first_tab.raw_records);

            // DataTableを動的に構築
            var dt = build_preview_table(records, cols, first_tab.tab_name);
            preview_grid.Columns.Clear();
            foreach (DataColumn dc in dt.Columns)
            {
                preview_grid.Columns.Add(new DataGridTextColumn
                {
                    Header = dc.ColumnName,
                    Binding = new System.Windows.Data.Binding($"[{dc.ColumnName}]"),
                    Width = dc.ColumnName == "作業内容" ? 200 : DataGridLength.Auto,
                });
            }
            preview_grid.ItemsSource = dt.DefaultView;
        }

        // ---- DataTableを構築 ----
        private DataTable build_preview_table(
            List<Models.cost_record> records,
            ExcelColumnOptions cols,
            string tab_name)
        {
            var dt = new DataTable();

            // 固定列
            dt.Columns.Add("作業日");
            dt.Columns.Add("作業内容");

            // オプション列
            if (cols.eng_name) dt.Columns.Add("技師氏名");
            if (cols.eng_count) dt.Columns.Add("技師人数");
            if (cols.eng_day) dt.Columns.Add("技師人工(日)");
            if (cols.eng_hour) dt.Columns.Add("技師時間(h)");
            if (cols.eng_cost) dt.Columns.Add("技師人件費");
            if (cols.ast_name) dt.Columns.Add("助手氏名");
            if (cols.ast_count) dt.Columns.Add("助手人数");
            if (cols.ast_day) dt.Columns.Add("助手人工(日)");
            if (cols.ast_hour) dt.Columns.Add("助手時間(h)");
            if (cols.ast_cost) dt.Columns.Add("助手人件費");
            if (cols.total_man) dt.Columns.Add("合計人工(日)");
            if (cols.person) dt.Columns.Add("人件費合計");
            if (cols.dist) dt.Columns.Add("距離(km往復)");
            if (cols.vehicle) dt.Columns.Add("台数");
            if (cols.trans) dt.Columns.Add("交通費");
            if (cols.use_count) dt.Columns.Add("使用数");
            if (cols.equip) dt.Columns.Add("損料");
            if (cols.equip_name) dt.Columns.Add("機材・使用者");
            if (cols.total) dt.Columns.Add("合計金額");

            foreach (var r in records)
            {
                var row = dt.NewRow();
                row["作業日"] = fmt_date(r.record_date);
                row["作業内容"] = r.work_content ?? "";
                if (cols.eng_name) row["技師氏名"] = r.engineer_names ?? "";
                if (cols.eng_count) row["技師人数"] = r.engineer_count == 0 ? "" : r.engineer_count.ToString();
                if (cols.eng_day) row["技師人工(日)"] = fmt_num(r.engineer_days);
                if (cols.eng_hour) row["技師時間(h)"] = fmt_num(r.engineer_hours);
                if (cols.eng_cost) row["技師人件費"] = fmt_yen(r.engineer_cost);
                if (cols.ast_name) row["助手氏名"] = r.assistant_names ?? "";
                if (cols.ast_count) row["助手人数"] = r.assistant_count == 0 ? "" : r.assistant_count.ToString();
                if (cols.ast_day) row["助手人工(日)"] = fmt_num(r.assistant_days);
                if (cols.ast_hour) row["助手時間(h)"] = fmt_num(r.assistant_hours);
                if (cols.ast_cost) row["助手人件費"] = fmt_yen(r.assistant_cost);
                if (cols.total_man) row["合計人工(日)"] = fmt_num(r.engineer_days + r.assistant_days);
                if (cols.person) row["人件費合計"] = fmt_yen(r.personnel_cost);
                if (cols.dist) row["距離(km往復)"] = fmt_num(r.distance_total);
                if (cols.vehicle) row["台数"] = r.vehicle_count == 0 ? "" : r.vehicle_count.ToString();
                if (cols.trans) row["交通費"] = fmt_yen(r.transport_cost);
                if (cols.use_count) row["使用数"] = r.equipment_quantity == 0 ? "" : r.equipment_quantity.ToString();
                if (cols.equip) row["損料"] = fmt_yen(r.equipment_cost);
                if (cols.equip_name) row["機材・使用者"] = r.equipment_names ?? "";
                if (cols.total) row["合計金額"] = fmt_yen(r.total_cost);
                dt.Rows.Add(row);
            }
            return dt;
        }

        // ---- 出力フォルダ参照 ----
        private void btn_browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "出力フォルダを選択",
                InitialDirectory = _output_dir,
            };
            if (dlg.ShowDialog() == true)
            {
                _output_dir = dlg.FolderName;
                txt_output_dir.Text = _output_dir;
            }
        }

        // ---- Excel書き出し実行 ----
        private void btn_export_Click(object sender, RoutedEventArgs e)
        {
            if (chk_period.IsChecked == true
                && dp_from.SelectedDate.HasValue && dp_to.SelectedDate.HasValue
                && dp_from.SelectedDate > dp_to.SelectedDate)
            {
                MessageBox.Show("開始日は終了日より前に設定してください。",
                    "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var tabs = get_selected_tabs();
            if (tabs.Count == 0)
            {
                MessageBox.Show("書き出すタブを選択してください。",
                    "選択なし", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string file_path = ExcelExportService.export(
                    _project_vm, tabs, _output_dir,
                    get_selected_columns(),
                    date_from(), date_to());

                var result = MessageBox.Show(
                    $"書き出し完了しました。\n\n{file_path}\n\nファイルを開きますか？",
                    "完了", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(file_path)
                        { UseShellExecute = true });

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"書き出しエラー：{ex.Message}",
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btn_cancel_Click(object sender, RoutedEventArgs e) => Close();

        // ---- 現在の列選択を取得 ----
        private ExcelColumnOptions get_selected_columns() => new()
        {
            eng_name = chk_eng_name.IsChecked == true,
            eng_count = chk_eng_count.IsChecked == true,
            eng_day = chk_eng_day.IsChecked == true,
            eng_hour = chk_eng_hour.IsChecked == true,
            eng_cost = chk_eng_cost.IsChecked == true,
            ast_name = chk_ast_name.IsChecked == true,
            ast_count = chk_ast_count.IsChecked == true,
            ast_day = chk_ast_day.IsChecked == true,
            ast_hour = chk_ast_hour.IsChecked == true,
            ast_cost = chk_ast_cost.IsChecked == true,
            total_man = chk_total_man.IsChecked == true,
            person = chk_person.IsChecked == true,
            dist = chk_dist.IsChecked == true,
            vehicle = chk_vehicle.IsChecked == true,
            trans = chk_trans.IsChecked == true,
            use_count = chk_use_count.IsChecked == true,
            equip = chk_equip.IsChecked == true,
            equip_name = chk_equip_name.IsChecked == true,
            total = chk_total.IsChecked == true,

            // ★v0.9.7追加：ヘッダー情報の表示オプション
            // null許容ガードを入れる（XAML初期化中の参照タイミング対策）
            show_filter_info = chk_show_filter_info?.IsChecked == true,
            show_custom_rate_info = chk_show_custom_rate?.IsChecked == true,
        };

        // ---- 選択中タブのリスト取得 ----
        private List<filter_tab_view_model> get_selected_tabs()
        {
            if (_project_vm == null) return new();
            var result = new List<filter_tab_view_model>();
            if (rb_all_tab?.IsChecked == true)
            {
                if (_project_vm.all_records_tab != null)
                    result.Add(_project_vm.all_records_tab);
            }
            else if (rb_filter_select?.IsChecked == true)
            {
                result.AddRange(_check_items.Where(c => c.is_checked).Select(c => c.vm));
            }
            else
            {
                if (_project_vm.all_records_tab != null)
                    result.Add(_project_vm.all_records_tab);
                result.AddRange(_project_vm.filter_tabs);
            }
            return result;
        }

        // ---- 期間フィルター適用 ----
        private List<Models.cost_record> filter_records(
            IEnumerable<Models.cost_record> records)
        {
            var result = records.AsEnumerable();
            if (!string.IsNullOrEmpty(date_from()))
                result = result.Where(r => string.Compare(r.record_date, date_from()) >= 0);
            if (!string.IsNullOrEmpty(date_to()))
                result = result.Where(r => string.Compare(r.record_date, date_to()) <= 0);
            return result.ToList();
        }

        private string? date_from() =>
            (chk_period.IsChecked == true && dp_from.SelectedDate.HasValue)
                ? dp_from.SelectedDate.Value.ToString("yyyy-MM-dd") : null;
        private string? date_to() =>
            (chk_period.IsChecked == true && dp_to.SelectedDate.HasValue)
                ? dp_to.SelectedDate.Value.ToString("yyyy-MM-dd") : null;

        // ---- フォーマットヘルパー ----
        private static string fmt_date(string? d) =>
            string.IsNullOrEmpty(d) ? "" :
            DateTime.TryParse(d, out var dt) ? dt.ToString("M/d") : d;
        private static string fmt_num(double v) =>
            v == 0 ? "" : v.ToString("0.##");
        private static string fmt_yen(decimal v) =>
            v == 0 ? "" : v.ToString("#,##0");
    }
}
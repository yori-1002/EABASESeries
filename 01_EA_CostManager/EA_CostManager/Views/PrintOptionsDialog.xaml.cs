using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EA_CostManager.Services;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Views
{
    /// <summary>
    /// 絞り込みタブ選択用の中間モデル（ListBoxのCheckBox用）
    /// </summary>
    public class filter_tab_check_item
    {
        public filter_tab_view_model vm { get; }
        public string tab_name => vm.tab_name;
        public bool is_checked { get; set; } = false;
        public filter_tab_check_item(filter_tab_view_model vm) { this.vm = vm; }
    }

    /// <summary>
    /// 印刷設定ダイアログ
    /// 左に設定オプション、右に常時プレビューを表示する
    /// オプション変更のたびに右のプレビューが自動更新される
    /// </summary>
    public partial class PrintOptionsDialog : Window
    {
        private readonly project_cost_view_model _project_vm;
        private readonly List<filter_tab_check_item> _check_items = new();

        // プレビュー更新中フラグ（再入防止）
        private bool _updating_preview = false;

        public PrintOptionsDialog(project_cost_view_model project_vm)
        {
            // ▼▼▼ 重要：InitializeComponent()より前にセット ▼▼▼
            _project_vm = project_vm;

            InitializeComponent();

            // ▼▼▼ [Sprint 5G] 全件タブ＋絞り込みタブを全てチェックボックスに追加 ▼▼▼
            // 全件タブを先頭に追加（デフォルトでチェック済み）
            if (project_vm.all_records_tab != null)
                _check_items.Add(new filter_tab_check_item(project_vm.all_records_tab)
                { is_checked = true });

            // 絞り込みタブを追加（デフォルトでチェック済み）
            foreach (var ft in project_vm.filter_tabs)
                _check_items.Add(new filter_tab_check_item(ft) { is_checked = true });

            list_filter_tabs.ItemsSource = _check_items;

            // 初期プレビューを表示
            Loaded += (_, _) => update_preview();
        }

        // ---- オプション変更の統合ハンドラー ----
        private void option_Changed(object sender, RoutedEventArgs e)
        {
            if (_updating_preview) return;

            // 期間設定エリアの表示切替
            if (grid_period != null)
                grid_period.Visibility = chk_period.IsChecked == true
                    ? Visibility.Visible : Visibility.Collapsed;

            update_preview();
        }

        // ---- プレビュー更新 ----
        private void update_preview()
        {
            if (_project_vm == null || doc_reader == null) return;

            _updating_preview = true;
            try
            {
                if (!validate_period_silent()) return;

                var tabs = get_selected_tabs();

                // 印刷情報テキストを更新
                update_info_text(tabs);

                if (tabs.Count == 0)
                {
                    // 対象なしのときは中央に大きく表示
                    var no_data_para = new Paragraph(
                        new Run("印刷するタブを選択してください。")
                        {
                            FontSize = 20,
                            FontWeight = System.Windows.FontWeights.Bold,
                            Foreground = System.Windows.Media.Brushes.Gray,
                        })
                    {
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 120, 0, 0),
                    };
                    var empty_doc = new FlowDocument(no_data_para)
                    {
                        PageWidth = PrintService.PAGE_WIDTH,
                        PageHeight = PrintService.PAGE_HEIGHT,
                        PagePadding = new Thickness(48),
                        ColumnWidth = double.MaxValue,
                        Background = System.Windows.Media.Brushes.White,
                    };
                    doc_reader.Document = empty_doc;
                    return;
                }

                // FlowDocumentを生成してプレビューに反映
                var doc = PrintService.build_document(
                    _project_vm, tabs, date_from(), date_to(),
                    get_selected_columns()); // ▼▼▼ [Sprint 5G] 列オプション追加 ▼▼▼
                doc_reader.Document = doc;
                // ページ数を上部ナビゲーションバーに表示
                update_page_info(doc);
            }
            catch (Exception ex)
            {
                // ▼▼▼ 修正：例外をプレビューに表示して原因を確認できるようにする ▼▼▼
                System.Diagnostics.Debug.WriteLine($"プレビュー更新エラー: {ex.Message}\n{ex.StackTrace}");
                try
                {
                    var err_doc = new FlowDocument(
                        new Paragraph(new Run($"プレビューエラー：{ex.Message}"))
                        { Foreground = System.Windows.Media.Brushes.Red })
                    {
                        PageWidth = PrintService.PAGE_WIDTH,
                        PageHeight = PrintService.PAGE_HEIGHT,
                        PagePadding = new Thickness(48),
                        ColumnWidth = double.MaxValue,
                        Background = System.Windows.Media.Brushes.White,
                    };
                    doc_reader.Document = err_doc;
                }
                catch { /* エラー表示自体が失敗した場合は無視 */ }
            }
            finally
            {
                _updating_preview = false;
            }
        }

        // ---- 印刷情報テキスト更新 ----
        private void update_info_text(List<filter_tab_view_model> tabs)
        {
            if (txt_info == null) return;
            int total = tabs.Sum(t =>
            {
                var recs = t.raw_records.AsEnumerable();
                if (!string.IsNullOrEmpty(date_from()))
                    recs = recs.Where(r => string.Compare(r.record_date, date_from()) >= 0);
                if (!string.IsNullOrEmpty(date_to()))
                    recs = recs.Where(r => string.Compare(r.record_date, date_to()) <= 0);
                return recs.Count();
            });
            txt_info.Text = $"印刷対象：{tabs.Count}タブ / {total}行";
        }

        // ---- 期間バリデーション（サイレント版：エラーメッセージ更新のみ） ----
        private bool validate_period_silent()
        {
            if (txt_period_error == null) return true;
            if (chk_period.IsChecked != true)
            {
                txt_period_error.Visibility = Visibility.Collapsed;
                return true;
            }
            if (dp_from.SelectedDate.HasValue && dp_to.SelectedDate.HasValue
                && dp_from.SelectedDate > dp_to.SelectedDate)
            {
                txt_period_error.Text = "※ 開始日は終了日より前に設定してください";
                txt_period_error.Visibility = Visibility.Visible;
                return false;
            }
            txt_period_error.Visibility = Visibility.Collapsed;
            return true;
        }

        // ---- 印刷 ----
        private void btn_print_Click(object sender, RoutedEventArgs e)
        {
            if (!validate_period_silent())
            {
                MessageBox.Show("期間の設定が正しくありません。", "入力エラー",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var tabs = get_selected_tabs();
            if (tabs.Count == 0)
            {
                MessageBox.Show("印刷するタブを選択してください。", "選択なし",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var pd = new System.Windows.Controls.PrintDialog();
            if (pd.ShowDialog() != true) return;

            // A4横レイアウトを指定
            pd.PrintTicket.PageOrientation = System.Printing.PageOrientation.Landscape;

            // ▼▼▼ 修正：footer_paginator経由で印刷（ページ番号・現場名フッター付き） ▼▼▼
            if (doc_reader.Document is FlowDocument current_doc)
            {
                string footer_left = $"EA_CostManager　{_project_vm.category_code}_{_project_vm.site_name}";
                var paginator = PrintService.create_paginator(current_doc, footer_left);
                pd.PrintDocument(paginator,
                    $"EA_CostManager 原価集計 - {_project_vm.tab_name}");
            }
        }

        // ---- キャンセル ----
        private void btn_cancel_Click(object sender, RoutedEventArgs e) => Close();

        // ---- ▼▼▼ 追加：カスタムナビゲーションバー処理 ▼▼▼ ----

        // ズームイン（+10%、最大200%）
        private void btn_zoom_in_Click(object sender, RoutedEventArgs e)
        {
            doc_reader.Zoom = Math.Min(doc_reader.Zoom + 10, 200);
            txt_zoom.Text = $"{(int)doc_reader.Zoom}%";
        }

        // ズームアウト（-10%、最小30%）
        private void btn_zoom_out_Click(object sender, RoutedEventArgs e)
        {
            doc_reader.Zoom = Math.Max(doc_reader.Zoom - 10, 30);
            txt_zoom.Text = $"{(int)doc_reader.Zoom}%";
        }

        // 前のページ
        private void btn_prev_page_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Input.NavigationCommands.PreviousPage.Execute(null, doc_reader);
        }

        // 次のページ
        private void btn_next_page_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Input.NavigationCommands.NextPage.Execute(null, doc_reader);
        }

        // ドキュメントセット時にページ数を取得して表示する
        private void update_page_info(FlowDocument? doc)
        {
            if (txt_page_info == null) return;
            if (doc == null) { txt_page_info.Text = ""; return; }
            try
            {
                // FlowDocumentのページネーターからページ数を取得
                var src = (IDocumentPaginatorSource)doc;
                src.DocumentPaginator.PageSize =
                    new System.Windows.Size(PrintService.PAGE_WIDTH, PrintService.PAGE_HEIGHT);
                src.DocumentPaginator.ComputePageCount();
                int total = src.DocumentPaginator.PageCount;
                txt_page_info.Text = $"全 {total} ページ";
            }
            catch
            {
                txt_page_info.Text = "";
            }
        }

        // ---- ヘルパー：選択中タブのリスト取得 ----
        // ▼▼▼ [Sprint 5G] チェックボックス方式に変更 ▼▼▼
        private List<filter_tab_view_model> get_selected_tabs()
        {
            return _check_items
                .Where(c => c.is_checked)
                .Select(c => c.vm)
                .ToList();
        }

        // ▼▼▼ [Sprint 5G] 追加：列選択オプションを取得 ▼▼▼
        // ★v0.9.7修正：ヘッダー情報オプション（show_filter_info / show_custom_rate_info）を追加
        private PrintColumnOptions get_selected_columns() => new()
        {
            eng_name = chk_eng_name?.IsChecked == true,
            eng_day = chk_eng_day?.IsChecked == true,
            eng_cost = chk_eng_cost?.IsChecked == true,
            ast_name = chk_ast_name?.IsChecked == true,
            ast_day = chk_ast_day?.IsChecked == true,
            ast_cost = chk_ast_cost?.IsChecked == true,
            personnel = chk_personnel?.IsChecked == true,
            transport = chk_transport?.IsChecked == true,
            equipment = chk_equipment?.IsChecked == true,
            equip_name = chk_equip_name?.IsChecked == true,

            // ★v0.9.7追加：ヘッダー情報の表示オプション
            // 絞り込み条件・カスタム単価情報を印刷ヘッダーに出力するかを制御
            show_filter_info = chk_show_filter_info?.IsChecked == true,
            show_custom_rate_info = chk_show_custom_rate?.IsChecked == true,
        };

        // ---- ヘルパー：期間文字列 ----
        private string? date_from() =>
            (chk_period.IsChecked == true && dp_from.SelectedDate.HasValue)
                ? dp_from.SelectedDate.Value.ToString("yyyy-MM-dd") : null;

        private string? date_to() =>
            (chk_period.IsChecked == true && dp_to.SelectedDate.HasValue)
                ? dp_to.SelectedDate.Value.ToString("yyyy-MM-dd") : null;
    }
}
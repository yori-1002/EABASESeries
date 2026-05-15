using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Dapper;                              // ▼ 追加：v0.1.8 SQL 実行用（QueryAsync）
using EA_DailyReport.Data;                 // ▼ 追加：v0.1.8 database_manager 参照
using EA_DailyReport.Models;
using EA_DailyReport.Services;
using EA_DailyReport.Views.Dialogs;

namespace EA_DailyReport.Views.Pages
{
    /// <summary>
    /// 日報一覧画面（v0.1.4 設計書 2.1・要件 ⑦）
    /// 案_日報レイアウト 22 列 DataGrid で表示
    /// 月度フィルター・氏名フィルター対応
    ///
    /// ▼ v0.1.7 修正：
    /// MainWindow から「年/月/氏名フィルター」を指定して開けるようコンストラクタ追加
    /// 「📅 今月の日報」クイックボタン → 当月度+自分指定
    /// 月度ツリー → 任意月度・全員
    ///
    /// ▼ v0.1.8 修正：
    /// 月度ドロップダウンを DB ベース化（実データある月度のみ＋当月度フォールバック）
    /// 旧実装：固定で過去6ヶ月＋当月度の7ヶ月を機械生成 → 空月度も表示されるバグ
    /// 新実装：daily_reports から月度を集計（MainWindow 月度ツリーと同等ロジック）
    /// </summary>
    public partial class DailyReportPage : Page
    {
        // 月度範囲（前月20日〜当月21日のサイクル）
        // 要件定義書 3.1 月度サイクル：前月20日〜当月21日
        private List<MonthRange> _months = new();

        // ▼ 追加：v0.1.7 起動時に適用する初期フィルター
        // null の場合は当月度・全員（既存挙動）
        private readonly int? _init_year;
        private readonly int? _init_month;
        private readonly string? _init_employee;

        public DailyReportPage()
        {
            InitializeComponent();
            Loaded += on_loaded;
        }

        /// <summary>
        /// ▼ 追加：v0.1.7
        /// 月度・氏名フィルターを指定して開くコンストラクタ
        /// </summary>
        /// <param name="year">起動時に適用する年度（例：2026）</param>
        /// <param name="month">起動時に適用する月度（例：5）</param>
        /// <param name="employee_filter">氏名フィルター（null=全員）</param>
        public DailyReportPage(int year, int month, string? employee_filter)
        {
            InitializeComponent();
            _init_year = year;
            _init_month = month;
            _init_employee = employee_filter;
            Loaded += on_loaded;
        }

        private async void on_loaded(object sender, RoutedEventArgs e)
        {
            // ▼ 修正：v0.1.8 月度コンボボックスを DB ベース初期化に変更
            // 旧：init_month_combo()（固定7ヶ月を機械生成）
            // 新：init_month_combo_async()（daily_reports から実データある月度を取得）
            await init_month_combo_async();
            // 氏名コンボボックスを初期化（後でDB取得した氏名で充実）
            init_employee_combo();

            // ▼ 追加：v0.1.7 起動時の初期フィルター適用
            // MainWindow から (年, 月, 氏名) 指定で開かれた場合
            if (_init_year.HasValue && _init_month.HasValue)
            {
                apply_init_filters();
            }

            // 初回データ読込
            await load_data_async();
        }

        /// <summary>
        /// ▼ 追加：v0.1.7
        /// コンストラクタで指定された (年, 月, 氏名) を画面に適用する
        /// 既存の月度コンボに該当月度がなければ追加する
        /// </summary>
        private void apply_init_filters()
        {
            if (!_init_year.HasValue || !_init_month.HasValue) return;

            int y = _init_year.Value;
            int m = _init_month.Value;

            // 月度範囲を作成（前月21日〜当月20日）
            int prev_y = y;
            int prev_m = m - 1;
            if (prev_m <= 0) { prev_m += 12; prev_y--; }
            var range = new MonthRange
            {
                label = $"{y}年{m}月度（{prev_m}/21〜{m}/20）",
                start = new DateTime(prev_y, prev_m, 21),
                end = new DateTime(y, m, 20),
            };

            // 既存リスト内に該当月度があれば選択、無ければ先頭に追加して選択
            int idx = _months.FindIndex(r => r.start == range.start && r.end == range.end);
            if (idx < 0)
            {
                _months.Insert(0, range);
                cmb_month.ItemsSource = null;
                cmb_month.ItemsSource = _months;
                cmb_month.DisplayMemberPath = "label";
                idx = 0;
            }
            cmb_month.SelectedIndex = idx;

            // 氏名フィルター
            if (!string.IsNullOrWhiteSpace(_init_employee))
            {
                if (!cmb_employee.Items.Contains(_init_employee))
                    cmb_employee.Items.Add(_init_employee);
                cmb_employee.SelectedItem = _init_employee;
            }
        }

        // ────────────────────────────────────────────────
        // 公開メソッド：MainWindow から呼び出される
        // ────────────────────────────────────────────────

        /// <summary>
        /// 一覧データを再読み込みする
        /// MainWindow の「最新を取得」ボタンから呼ばれる
        /// </summary>
        public async void reload_data()
        {
            await load_data_async();
        }

        // ────────────────────────────────────────────────
        // ▼ 修正：v0.1.8 月度コンボボックスの初期化（DB ベース化）
        // ────────────────────────────────────────────────

        /// <summary>
        /// ▼ 修正：v0.1.8
        /// 月度コンボボックスを DB から実データのある月度のみで初期化する
        ///
        /// 処理ロジック：
        /// ① daily_reports.report_date を全件取得して月度サイクルでグルーピング
        /// ② 当月度は実データ無くても必ず含める（入力導線確保のため）
        /// ③ 新しい順にソートしてコンボボックスにバインド
        /// ④ DB アクセス失敗時は旧ロジック（固定7ヶ月）にフォールバック
        ///
        /// 注意：MainWindow.build_month_tree_async() と同等の月度判定ロジック。
        /// ヘルパーメソッド（date_to_month_period / get_current_month_period）は
        /// 両ファイルに重複定義になっているが、v0.1.8 時点では共通化せず置いておく。
        /// 共通化は別タスク（リファクタリング項目）として基本設計書に記録する想定。
        /// </summary>
        private async Task init_month_combo_async()
        {
            _months.Clear();

            try
            {
                // ─── ① daily_reports から月度を集計 ───
                // MainWindow の月度ツリーと同じクエリ
                using var conn = database_manager.create_connection();
                var raw = (await conn.QueryAsync<(string report_date, int cnt)>(@"
                    SELECT report_date, COUNT(*) AS cnt
                    FROM daily_reports
                    WHERE report_date IS NOT NULL AND report_date <> ''
                    GROUP BY report_date
                ")).ToList();

                // ─── ② 月度サイクル（前月21日〜当月20日）でグルーピング ───
                // 月度の集合を作るので HashSet（重複自動排除）
                var month_set = new HashSet<(int year, int month)>();
                foreach (var (date_str, _) in raw)
                {
                    if (!DateTime.TryParse(date_str, out var dt)) continue;
                    month_set.Add(date_to_month_period(dt));
                }

                // ─── ③ 当月度は実データ無くても必ず含める ───
                // 入力開始の導線を確保するため（要件㊶ の解釈：当月度は常時表示）
                month_set.Add(get_current_month_period());

                // ─── ④ 月度範囲リストを生成（新しい順） ───
                foreach (var (y, m) in month_set
                    .OrderByDescending(x => x.year)
                    .ThenByDescending(x => x.month))
                {
                    int prev_y = y;
                    int prev_m = m - 1;
                    if (prev_m <= 0) { prev_m += 12; prev_y--; }

                    _months.Add(new MonthRange
                    {
                        label = $"{y}年{m}月度（{prev_m}/21〜{m}/20）",
                        start = new DateTime(prev_y, prev_m, 21),
                        end = new DateTime(y, m, 20),
                    });
                }
            }
            catch (Exception ex)
            {
                // DB アクセス失敗時は旧ロジック（固定7ヶ月）にフォールバック
                // 起動時に DB がまだ初期化されてない等の例外ケースを想定
                System.Diagnostics.Debug.WriteLine(
                    $"[DailyReportPage] 月度コンボ DB 取得失敗・固定リストにフォールバック: {ex.Message}");
                fallback_init_month_combo();
            }

            // コンボボックスにバインド
            cmb_month.ItemsSource = null;
            cmb_month.ItemsSource = _months;
            cmb_month.DisplayMemberPath = "label";
            cmb_month.SelectedIndex = 0;  // 先頭（最新月度）を選択
        }

        /// <summary>
        /// ▼ 追加：v0.1.8
        /// 月度コンボボックスのフォールバック初期化（旧ロジック保持）
        /// DB アクセスが失敗した場合のみ呼ばれる。
        /// 過去6ヶ月＋当月度＝7ヶ月分を機械生成する。
        /// </summary>
        private void fallback_init_month_combo()
        {
            _months.Clear();

            var today = DateTime.Today;

            // 当月度 = 21日以降なら翌月度、それ以外は当月度
            // 例：4/21〜5/20 は 5月度、5/21〜6/20 は 6月度
            int base_year = today.Year;
            int base_month = today.Month;
            if (today.Day >= 21)
                base_month++;
            if (base_month > 12) { base_month = 1; base_year++; }

            // 過去6ヶ月＋当月度＝7ヶ月分を表示
            for (int i = 0; i < 7; i++)
            {
                int y = base_year;
                int m = base_month - i;
                while (m <= 0) { m += 12; y--; }

                int prev_y = y;
                int prev_m = m - 1;
                if (prev_m <= 0) { prev_m += 12; prev_y--; }

                var range = new MonthRange
                {
                    label = $"{y}年{m}月度（{prev_m}/21〜{m}/20）",
                    start = new DateTime(prev_y, prev_m, 21),
                    end = new DateTime(y, m, 20),
                };
                _months.Add(range);
            }
        }

        /// <summary>
        /// ▼ 追加：v0.1.8
        /// 日付から月度サイクルの (year, month) を計算する。
        /// 月度サイクル：前月21日〜当月20日
        /// 例：4/25 → 5月度、5/15 → 5月度、5/21 → 6月度
        ///
        /// 注意：MainWindow.date_to_month_period と同一実装。
        /// v0.1.8 時点では共通化せず両ファイルに重複定義。
        /// （将来のリファクタリング項目）
        /// </summary>
        private static (int year, int month) date_to_month_period(DateTime dt)
        {
            int year = dt.Year;
            int month = dt.Month;
            if (dt.Day >= 21)
            {
                month++;
                if (month > 12) { month = 1; year++; }
            }
            return (year, month);
        }

        /// <summary>
        /// ▼ 追加：v0.1.8
        /// 当月度の (year, month) を取得する。
        /// 月度サイクル：前月21日〜当月20日
        ///
        /// 注意：MainWindow.get_current_month_period と同一実装。
        /// v0.1.8 時点では共通化せず両ファイルに重複定義。
        /// </summary>
        private static (int year, int month) get_current_month_period()
        {
            return date_to_month_period(DateTime.Today);
        }

        // ────────────────────────────────────────────────
        // 氏名コンボボックスの初期化
        // ────────────────────────────────────────────────

        private void init_employee_combo()
        {
            // 起動直後は「全員」のみ。データ読込後に氏名を追加する
            cmb_employee.Items.Clear();
            cmb_employee.Items.Add("全員");
            cmb_employee.SelectedIndex = 0;
        }

        // ────────────────────────────────────────────────
        // データ読込
        // ────────────────────────────────────────────────

        private async Task load_data_async()
        {
            try
            {
                if (cmb_month.SelectedItem is not MonthRange range)
                    return;

                string? employee_filter = cmb_employee.SelectedItem as string;
                if (employee_filter == "全員" || string.IsNullOrEmpty(employee_filter))
                    employee_filter = null;

                // 取得実行
                var rows = await DailyReportService.get_rows_async(
                    range.start, range.end, employee_filter);

                // DataGrid にバインド
                grid_reports.ItemsSource = rows;
                txt_record_count.Text = $"件数：{rows.Count}件";

                // 氏名コンボボックスを更新（重複除去・ソート）
                update_employee_combo(rows);

                // ▼ 追加：v0.1.7 タイトル動的表示（「日報一覧 - 2026年5月度」）
                // 月度をタイトルに含めることで「今月の日報」遷移時の視認性を改善
                update_page_title(range);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"日報データの取得に失敗しました。\n\n{ex.Message}",
                    "読込エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// ▼ 追加：v0.1.7
        /// ページタイトルに月度を埋め込んで動的に更新する
        /// 例：「日報一覧 - 2026年5月度」
        /// 「今月の日報」遷移時に自分が今月分を見ているか視覚的に分かりやすくする
        /// </summary>
        private void update_page_title(MonthRange range)
        {
            try
            {
                // range.end が当月度の最終日（5/20 など）なので、
                // その月を「○月度」として表示
                int year = range.end.Year;
                int month = range.end.Month;
                txt_page_title.Text = $"日報一覧 - {year}年{month}月度";
            }
            catch
            {
                // 失敗時はデフォルトのまま
                txt_page_title.Text = "日報一覧";
            }
        }

        /// <summary>
        /// 氏名コンボボックスを最新データから更新する
        /// 既存選択は維持（あれば）
        /// </summary>
        private void update_employee_combo(List<DailyReportRow> rows)
        {
            string? prev_selected = cmb_employee.SelectedItem as string;

            var names = rows.Select(r => r.employee_name)
                            .Where(n => !string.IsNullOrEmpty(n))
                            .Distinct()
                            .OrderBy(n => n)
                            .ToList();

            cmb_employee.SelectionChanged -= cmb_employee_SelectionChanged;
            cmb_employee.Items.Clear();
            cmb_employee.Items.Add("全員");
            foreach (var n in names) cmb_employee.Items.Add(n);

            // 元の選択を復元
            if (!string.IsNullOrEmpty(prev_selected) && cmb_employee.Items.Contains(prev_selected))
                cmb_employee.SelectedItem = prev_selected;
            else
                cmb_employee.SelectedIndex = 0;

            cmb_employee.SelectionChanged += cmb_employee_SelectionChanged;
        }

        // ────────────────────────────────────────────────
        // イベントハンドラー
        // ────────────────────────────────────────────────

        private async void cmb_month_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await load_data_async();
        }

        private async void cmb_employee_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await load_data_async();
        }

        /// <summary>
        /// 「+ 新規入力」ボタン → 入力ダイアログを開く
        /// </summary>
        private async void btn_new_input_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new InputDialog
            {
                Owner = Window.GetWindow(this)
            };
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                // 保存成功 → 一覧を再読込
                await load_data_async();
            }
        }

        /// <summary>
        /// 行ダブルクリック → 編集ダイアログを開く（要件定義書 ㉛）
        /// 過去データ編集制限は v0.1.4 では未実装（Sprint 2 予定）
        /// </summary>
        private async void grid_reports_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (grid_reports.SelectedItem is not DailyReportRow row)
                return;

            // 既存日報を読み込んで InputDialog に渡す
            var dlg = new InputDialog(row.daily_report_id)
            {
                Owner = Window.GetWindow(this)
            };
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                await load_data_async();
            }
        }

        // ────────────────────────────────────────────────
        // 内部クラス
        // ────────────────────────────────────────────────

        /// <summary>月度範囲（コンボボックスの ItemsSource 用）</summary>
        private class MonthRange
        {
            public string label { get; set; } = "";
            public DateTime start { get; set; }
            public DateTime end { get; set; }
        }
    }
}
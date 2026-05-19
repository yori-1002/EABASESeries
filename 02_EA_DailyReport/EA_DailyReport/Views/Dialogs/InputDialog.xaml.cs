using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dapper;
using EA_DailyReport.Data;
using EA_DailyReport.Models;
using EA_DailyReport.Services;

namespace EA_DailyReport.Views.Dialogs
{
    /// <summary>
    /// 日報入力ダイアログ（v0.1.8 全面書き直し版・UI のみ刷新）
    ///
    /// ▼ v0.1.8 改修方針（ロジック温存・UI 全面書き直し）：
    /// ・DataGrid 直編集 → 「単一作業フォーム + 追加済み一覧」のハイブリッド方式へ移行
    /// ・Tab キー駆動の高速入力に対応
    /// ・職種・区分詳細・作業時間残の自動表示を新規追加
    /// ・戻る・終了・保存の3ボタン体系に変更（終了は赤・誤クリック警告）
    /// ・閉じる前に状態別の確認ダイアログを表示
    ///
    /// ▼ v0.1.6 → v0.1.8 で温存しているロジック：
    /// ・マスタロード（load_master_data_async / load_employees_combo_async）
    /// ・既存日報検出 + 出退勤ロック（check_existing_and_lock_async / lock_time_fields）
    /// ・早朝・残業・深夜の自動算出（recalculate / WorkTimeCalculator 依存）
    /// ・DB 保存（DailyReportService.save_async）
    /// ・編集モード時の既存読込（load_existing_async）
    ///
    /// コンストラクタの daily_report_id：
    ///   0  → 新規入力モード
    ///   >0 → 既存日報の編集モード
    /// </summary>
    public partial class InputDialog : Window, INotifyPropertyChanged
    {
        private readonly int _editing_id;
        private DailyReport? _editing_report;

        // ListView のバインドソース（ObservableCollection で行追加・削除を即時反映）
        private ObservableCollection<WorkDetail> _details = new();

        // ▼ v0.1.6 内部状態：既存日報検出時のロック制御
        private bool _is_locked = false;

        // ▼ 追加：v0.1.8 編集中作業のインデックス
        // -1     → 新規追加モード（[追加] ボタン表示）
        // 0 以上 → 既存項目の編集モード（[更新][編集キャンセル] ボタン表示）
        private int _editing_work_index = -1;

        // ▼ 追加 v0.1.9（冗長#2#3 修正）：cmb_employee_changed の二重発火抑制フラグ
        //   xaml 側で SelectionChanged と LostFocus の両方に同じハンドラが登録されており、
        //   さらに init_for_new_input → try_set_default_employee_name 経由でも発火する。
        //   このフラグで check_existing_and_lock_async / load_job_type_for_employee_async の
        //   多重実行（DB クエリ多重発火）を防ぐ。
        private bool _is_checking_existing = false;

        // ▼ 追加：v0.1.8 定時時間の基準（Sprint 1 は固定 8.0h）
        // Sprint 2 で employee_work_settings 参照に切り替え予定
        private const double STANDARD_WORK_HOURS = 8.0;

        // ────────────────────────────────────────────────
        // ▼ v0.1.6 マスタリスト（AutoCompleteTextBox の ItemsSource 経由でバインド）
        // 各 ControlsAutoCompleteTextBox は ItemsSource={Binding ElementName=this_dialog, Path=xxx_list}
        // で参照する。INotifyPropertyChanged で更新通知を発火する。
        // ────────────────────────────────────────────────

        private List<string> _category_list = new();
        public List<string> category_list
        {
            get => _category_list;
            set { _category_list = value; on_property_changed(nameof(category_list)); }
        }

        private List<string> _project_name_list = new();
        public List<string> project_name_list
        {
            get => _project_name_list;
            set { _project_name_list = value; on_property_changed(nameof(project_name_list)); }
        }

        private List<string> _software_list = new();
        public List<string> software_list
        {
            get => _software_list;
            set { _software_list = value; on_property_changed(nameof(software_list)); }
        }

        private List<string> _vehicle_list = new();
        public List<string> vehicle_list
        {
            get => _vehicle_list;
            set { _vehicle_list = value; on_property_changed(nameof(vehicle_list)); }
        }

        // 移動方法は固定リスト（CostManager にマスタテーブル無しのため）
        private List<string> _transport_list =
            new() { "下道", "高速", "徒歩", "電車", "バス", "自転車" };
        public List<string> transport_list
        {
            get => _transport_list;
            set { _transport_list = value; on_property_changed(nameof(transport_list)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void on_property_changed(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ════════════════════════════════════════════════
        // コンストラクタ
        // ════════════════════════════════════════════════

        /// <summary>新規入力モード</summary>
        public InputDialog() : this(0) { }

        /// <summary>編集モード（既存日報のID指定）</summary>
        public InputDialog(int daily_report_id)
        {
            InitializeComponent();
            _editing_id = daily_report_id;

            // タイトル変更
            txt_dialog_title.Text = (daily_report_id == 0)
                ? "日報入力（新規）"
                : "日報編集";

            // ▼ 修正：v0.1.8 grid_details → lst_added_works に変更
            lst_added_works.ItemsSource = _details;

            // _details の変更時に表示更新（合計時間・残業対象・達成バッジ）
            _details.CollectionChanged += (s, ev) =>
            {
                update_totals_display();
                update_hours_remaining();
                update_list_section_label();
            };

            Loaded += on_loaded;
        }

        // ════════════════════════════════════════════════
        // 初期化
        // ════════════════════════════════════════════════

        private async void on_loaded(object sender, RoutedEventArgs e)
        {
            // ▼ v0.1.6 マスタロード（NAS取得済みローカルDBから）
            await load_master_data_async();

            // 氏名コンボボックスの選択肢を employees から設定
            await load_employees_combo_async();

            if (_editing_id > 0)
            {
                // 編集モード
                await load_existing_async();
                // 編集モードは初期状態でロック（VBA 同等）
                lock_time_fields(show_warning: true);
            }
            else
            {
                // 新規モード
                init_for_new_input();
                // 自動入力した氏名で重複チェック → 既存あればロック
                await check_existing_and_lock_async();
                // 氏名から職種を自動表示
                await load_job_type_for_employee_async();
            }

            // 初回の自動算出
            recalculate();

            // 表示の初期化
            update_totals_display();
            update_hours_remaining();
            update_list_section_label();
        }

        // ────────────────────────────────────────────────
        // ▼ v0.1.6 マスタロード（既存ロジックをそのまま温存）
        // ────────────────────────────────────────────────

        /// <summary>
        /// マスタリスト（区分・業務名・ソフト・車両）を ローカルDB から読み込む
        /// AutoCompleteTextBox のバインドソースになる
        ///
        /// データソース：
        ///   ・区分・業務名 → projects テーブル
        ///   ・ソフト・機材 → equipment_rates テーブル
        ///   ・車両         → vehicles テーブル
        /// </summary>
        private async Task load_master_data_async()
        {
            try
            {
                using var conn = database_manager.create_connection();

                // 区分（projects.category_code）
                var categories = (await conn.QueryAsync<string>(@"
                    SELECT DISTINCT category_code FROM projects
                    WHERE is_active = 1 AND category_code IS NOT NULL AND category_code <> ''
                    ORDER BY sort_order, category_code
                ")).ToList();
                category_list = categories;

                // 業務名（projects.site_name）
                var sites = (await conn.QueryAsync<string>(@"
                    SELECT DISTINCT site_name FROM projects
                    WHERE is_active = 1 AND site_name IS NOT NULL AND site_name <> ''
                    ORDER BY site_name
                ")).ToList();
                project_name_list = sites;

                // ソフト・機材（equipment_rates.equipment_name）
                var softwares = new List<string> { "なし" };
                try
                {
                    var s = (await conn.QueryAsync<string>(@"
                        SELECT DISTINCT equipment_name FROM equipment_rates
                        WHERE is_active = 1 AND equipment_name IS NOT NULL AND equipment_name <> ''
                        ORDER BY equipment_name
                    ")).ToList();
                    softwares.AddRange(s);
                }
                catch { /* テーブル無しは無視 */ }
                software_list = softwares;

                // 車両（vehicles.vehicle_name）
                var vehicles = new List<string> { "なし" };
                try
                {
                    var v = (await conn.QueryAsync<string>(@"
                        SELECT DISTINCT vehicle_name FROM vehicles
                        WHERE is_active = 1 AND vehicle_name IS NOT NULL AND vehicle_name <> ''
                        ORDER BY vehicle_name
                    ")).ToList();
                    vehicles.AddRange(v);
                }
                catch { /* テーブル無しは無視 */ }
                vehicle_list = vehicles;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[InputDialog] マスタロード失敗（候補なしで継続）: {ex.Message}");
            }
        }

        /// <summary>
        /// 氏名コンボボックスの選択肢を設定（employees マスタ + 既存日報の氏名）
        /// マスタに無い氏名（過去の退職者等）も含めるため UNION で結合
        /// </summary>
        private async Task load_employees_combo_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var names = (await conn.QueryAsync<string>(@"
                    SELECT employee_name FROM employees
                    WHERE is_active = 1 AND employee_name <> ''
                    UNION
                    SELECT DISTINCT employee_name FROM daily_reports
                    WHERE employee_name <> ''
                    ORDER BY 1
                ")).ToList();

                cmb_employee.Items.Clear();
                foreach (var n in names) cmb_employee.Items.Add(n);
            }
            catch
            {
                // 取得失敗時は空のコンボボックス（手入力で代用）
            }
        }

        // ────────────────────────────────────────────────
        // ▼ 追加：v0.1.8 職種の自動表示（要件㉒）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 選択中の氏名から職種を取得して txt_job_type に自動表示する
        /// employees テーブルから employee_name で検索
        /// 未登録または取得失敗時は空欄
        /// </summary>
        private async Task load_job_type_for_employee_async()
        {
            string name = cmb_employee.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                txt_job_type.Text = "";
                return;
            }

            try
            {
                using var conn = database_manager.create_connection();
                var job = await conn.QueryFirstOrDefaultAsync<string>(@"
                    SELECT job_type FROM employees
                    WHERE employee_name = @name AND is_active = 1
                    LIMIT 1",
                    new { name });

                txt_job_type.Text = job ?? "";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[InputDialog] 職種取得失敗（空欄で継続）: {ex.Message}");
                txt_job_type.Text = "";
            }
        }

        // ────────────────────────────────────────────────
        // 新規入力時の初期化
        // ────────────────────────────────────────────────

        /// <summary>
        /// ▼ 修正：v0.1.8 _details に空行を追加しなくなった
        /// 旧：DataGrid に1行追加していた
        /// 新：フォーム駆動のため一覧は空のスタートでよい
        /// </summary>
        private void init_for_new_input()
        {
            dp_report_date.SelectedDate = DateTime.Today;
            txt_clock_in.Text = "08:30";
            txt_clock_out.Text = "17:30";
            cmb_start_location.Text = "本社";
            cmb_end_location.Text = "本社";

            // ▼ v0.1.6 氏名のデフォルト自動入力
            try_set_default_employee_name();

            // ▼ 修正：v0.1.8 フォームに初期値をセット（_details への追加は廃止）
            clear_work_form();
        }

        /// <summary>
        /// ▼ v0.1.7 氏名のデフォルトをアクセス中ユーザーから取得して設定する
        /// UserSession.user_name を呼び出し
        /// </summary>
        private void try_set_default_employee_name()
        {
            try
            {
                if (UserSession.is_logged_in
                 && !string.IsNullOrWhiteSpace(UserSession.user_name))
                {
                    cmb_employee.Text = UserSession.user_name;
                }
            }
            catch
            {
                // UserSession 未初期化等は無視
            }
        }

        // ────────────────────────────────────────────────
        // 既存日報の読込（編集モード）
        // ────────────────────────────────────────────────

        /// <summary>
        /// ▼ 修正：v0.1.8 grid_details → _details / lst_added_works に変更
        /// ロジックは温存。最後の「1行も無ければ空行追加」は削除（フォーム駆動のため）
        /// </summary>
        private async Task load_existing_async()
        {
            try
            {
                using var conn = database_manager.create_connection();

                // daily_reports 取得
                _editing_report = await conn.QueryFirstOrDefaultAsync<DailyReport>(
                    "SELECT * FROM daily_reports WHERE id = @id",
                    new { id = _editing_id });

                if (_editing_report == null)
                {
                    MessageBox.Show(
                        "対象の日報が見つかりませんでした。",
                        "読込エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    DialogResult = false;
                    Close();
                    return;
                }

                // 画面に値をセット
                if (DateTime.TryParse(_editing_report.report_date, out var dt))
                    dp_report_date.SelectedDate = dt;
                cmb_employee.Text = _editing_report.employee_name;
                txt_clock_in.Text = _editing_report.clock_in;
                txt_clock_out.Text = _editing_report.clock_out;
                cmb_start_location.Text = _editing_report.start_location;
                cmb_end_location.Text = _editing_report.end_location;

                // 職種を自動表示
                await load_job_type_for_employee_async();

                // work_details 取得（論理削除済みは除外）
                var details = (await conn.QueryAsync<WorkDetail>(
                    "SELECT * FROM work_details WHERE daily_report_id = @id AND is_deleted = 0 ORDER BY sort_order, id",
                    new { id = _editing_id })).ToList();

                _details.Clear();
                foreach (var d in details) _details.Add(d);

                // ▼ 修正：v0.1.8 「1行も無ければ空行追加」を廃止（フォーム駆動）
                // 旧：if (_details.Count == 0) _details.Add(new WorkDetail { ... });

                // フォームは空のままにしておく
                clear_work_form();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"既存日報の読込に失敗しました。\n\n{ex.Message}",
                    "読込エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        // ────────────────────────────────────────────────
        // ▼ v0.1.6 ロック / アンロック制御（VBA 同等・温存）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 出退勤・場所コントロールを「ロック」状態にする
        /// </summary>
        private void lock_time_fields(bool show_warning)
        {
            _is_locked = true;

            var locked_bg = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
            txt_clock_in.IsReadOnly = true;
            txt_clock_in.Background = locked_bg;
            txt_clock_out.IsReadOnly = true;
            txt_clock_out.Background = locked_bg;

            cmb_start_location.IsEnabled = false;
            cmb_end_location.IsEnabled = false;

            btn_unlock.Visibility = Visibility.Visible;

            if (show_warning)
                border_existing_warning.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 出退勤・場所コントロールを「編集可能」状態に戻す
        /// </summary>
        private void unlock_time_fields()
        {
            _is_locked = false;

            var normal_bg = new SolidColorBrush(Colors.White);
            txt_clock_in.IsReadOnly = false;
            txt_clock_in.Background = normal_bg;
            txt_clock_out.IsReadOnly = false;
            txt_clock_out.Background = normal_bg;

            cmb_start_location.IsEnabled = true;
            cmb_end_location.IsEnabled = true;

            btn_unlock.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 「🔓 ロック解除」ボタンハンドラ
        /// 確認ダイアログで意思確認してからロック解除する
        /// </summary>
        private void btn_unlock_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "出退勤と場所を編集可能にいたします。\n\n" +
                "保存すると、この日のあなたの出退勤情報（全作業行に共通）が更新されます。\n" +
                "本当によろしいでしょうか？",
                "ロック解除の確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.OK)
            {
                unlock_time_fields();
            }
        }

        // ────────────────────────────────────────────────
        // ▼ v0.1.6 重複チェック（新規モード時の自動ロック・温存）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 日付変更時に呼ばれる（DatePicker.SelectedDateChanged）
        /// 新規モードのみ動作
        /// </summary>
        private async void report_date_or_name_changed(object sender, RoutedEventArgs e)
        {
            if (_editing_id > 0) return;
            if (!IsLoaded) return;

            await check_existing_and_lock_async();
        }

        /// <summary>
        /// ▼ 追加：v0.1.8
        /// 氏名コンボ変更時に呼ばれる（SelectionChanged / LostFocus）
        /// ・既存日報チェック（ロック制御）
        /// ・職種の自動表示
        /// 新規モードのみ動作
        /// </summary>
        private async void cmb_employee_changed(object sender, RoutedEventArgs e)
        {
            if (_editing_id > 0) return;
            if (!IsLoaded) return;

            // ▼ 追加 v0.1.9（冗長#2#3 修正）：
            //   SelectionChanged と LostFocus の二重発火、および try_set_default_employee_name
            //   経由の発火による check_existing_and_lock_async 多重実行を抑制する。
            if (_is_checking_existing) return;

            try
            {
                _is_checking_existing = true;  // ▼ 追加 v0.1.9：実行中フラグを立てる
                await check_existing_and_lock_async();
                await load_job_type_for_employee_async();
            }
            finally
            {
                _is_checking_existing = false;  // ▼ 追加 v0.1.9：例外発生時も必ず解除
            }
        }

        /// <summary>
        /// 「同じ日 × 同じ人」の既存日報をローカルDBから検索する
        /// 見つかれば → 既存値を表示してロック
        /// 見つからなければ → アンロック（新規入力可）
        /// </summary>
        private async Task check_existing_and_lock_async()
        {
            string name = cmb_employee.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name)) return;
            if (dp_report_date.SelectedDate == null) return;

            string date_str = dp_report_date.SelectedDate.Value.ToString("yyyy-MM-dd");

            try
            {
                using var conn = database_manager.create_connection();
                var existing = await conn.QueryFirstOrDefaultAsync<DailyReport>(@"
                    SELECT * FROM daily_reports
                    WHERE report_date = @date AND employee_name = @name
                    LIMIT 1",
                    new { date = date_str, name });

                if (existing != null)
                {
                    txt_clock_in.Text = existing.clock_in ?? "";
                    txt_clock_out.Text = existing.clock_out ?? "";
                    cmb_start_location.Text = existing.start_location ?? "本社";
                    cmb_end_location.Text = existing.end_location ?? "本社";
                    lock_time_fields(show_warning: true);
                    recalculate();
                }
                else
                {
                    if (_is_locked) unlock_time_fields();
                    border_existing_warning.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[InputDialog] 重複チェック失敗（無視）: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────
        // 出退勤の自動算出（早朝・残業・深夜・温存）
        // ────────────────────────────────────────────────

        private void time_or_settings_changed(object sender, TextChangedEventArgs e)
        {
            recalculate();
        }

        /// <summary>
        /// ▼ v0.1.6 既存ロジック温存：早朝/残業/深夜の表示更新
        /// </summary>
        private void recalculate()
        {
            // InitializeComponent 中の TextChanged 発火対策
            if (txt_clock_in == null || txt_clock_out == null) return;
            if (txt_calc_early == null
                || txt_calc_overtime == null
                || txt_calc_late_night == null) return;

            var ti = WorkTimeCalculator.parse_time(txt_clock_in.Text);
            var to = WorkTimeCalculator.parse_time(txt_clock_out.Text);

            if (ti == null || to == null)
            {
                txt_calc_early.Text = "0:00";
                txt_calc_overtime.Text = "0:00";
                txt_calc_late_night.Text = "0:00";
                return;
            }

            var calc = WorkTimeCalculator.calculate(ti.Value, to.Value);
            txt_calc_early.Text = WorkTimeCalculator.format_minutes(calc.early_morning_min);
            txt_calc_overtime.Text = WorkTimeCalculator.format_minutes(calc.overtime_min);
            txt_calc_late_night.Text = WorkTimeCalculator.format_minutes(calc.late_night_min);
        }

        // ════════════════════════════════════════════════
        // ▼ 追加：v0.1.8 区分詳細の自動表示
        // ════════════════════════════════════════════════

        /// <summary>
        /// 区分の AutoCompleteTextBox の LostFocus 時に呼ばれる
        /// projects.detail を引いて区分詳細を表示
        /// ▼ 修正 v0.1.11：projects.name も取得して業務名（txt_project_name）に自動入力するように拡張。
        /// </summary>
        private async void txt_category_LostFocus(object sender, RoutedEventArgs e)
        {
            await load_category_detail_async();
        }

        /// <summary>
        /// 現在の区分から projects.site_name / projects.company_name を取得して
        /// 業務名（txt_project_name）と区分詳細（txt_category_detail）に自動表示
        ///
        /// ▼ 修正（実 DB スキーマ確認後）：
        ///   projects テーブルの実カラム構成（2026/05/19 DB 直接確認）：
        ///   id / category_code / site_name / company_name / detail / attribute /
        ///   tab_color / is_active / sort_order / created_at / updated_at / updated_by
        ///
        ///   業務上のマッピング（yori 確定仕様）：
        ///   ・「業務名(自動)」 = site_name（現場略称・例「鳥居水門_五洋建設」）
        ///   ・「区分詳細(自動)」 = company_name（正式工事名・例「和歌山下津港海岸鳥居水門築造工事」）
        ///   ・detail カラムは実データほぼ空のため使用しない
        ///
        ///   v0.1.11 では「name, detail」と書いていたが name カラムは存在せず SQLite エラー
        ///   になっていた（catch で握りつぶしていたため業務名が常に空欄表示だった）。
        /// </summary>
        private async Task load_category_detail_async()
        {
            string code = txt_category.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(code))
            {
                // 区分が空の場合は業務名と区分詳細を両方クリア
                txt_project_name.Text = "";
                txt_category_detail.Text = "";
                return;
            }

            try
            {
                using var conn = database_manager.create_connection();
                // ▼ 修正：実 DB カラムに合わせ SELECT site_name, company_name に変更
                //   QueryFirstOrDefaultAsync (非ジェネリック版) は dynamic を返す。
                //   dynamic.site_name / dynamic.company_name でカラム名アクセスできる（Dapper 標準仕様）。
                var row = await conn.QueryFirstOrDefaultAsync(@"
                    SELECT site_name, company_name FROM projects
                    WHERE category_code = @code AND is_active = 1
                    LIMIT 1",
                    new { code });

                if (row != null)
                {
                    // ▼ 修正：site_name → 業務名 / company_name → 区分詳細
                    string? site_name = row.site_name as string;
                    string? company_name = row.company_name as string;
                    txt_project_name.Text = site_name ?? "";
                    txt_category_detail.Text = company_name ?? "";
                }
                else
                {
                    // 該当する区分がマスタに無い場合は両方クリア
                    txt_project_name.Text = "";
                    txt_category_detail.Text = "";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[InputDialog] 業務名・区分詳細取得失敗（空欄で継続）: {ex.Message}");
                // 失敗時は両方クリア
                txt_project_name.Text = "";
                txt_category_detail.Text = "";
            }
        }

        // ════════════════════════════════════════════════
        // ▼ 追加：v0.1.8 作業時間残・合計表示の自動更新
        // ════════════════════════════════════════════════

        /// <summary>
        /// 作業時間 TextBox の変更時に呼ばれる
        /// 作業時間残を再計算して表示更新
        /// </summary>
        private void txt_hours_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            update_hours_remaining();
        }

        /// <summary>
        /// 作業時間残を計算して txt_hours_remaining に表示
        /// 計算式：STANDARD_WORK_HOURS − (一覧合計 + フォーム入力中の時間)
        /// 編集モード時：一覧合計から編集中の項目を除外（フォーム値で代替）
        /// </summary>
        private void update_hours_remaining()
        {
            if (txt_hours_remaining == null) return;

            double form_hours = 0;
            if (!string.IsNullOrWhiteSpace(txt_hours?.Text))
                double.TryParse(txt_hours.Text, out form_hours);

            // 一覧内の合計（編集中の項目は除外する）
            double list_hours = 0;
            for (int i = 0; i < _details.Count; i++)
            {
                if (i == _editing_work_index) continue;  // 編集中は除外
                list_hours += _details[i].hours;
            }

            double total_used = list_hours + form_hours;
            double remaining = STANDARD_WORK_HOURS - total_used;

            // 表示書式：マイナスは「+Xh 超過」、プラスは「Xh」
            if (remaining < 0)
            {
                txt_hours_remaining.Text = $"+{Math.Abs(remaining):0.##}h 超過";
                txt_hours_remaining.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            }
            else
            {
                txt_hours_remaining.Text = $"{remaining:0.##}h";
                txt_hours_remaining.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            }
        }

        /// <summary>
        /// 合計作業時間・残業対象・達成バッジを表示更新
        /// </summary>
        private void update_totals_display()
        {
            if (txt_total_hours == null) return;

            double total = _details.Sum(d => d.hours);

            txt_total_hours.Text = $"{total:0.##}h";

            // 残業対象 = max(0, 合計 - 定時)
            double overtime = Math.Max(0, total - STANDARD_WORK_HOURS);
            txt_overtime_target.Text = $"{overtime:0.##}h";

            // 達成バッジ
            if (total >= STANDARD_WORK_HOURS)
                txt_achievement_badge.Text = $"✓ 定時（{STANDARD_WORK_HOURS}h）達成";
            else
                txt_achievement_badge.Text = "";
        }

        /// <summary>
        /// 一覧セクションの見出し更新（件数表示）
        /// </summary>
        private void update_list_section_label()
        {
            if (txt_list_section_label == null) return;
            txt_list_section_label.Text = $"本日追加済み作業（{_details.Count}件）";
        }

        // ════════════════════════════════════════════════
        // ▼ 追加：v0.1.8 作業の追加・更新・編集キャンセル・削除
        // ════════════════════════════════════════════════

        /// <summary>
        /// <summary>
        /// ▼ 追加：作業フォームの必須項目チェック + 区分妥当性チェック
        /// btn_add_work_Click / btn_update_work_Click の両方から呼び出す。
        /// 戻り値: true = 検証 OK / false = 検証 NG（MessageBox 表示済み・呼び出し元は return する）
        ///
        /// 必須項目仕様：
        ///   ・区分 (空 NG + category_list に存在しないものは「不正な区分」エラー)
        ///   ・内容 (空 NG)
        ///   ・作業時間 (空 / 0 以下 / 数値変換不可 は NG)
        ///   ・ソフト機材 (空 NG・「なし」は許容)
        ///   ・車両 (空 NG・「なし」は許容)
        ///   ・車両が「なし」以外の場合 → 移動方法・発・着・距離 も必須
        ///     (移動方法は "-" 不可・発着は "-" 不可・距離は 0 不可)
        /// </summary>
        private bool validate_work_form()
        {
            // ----- 1. 区分の必須チェック -----
            string category = txt_category.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(category))
            {
                show_required_field_message();
                txt_category.Focus();
                return false;
            }

            // ----- 2. 区分が category_list に存在するかチェック（不正区分） -----
            // 区分マスタに無い区分が手入力された場合に「不正な区分」アナウンス
            if (category_list != null && !category_list.Contains(category))
            {
                MessageBox.Show(
                    "この区分は不正です。\n正しい区分を選択するか、区分の追加をして下さい。",
                    "不正な区分",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txt_category.Focus();
                return false;
            }

            // ----- 3. 内容の必須チェック -----
            if (string.IsNullOrWhiteSpace(txt_detail.Text))
            {
                show_required_field_message();
                txt_detail.Focus();
                return false;
            }

            // ----- 4. 作業時間の必須チェック（空・数値変換不可・0 以下を NG） -----
            if (string.IsNullOrWhiteSpace(txt_hours.Text)
                || !double.TryParse(txt_hours.Text, out double hours)
                || hours <= 0)
            {
                show_required_field_message();
                txt_hours.Focus();
                return false;
            }

            // ----- 5. ソフト機材の必須チェック（「なし」を選んでもらうことを期待） -----
            if (string.IsNullOrWhiteSpace(txt_software.Text))
            {
                show_required_field_message();
                txt_software.Focus();
                return false;
            }

            // ----- 6. 車両の必須チェック（「なし」を選んでもらうことを期待） -----
            string vehicle = txt_vehicle.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(vehicle))
            {
                show_required_field_message();
                txt_vehicle.Focus();
                return false;
            }

            // ----- 7. 車両が「なし」以外の場合 → 移動方法・発・着・距離も必須 -----
            // 車を使う作業 = 移動の詳細を必ず入力する必要がある仕様
            if (vehicle != "なし")
            {
                string transport = txt_transport.Text?.Trim() ?? "";
                string from_loc = txt_from_location.Text?.Trim() ?? "";
                string to_loc = txt_to_location.Text?.Trim() ?? "";
                string distance_str = txt_distance.Text?.Trim() ?? "";

                // 7-1. 移動方法（"-" や空は NG・「下道」「高速」のいずれかが入っているべき）
                if (string.IsNullOrWhiteSpace(transport) || transport == "-")
                {
                    show_required_field_message();
                    txt_transport.Focus();
                    return false;
                }

                // 7-2. 発（"-" や空は NG）
                if (string.IsNullOrWhiteSpace(from_loc) || from_loc == "-")
                {
                    show_required_field_message();
                    txt_from_location.Focus();
                    return false;
                }

                // 7-3. 着（"-" や空は NG）
                if (string.IsNullOrWhiteSpace(to_loc) || to_loc == "-")
                {
                    show_required_field_message();
                    txt_to_location.Focus();
                    return false;
                }

                // 7-4. 距離（空・0・数値変換不可は NG）
                if (string.IsNullOrWhiteSpace(distance_str)
                    || !double.TryParse(distance_str, out double dist)
                    || dist <= 0)
                {
                    show_required_field_message();
                    txt_distance.Focus();
                    return false;
                }
            }

            // すべての必須項目 OK
            return true;
        }

        /// <summary>
        /// ▼ 追加：必須項目未入力時の共通アナウンス
        /// 同じメッセージを複数箇所で表示するため、メソッドに切り出して保守性を確保
        /// </summary>
        private void show_required_field_message()
        {
            MessageBox.Show(
                "必須項目が入力されていません。ご確認ください。",
                "入力不足",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// 「＋ この作業を追加」ボタン
        /// フォーム入力値を _details に追加 → フォームクリア → カーソルを区分欄に戻す
        /// ▼ 修正：validate_work_form() で必須項目チェック + 不正区分チェックを実施
        /// </summary>
        private void btn_add_work_Click(object sender, RoutedEventArgs e)
        {
            // ▼ 修正：旧来の簡易チェック（区分または業務名のいずれかが空）を廃止し、
            //   validate_work_form() で全必須項目と不正区分を検証する
            if (!validate_work_form()) return;

            var work = extract_work_from_form();
            work.sort_order = _details.Count;
            _details.Add(work);

            clear_work_form();

            // カーソルを区分欄に戻す（次の入力をスムーズに）
            txt_category.Focus();
        }

        /// <summary>
        /// 「✓ 更新」ボタン（編集モード時のみ表示）
        /// 編集中の _details インデックスをフォーム値で上書き
        /// ▼ 修正：validate_work_form() で必須項目チェック + 不正区分チェックを実施
        /// </summary>
        private void btn_update_work_Click(object sender, RoutedEventArgs e)
        {
            if (_editing_work_index < 0 || _editing_work_index >= _details.Count)
            {
                exit_edit_mode();
                return;
            }

            // ▼ 修正：追加時と同じ必須項目チェック + 不正区分チェック
            if (!validate_work_form()) return;

            var work = extract_work_from_form();

            // 既存項目の id / sort_order などのキー情報は維持
            var original = _details[_editing_work_index];
            work.id = original.id;
            work.daily_report_id = original.daily_report_id;
            work.sort_order = original.sort_order;
            work.created_at = original.created_at;

            _details[_editing_work_index] = work;

            exit_edit_mode();
            clear_work_form();
            txt_category.Focus();
        }

        /// <summary>
        /// 「編集キャンセル」ボタン（編集モード時のみ表示）
        /// フォームをクリアして新規追加モードに戻る
        /// </summary>
        private void btn_cancel_edit_Click(object sender, RoutedEventArgs e)
        {
            exit_edit_mode();
            clear_work_form();
        }

        /// <summary>
        /// 追加済み一覧のダブルクリック
        /// 該当行をフォームに復元 → 編集モードに移行
        /// </summary>
        private void lst_added_works_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lst_added_works.SelectedItem is WorkDetail work)
            {
                int index = _details.IndexOf(work);
                if (index >= 0)
                {
                    load_work_to_form(work);
                    enter_edit_mode(index);
                }
            }
        }

        /// <summary>
        /// 一覧の行内 [🗑] ボタンクリック
        /// 該当作業を _details から削除
        /// 編集中の項目を削除した場合は編集モード解除
        /// </summary>
        private void btn_delete_work_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not WorkDetail work) return;

            int target_index = _details.IndexOf(work);
            if (target_index < 0) return;

            var result = MessageBox.Show(
                $"この作業を一覧から削除いたします。\n\n" +
                $"区分: {work.category_code}\n" +
                $"業務名: {work.project_name}\n" +
                $"内容: {work.detail}\n\n" +
                $"よろしいでしょうか？",
                "削除の確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.OK) return;

            // 編集中の項目を削除する場合は編集モード解除
            if (_editing_work_index == target_index)
            {
                exit_edit_mode();
                clear_work_form();
            }
            else if (target_index < _editing_work_index)
            {
                // インデックスがずれるので補正
                _editing_work_index--;
            }

            _details.Remove(work);
        }

        // ────────────────────────────────────────────────
        // 編集モード制御・フォーム操作ヘルパー
        // ────────────────────────────────────────────────

        /// <summary>編集モードへ移行</summary>
        private void enter_edit_mode(int index)
        {
            _editing_work_index = index;

            btn_add_work.Visibility = Visibility.Collapsed;
            btn_update_work.Visibility = Visibility.Visible;
            btn_cancel_edit.Visibility = Visibility.Visible;

            txt_form_section_label.Text = "作業編集中（ダブルクリック元の行を修正中）";
            txt_edit_mode_banner_text.Text = $"📝 編集中：{index + 1} 件目を修正しています";
            border_edit_mode_banner.Visibility = Visibility.Visible;

            update_hours_remaining();
        }

        /// <summary>新規追加モードへ戻る</summary>
        private void exit_edit_mode()
        {
            _editing_work_index = -1;

            btn_add_work.Visibility = Visibility.Visible;
            btn_update_work.Visibility = Visibility.Collapsed;
            btn_cancel_edit.Visibility = Visibility.Collapsed;

            txt_form_section_label.Text = "作業入力（1作業ずつ・Tab で次フィールドへ）";
            border_edit_mode_banner.Visibility = Visibility.Collapsed;

            update_hours_remaining();
        }

        /// <summary>フォームを初期値で埋める（クリア）</summary>
        private void clear_work_form()
        {
            txt_category.Text = "";
            txt_category_detail.Text = "";
            txt_project_name.Text = "";
            txt_detail.Text = "";
            txt_note.Text = "";
            txt_hours.Text = "";
            txt_software.Text = "";       // ▼ 修正：入力忘れ防止のため "なし" → "" (空白)。車両と同じ対応
            txt_vehicle.Text = "";        // ▼ 修正 v0.1.10：入力忘れ防止のため "なし" → "" (空白)
            txt_transport.Text = "-";
            txt_from_location.Text = "-";
            txt_to_location.Text = "-";
            txt_distance.Text = "0";
        }

        /// <summary>WorkDetail の値をフォームにロード（編集モード移行時）</summary>
        private void load_work_to_form(WorkDetail work)
        {
            txt_category.Text = work.category_code ?? "";
            txt_project_name.Text = work.project_name ?? "";
            txt_detail.Text = work.detail ?? "";
            txt_note.Text = work.note ?? "";
            txt_hours.Text = work.hours.ToString();
            txt_software.Text = string.IsNullOrEmpty(work.software) ? "なし" : work.software;
            txt_vehicle.Text = string.IsNullOrEmpty(work.vehicle) ? "なし" : work.vehicle;
            txt_transport.Text = string.IsNullOrEmpty(work.transport) ? "-" : work.transport;
            txt_from_location.Text = string.IsNullOrEmpty(work.from_location) ? "-" : work.from_location;
            txt_to_location.Text = string.IsNullOrEmpty(work.to_location) ? "-" : work.to_location;
            txt_distance.Text = work.distance.ToString();

            // 区分詳細を非同期で取得（待たずに進める）
            _ = load_category_detail_async();
        }

        /// <summary>フォームの値から WorkDetail オブジェクトを生成</summary>
        private WorkDetail extract_work_from_form()
        {
            double.TryParse(txt_hours.Text, out double hours);
            double.TryParse(txt_distance.Text, out double distance);

            return new WorkDetail
            {
                category_code = txt_category.Text?.Trim() ?? "",
                project_name = txt_project_name.Text?.Trim() ?? "",
                detail = txt_detail.Text?.Trim() ?? "",
                note = txt_note.Text?.Trim() ?? "",
                hours = hours,
                software = string.IsNullOrWhiteSpace(txt_software.Text) ? "なし" : txt_software.Text.Trim(),
                software_cost = 0,  // Sprint 2 でマスタ参照による自動設定検討
                vehicle = string.IsNullOrWhiteSpace(txt_vehicle.Text) ? "なし" : txt_vehicle.Text.Trim(),
                transport = string.IsNullOrWhiteSpace(txt_transport.Text) ? "-" : txt_transport.Text.Trim(),
                from_location = string.IsNullOrWhiteSpace(txt_from_location.Text) ? "-" : txt_from_location.Text.Trim(),
                to_location = string.IsNullOrWhiteSpace(txt_to_location.Text) ? "-" : txt_to_location.Text.Trim(),
                distance = distance,
                sort_order = 0,  // 保存時に再設定
            };
        }

        /// <summary>フォームに何か入力がされているかをチェック</summary>
        private bool is_form_dirty()
        {
            return !string.IsNullOrWhiteSpace(txt_category.Text)
                || !string.IsNullOrWhiteSpace(txt_project_name.Text)
                || !string.IsNullOrWhiteSpace(txt_detail.Text)
                || !string.IsNullOrWhiteSpace(txt_note.Text)
                || (!string.IsNullOrWhiteSpace(txt_hours.Text) && txt_hours.Text != "0");
        }

        // ════════════════════════════════════════════════
        // ▼ 追加：v0.1.8 スタブボタン（Phase 2 / Sprint 2 で実装予定）
        // ════════════════════════════════════════════════

        /// <summary>
        /// 「区分リスト」ボタン
        /// ▼ 修正 v0.1.12 Phase 2：スタブを本実装に置き換え。
        ///   CategoryListDialog をモーダル表示し、ユーザーが選択した区分コードを
        ///   txt_category.Text にセット → load_category_detail_async() で
        ///   業務名・区分詳細を自動取得する。
        ///   キャンセル時は何もしない（フォームは現状維持）。
        /// </summary>
        private async void btn_open_category_list_Click(object sender, RoutedEventArgs e)
        {
            // CategoryListDialog をモーダル表示（Owner を this にして親子関係を明示）
            var dlg = new CategoryListDialog { Owner = this };
            bool? result = dlg.ShowDialog();

            // ユーザーがダブルクリック選択した場合のみ、区分を反映
            if (result == true
                && !string.IsNullOrWhiteSpace(dlg.selected_category_code))
            {
                txt_category.Text = dlg.selected_category_code;

                // ▼ 既存ロジック流用：projects.name / projects.detail を取得して
                //   業務名（txt_project_name）と区分詳細（txt_category_detail）に自動入力
                await load_category_detail_async();

                // 次の入力をスムーズにするため、内容欄にフォーカス移動
                txt_detail.Focus();
            }
            // キャンセル時はフォームに変更を加えない
        }

        /// <summary>「MAP」ボタン（Sprint 2 で実装）</summary>
        private void btn_map_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Google Maps 連携機能は Sprint 2 で実装予定でございます。",
                "未実装",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ════════════════════════════════════════════════
        // ▼ 追加：v0.1.8 戻る・終了ボタン（閉じる前確認）
        // ════════════════════════════════════════════════

        /// <summary>「← 戻る」ボタン</summary>
        private void btn_back_Click(object sender, RoutedEventArgs e)
        {
            if (show_close_confirmation())
            {
                DialogResult = false;
                Close();
            }
        }

        /// <summary>「終了」ボタン（赤・誤クリック警告色）</summary>
        private void btn_exit_Click(object sender, RoutedEventArgs e)
        {
            if (show_close_confirmation())
            {
                DialogResult = false;
                Close();
            }
        }

        /// <summary>
        /// 閉じる前の状態別確認ダイアログ
        /// 戻り値：true = 閉じてよい / false = キャンセル（開いたまま）
        ///
        /// 状態別の挙動：
        ///   A. 一覧空 + フォーム空 → 確認なしで閉じる
        ///   B. 一覧空 + フォーム入力中 → 「破棄して閉じる」確認
        ///   C. 一覧に項目あり → 「保存して閉じる / 破棄して閉じる / キャンセル」3択
        ///      Yes/No/Cancel のボタンキャプションは MessageBox 仕様により固定（はい/いいえ/キャンセル）
        ///      意味づけはメッセージ本文で説明
        /// </summary>
        private bool show_close_confirmation()
        {
            bool form_dirty = is_form_dirty();
            int list_count = _details.Count;

            // 状態 A：何もない
            if (list_count == 0 && !form_dirty)
            {
                return true;
            }

            // 状態 B：一覧空・フォームのみ入力あり
            if (list_count == 0 && form_dirty)
            {
                var result = MessageBox.Show(
                    "入力中のデータは破棄されます。\n閉じてもよろしいでしょうか？",
                    "閉じる前の確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                return result == MessageBoxResult.Yes;
            }

            // 状態 C：一覧に項目あり（未保存）
            var msg = $"追加済みの作業 {list_count} 件は保存されておりません。\n\n" +
                      $"[はい]　　　 保存して閉じる\n" +
                      $"[いいえ]　　 破棄して閉じる\n" +
                      $"[キャンセル] このまま続ける";

            var choice = MessageBox.Show(
                msg,
                "閉じる前の確認",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Yes)
            {
                // 保存して閉じる → btn_save_Click のロジックを呼ぶ
                // 保存成功時は DialogResult=true で Close される
                // 保存失敗時は開いたままにしたいので false 返却
                btn_save_Click(null!, null!);
                // btn_save_Click 内で Close() するため、ここに来る時点で表示中なら失敗
                return false;
            }
            else if (choice == MessageBoxResult.No)
            {
                // 破棄して閉じる
                return true;
            }
            else
            {
                // キャンセル（開いたまま）
                return false;
            }
        }

        // ════════════════════════════════════════════════
        // 保存処理（v0.1.6 ロジック温存・微修正）
        // ════════════════════════════════════════════════

        /// <summary>
        /// 「💾 保存」ボタン → 入力値を検証して DB に保存
        ///
        /// ▼ 修正：v0.1.8
        /// ・grid_details.CommitEdit() を削除（DataGrid 廃止のため）
        /// ・btn_cancel.IsEnabled → btn_back.IsEnabled / btn_exit.IsEnabled に変更
        /// ・フォームに編集途中のデータがあれば確認を促す
        /// </summary>
        private async void btn_save_Click(object sender, RoutedEventArgs e)
        {
            // ─── 入力検証 ───
            if (dp_report_date.SelectedDate == null)
            {
                MessageBox.Show("日付を選択してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(cmb_employee.Text))
            {
                MessageBox.Show("氏名を入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var ti = WorkTimeCalculator.parse_time(txt_clock_in.Text);
            var to = WorkTimeCalculator.parse_time(txt_clock_out.Text);
            if (ti == null || to == null)
            {
                MessageBox.Show("出勤・退勤時刻を HH:mm 形式で入力してください（例：08:30）。",
                    "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (to <= ti)
            {
                MessageBox.Show("退勤時刻は出勤時刻より後にしてください。",
                    "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 一覧に作業詳細が無い場合は警告
            if (_details.Count == 0)
            {
                // フォームに入力途中があれば、まずそれを追加するか案内
                if (is_form_dirty())
                {
                    MessageBox.Show(
                        "作業詳細が一覧に追加されておりません。\n" +
                        "「＋ この作業を追加」ボタンを押してから保存してください。",
                        "入力エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(
                        "作業詳細を1件以上追加してください。",
                        "入力エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return;
            }

            // ▼ 修正：v0.1.8 grid_details.CommitEdit() 削除（DataGrid 廃止のため）

            // ─── ▼ v0.1.6 既存親レコードの特定 ───
            int target_id = _editing_id;
            int existing_employee_id = _editing_report?.employee_id ?? 0;
            int existing_entered_by = _editing_report?.entered_by ?? 0;
            string existing_source_pc = _editing_report?.source_pc ?? "DESKTOP";
            string existing_gdrive_id = _editing_report?.gdrive_file_id ?? "";

            if (target_id == 0)
            {
                try
                {
                    using var conn = database_manager.create_connection();
                    var existing = await conn.QueryFirstOrDefaultAsync<DailyReport>(@"
                        SELECT * FROM daily_reports
                        WHERE report_date = @date AND employee_name = @name
                        LIMIT 1",
                        new
                        {
                            date = dp_report_date.SelectedDate.Value.ToString("yyyy-MM-dd"),
                            name = cmb_employee.Text.Trim()
                        });

                    if (existing != null)
                    {
                        target_id = existing.id;
                        existing_employee_id = existing.employee_id;
                        existing_entered_by = existing.entered_by;
                        existing_source_pc = existing.source_pc;
                        existing_gdrive_id = existing.gdrive_file_id;
                    }
                }
                catch (Exception ex_chk)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[InputDialog] 既存検索失敗（新規扱いで継続）: {ex_chk.Message}");
                }
            }

            // ─── DailyReport 構築 ───
            var calc = WorkTimeCalculator.calculate(ti.Value, to.Value);

            var report = new DailyReport
            {
                id = target_id,
                report_date = dp_report_date.SelectedDate.Value.ToString("yyyy-MM-dd"),
                employee_id = existing_employee_id,
                employee_name = cmb_employee.Text.Trim(),
                clock_in = txt_clock_in.Text.Trim(),
                clock_out = txt_clock_out.Text.Trim(),
                early_morning_min = calc.early_morning_min,
                overtime_min = calc.overtime_min,
                late_night_min = calc.late_night_min,
                start_location = cmb_start_location.Text?.Trim() ?? "本社",
                end_location = cmb_end_location.Text?.Trim() ?? "本社",
                entered_by = existing_entered_by,
                source_pc = existing_source_pc,
                source = "desktop",
                gdrive_file_id = existing_gdrive_id,
            };

            // ─── WorkDetail 一覧を整形 ───
            var detail_list = new List<WorkDetail>();
            int order = 0;
            foreach (var d in _details)
            {
                d.sort_order = order++;
                if (string.IsNullOrWhiteSpace(d.software)) d.software = "なし";
                if (string.IsNullOrWhiteSpace(d.vehicle)) d.vehicle = "なし";
                if (string.IsNullOrWhiteSpace(d.from_location)) d.from_location = "-";
                if (string.IsNullOrWhiteSpace(d.to_location)) d.to_location = "-";
                if (string.IsNullOrWhiteSpace(d.transport)) d.transport = "-";
                detail_list.Add(d);
            }

            // ─── DB 保存 ───
            try
            {
                btn_save.IsEnabled = false;
                // ▼ 修正：v0.1.8 btn_cancel → btn_back / btn_exit に変更
                btn_back.IsEnabled = false;
                btn_exit.IsEnabled = false;

                int saved_id = await DailyReportService.save_async(report, detail_list);

                MessageBox.Show(
                    target_id == 0
                        ? $"日報を登録いたしました。（ID: {saved_id}）"
                        : "日報を更新いたしました。",
                    "保存完了",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"日報の保存に失敗いたしました。\n\n{ex.Message}",
                    "保存エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                btn_save.IsEnabled = true;
                btn_back.IsEnabled = true;
                btn_exit.IsEnabled = true;
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dapper;
using EA_DailyReport.Data;
using EA_DailyReport.Models;
using EA_DailyReport.Services;

namespace EA_DailyReport.Views.Dialogs
{
    /// <summary>
    /// 日報入力ダイアログ（v0.1.6 大幅改修）
    /// 出退勤・氏名選択・作業行（複数）・自動算出（早朝・残業・深夜）に対応
    ///
    /// ▼ v0.1.6 追加機能：
    /// ・マスタ（氏名/区分/業務名/ソフト/車両/移動方法）を NAS取得済みローカルDBから読込
    /// ・DataGrid セルにオートコンプリート（AutoCompleteTextBox）
    /// ・氏名のデフォルトに UserSession のユーザー名を自動入力
    /// ・編集モード時は出退勤・場所をロック表示・「🔓 ロック解除」ボタンで解除
    /// ・新規モードで「同じ日 × 同じ人」の既存日報があれば自動的にロックモードへ
    ///
    /// コンストラクタの daily_report_id：
    ///   0  → 新規入力モード
    ///   >0 → 既存日報の編集モード
    /// </summary>
    public partial class InputDialog : Window, INotifyPropertyChanged
    {
        private readonly int _editing_id;
        private DailyReport? _editing_report;

        // DataGrid のバインドソース（ObservableCollection で行追加・削除を即時反映）
        private ObservableCollection<WorkDetail> _details = new();

        // ▼ v0.1.6 内部状態
        // 「同じ日 × 同じ人」で既存日報が見つかったときに自動セットされる
        // ロック中は出退勤・場所の編集を抑止する
        private bool _is_locked = false;

        // ────────────────────────────────────────────────
        // ▼ v0.1.6 マスタリスト（DataGrid セルの AutoCompleteTextBox にバインド）
        // public プロパティ + INotifyPropertyChanged で XAML から ElementName 経由で参照される
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

        // ────────────────────────────────────────────────
        // コンストラクタ
        // ────────────────────────────────────────────────

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

            grid_details.ItemsSource = _details;
            Loaded += on_loaded;
        }

        // ────────────────────────────────────────────────
        // 初期化
        // ────────────────────────────────────────────────

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
            }

            // 初回の自動算出
            recalculate();
        }

        /// <summary>
        /// ▼ 追加：v0.1.6
        /// マスタリスト（区分・業務名・ソフト・車両）を ローカルDB から読み込む
        /// AutoCompleteTextBox のバインドソースになる
        ///
        /// データソース：
        ///   ・区分・業務名 → projects テーブル（NAS から取得済み・78件）
        ///   ・ソフト・機材 → equipment_rates テーブル（NAS から取得済み・13件）
        ///   ・車両         → vehicles テーブル（NAS から取得済み・4件）
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

                // 業務名（projects.site_name + project_name 結合）
                // CostManager のスキーマ：site_name = "鳥居水門" など
                var sites = (await conn.QueryAsync<string>(@"
                    SELECT DISTINCT site_name FROM projects
                    WHERE is_active = 1 AND site_name IS NOT NULL AND site_name <> ''
                    ORDER BY site_name
                ")).ToList();
                project_name_list = sites;

                // ソフト・機材（equipment_rates.equipment_name）
                // テーブルが無い環境もあるため try-catch で吸収
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
                catch { /* テーブル無しは無視（「なし」だけ表示） */ }
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

                // 移動方法は固定リスト（既に初期化済み）
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
        // 新規入力時の初期化
        // ────────────────────────────────────────────────

        private void init_for_new_input()
        {
            dp_report_date.SelectedDate = DateTime.Today;
            txt_clock_in.Text = "08:30";
            txt_clock_out.Text = "17:30";
            cmb_start_location.Text = "本社";
            cmb_end_location.Text = "本社";

            // ▼ 追加：v0.1.6 氏名のデフォルト自動入力
            // App.xaml.cs の起動フローで UserSession.set(...) されている想定
            // 取得失敗時は空のまま（後でユーザーが手入力）
            try_set_default_employee_name();

            // 空の作業詳細1行をデフォルトで追加
            _details.Add(new WorkDetail
            {
                category_code = "",
                project_name = "",
                detail = "",
                hours = 0,
                software = "なし",
                software_cost = 0,
                vehicle = "なし",
                from_location = "-",
                to_location = "-",
                distance = 0,
                transport = "-",
                note = "",
                sort_order = 0,
            });
        }

        /// <summary>
        /// ▼ 修正：v0.1.7
        /// 氏名のデフォルトをアクセス中ユーザーから取得して設定する
        /// UserSession.user_name を直接呼び出し（v0.1.6 のリフレクションは削除）
        /// 失敗時は空のまま（ユーザーが手入力）
        /// </summary>
        private void try_set_default_employee_name()
        {
            try
            {
                // UserSession は EA_DailyReport プロジェクト内に存在する想定
                // App.xaml.cs の起動フローで UserSession.set(...) されている
                if (UserSession.is_logged_in
                 && !string.IsNullOrWhiteSpace(UserSession.user_name))
                {
                    cmb_employee.Text = UserSession.user_name;
                }
            }
            catch
            {
                // UserSession 未初期化等は無視（ユーザーが手入力で対応）
            }
        }

        // ────────────────────────────────────────────────
        // 既存日報の読込（編集モード）
        // ────────────────────────────────────────────────

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

                // work_details 取得（論理削除済みは除外）
                var details = (await conn.QueryAsync<WorkDetail>(
                    "SELECT * FROM work_details WHERE daily_report_id = @id AND is_deleted = 0 ORDER BY sort_order, id",
                    new { id = _editing_id })).ToList();

                _details.Clear();
                foreach (var d in details) _details.Add(d);

                // 1行も無ければ空行を追加
                if (_details.Count == 0)
                    _details.Add(new WorkDetail { software = "なし", vehicle = "なし", from_location = "-", to_location = "-", transport = "-" });
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
        // ▼ 追加：v0.1.6 ロック / アンロック制御（VBA 同等）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 出退勤・場所コントロールを「ロック」状態にする
        /// VBA の LockTimeFields() に相当
        /// グレー背景 + IsReadOnly=true / IsEnabled=false で「触れない感」を出す
        /// </summary>
        /// <param name="show_warning">既存日報警告バッジを表示するか</param>
        private void lock_time_fields(bool show_warning)
        {
            _is_locked = true;

            // 出退勤 TextBox：IsReadOnly + 灰色背景
            var locked_bg = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));
            txt_clock_in.IsReadOnly = true;
            txt_clock_in.Background = locked_bg;
            txt_clock_out.IsReadOnly = true;
            txt_clock_out.Background = locked_bg;

            // 場所 ComboBox：IsEnabled=false で完全に触れなくする
            cmb_start_location.IsEnabled = false;
            cmb_end_location.IsEnabled = false;

            // 「🔓 ロック解除」ボタンを表示
            btn_unlock.Visibility = Visibility.Visible;

            // 「既存日報あり」バッジ表示
            if (show_warning)
                border_existing_warning.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 出退勤・場所コントロールを「編集可能」状態に戻す
        /// VBA の NotLockTimeFields() に相当
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
            // 警告バッジは表示したまま（「既存日報を編集中」の事実は変わらないため）
        }

        /// <summary>
        /// 「🔓 ロック解除」ボタンハンドラ
        /// 確認ダイアログで意思確認してからロック解除する
        /// 解除後の保存時、出退勤更新は親レコードに対して1セットのみ反映される
        /// （構造的に「2つの時間が存在する」事態は発生しない）
        /// </summary>
        private void btn_unlock_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "出退勤と場所を編集可能にします。\n\n" +
                "保存すると、この日のあなたの出退勤情報（全作業行に共通）が更新されます。\n" +
                "本当によろしいですか？",
                "ロック解除の確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.OK)
            {
                unlock_time_fields();
            }
        }

        // ────────────────────────────────────────────────
        // ▼ 追加：v0.1.6 重複チェック（新規モード時の自動ロック）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 日付・氏名コンボの変更時に呼ばれる
        /// 新規モードのみ動作（編集モードは初期状態で固定ロック）
        /// </summary>
        private async void report_date_or_name_changed(object sender, RoutedEventArgs e)
        {
            // 編集モード or ロード前は何もしない
            if (_editing_id > 0) return;
            if (!IsLoaded) return;

            await check_existing_and_lock_async();
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
                    // 既存値を表示してロック
                    txt_clock_in.Text = existing.clock_in ?? "";
                    txt_clock_out.Text = existing.clock_out ?? "";
                    cmb_start_location.Text = existing.start_location ?? "本社";
                    cmb_end_location.Text = existing.end_location ?? "本社";
                    lock_time_fields(show_warning: true);
                    recalculate();
                }
                else
                {
                    // 既存なし → ロック解除
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
        // 出退勤の自動算出（早朝・残業・深夜）
        // ────────────────────────────────────────────────

        /// <summary>
        /// 出勤・退勤の TextBox 変更時に呼ばれる
        /// 自動算出ラベル（早朝・残業・深夜）を即時更新する
        /// </summary>
        private void time_or_settings_changed(object sender, TextChangedEventArgs e)
        {
            recalculate();
        }

        private void recalculate()
        {
            // ▼ 修正：v0.1.6 バグ対応
            // WPF の InitializeComponent() 中、XAML で Text="08:30" を持つ TextBox が
            // 生成された瞬間に TextChanged イベントが発火する。
            // その時点ではまだ後続の TextBox（txt_clock_out 等）が null のため、
            // 参照すると NullReferenceException が発生する。
            // → null の場合は何もせず即 return する
            //   （on_loaded での明示的な recalculate() 呼び出しで再計算されるため問題なし）
            if (txt_clock_in == null || txt_clock_out == null) return;
            if (txt_calc_early == null
                || txt_calc_overtime == null
                || txt_calc_late_night == null) return;

            var ti = WorkTimeCalculator.parse_time(txt_clock_in.Text);
            var to = WorkTimeCalculator.parse_time(txt_clock_out.Text);

            if (ti == null || to == null)
            {
                // 不正な時刻フォーマット → 0:00 表示
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

        // ────────────────────────────────────────────────
        // 作業詳細の行追加・削除
        // ────────────────────────────────────────────────

        /// <summary>「+ 行追加」ボタン → 末尾に空行を追加</summary>
        private void btn_add_row_Click(object sender, RoutedEventArgs e)
        {
            _details.Add(new WorkDetail
            {
                software = "なし",
                vehicle = "なし",
                from_location = "-",
                to_location = "-",
                transport = "-",
                sort_order = _details.Count,
            });
        }

        /// <summary>「- 選択行削除」ボタン → DataGrid 選択行を削除</summary>
        private void btn_remove_row_Click(object sender, RoutedEventArgs e)
        {
            if (grid_details.SelectedItem is WorkDetail d)
            {
                _details.Remove(d);
            }
            else
            {
                MessageBox.Show(
                    "削除する行を選択してください。",
                    "選択なし",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        // ────────────────────────────────────────────────
        // 保存・キャンセル
        // ────────────────────────────────────────────────

        /// <summary>
        /// 「💾 保存」ボタン → 入力値を検証して DB に保存
        /// ▼ v0.1.6 ロック中の場合の処理：
        ///   ・新規モードで既存ロック中 → 既存親レコードを取得して daily_report_id をセット
        ///     → save_async は UPDATE モードで実行され、出退勤は1セットのみ維持される
        ///   ・編集モードでロック中 → ロック解除されてないので出退勤は変わらない（DBの値そのまま）
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

            // 1行も作業詳細が無い場合は警告
            if (_details.Count == 0)
            {
                MessageBox.Show("作業詳細を1行以上入力してください。",
                    "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // DataGrid のコミット（編集中セルが未確定なら確定させる）
            grid_details.CommitEdit(DataGridEditingUnit.Cell, true);
            grid_details.CommitEdit(DataGridEditingUnit.Row, true);

            // ─── ▼ v0.1.6 既存親レコードの特定 ───
            // 新規モードでも、同じ日 × 同じ人で既存日報があれば、
            // その親 daily_reports.id を使って UPDATE モードで保存する
            // → これで「2つの時間が存在する」事態を構造的に防止する
            int target_id = _editing_id;
            int existing_employee_id = _editing_report?.employee_id ?? 0;
            int existing_entered_by = _editing_report?.entered_by ?? 0;
            string existing_source_pc = _editing_report?.source_pc ?? "DESKTOP";
            string existing_gdrive_id = _editing_report?.gdrive_file_id ?? "";

            if (target_id == 0)
            {
                // 新規モード → 既存親レコードを検索
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
                        // 既存あり → UPDATE モードに切り替え
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
                id = target_id,  // 0 なら新規・>0 なら更新
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
                // 空白埋め
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
                btn_cancel.IsEnabled = false;

                int saved_id = await DailyReportService.save_async(report, detail_list);

                MessageBox.Show(
                    target_id == 0
                        ? $"日報を登録しました。（ID: {saved_id}）"
                        : "日報を更新しました。",
                    "保存完了",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"日報の保存に失敗しました。\n\n{ex.Message}",
                    "保存エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                btn_save.IsEnabled = true;
                btn_cancel.IsEnabled = true;
            }
        }

        /// <summary>「キャンセル」ボタン</summary>
        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;           // ▼▼▼ 追加：ICollectionView / CollectionViewSource ▼▼▼

namespace EA_CostManager.ViewModels
{
    /// <summary>
    /// 絞り込みタブ1つ分のViewModel
    /// 親の project_cost_view_model に紐づく
    /// </summary>
    public class filter_tab_view_model : base_view_model
    {
        // ---- タブ情報 ----
        public int filter_tab_id { get; }
        public int project_id { get; }
        public string tab_name { get; }
        public string filter_month { get; }
        public string filter_content { get; }
        public string filter_match { get; }
        public string filter_names { get; }
        public bool is_single_mode { get; }

        // ▼▼▼ 追加(B)：原価集計モード（"daily"/"task"）。タブごとに保持し、その場切替で更新する ▼▼▼
        private string _agg_mode = "daily";
        public string agg_mode
        {
            get => _agg_mode;
            set
            {
                if (SetProperty(ref _agg_mode, value))
                {
                    OnPropertyChanged(nameof(is_task_mode));
                    OnPropertyChanged(nameof(agg_mode_label));
                }
            }
        }
        /// <summary>task モードか（UIトグルのバインド用）</summary>
        public bool is_task_mode => _agg_mode == "task";
        /// <summary>UI表示用ラベル</summary>
        public string agg_mode_label => _agg_mode == "task" ? "業務単位集計（担当者別）" : "日単位集計";

        public string filter_date_from { get; } = "";
        public string filter_date_to { get; } = "";
        public bool use_custom_rates { get; }

        // ---- 集計レコード（表示用：month_header / normal / subtotal を混在させたコレクション）----
        private ObservableCollection<cost_row_item> _cost_records = new();
        public ObservableCollection<cost_row_item> cost_records
        {
            get => _cost_records;
            private set
            {
                if (SetProperty(ref _cost_records, value))
                {
                    notify_totals();
                    // ▼▼▼ 追加：新データロード時にComboBoxフィルターをリセット ▼▼▼
                    _col_filter_eng = "（すべて）";
                    _col_filter_ast = "（すべて）";
                    _col_filter_work = "";
                    _search_text = "";
                    OnPropertyChanged(nameof(col_filter_eng));
                    OnPropertyChanged(nameof(col_filter_ast));
                    OnPropertyChanged(nameof(col_filter_work));
                    OnPropertyChanged(nameof(search_text));
                    rebuild_filtered_view(); // ▼▼▼ 追加：ICollectionViewを再構築 ▼▼▼
                }
            }
        }

        // ---- 生データ（合計計算用・表示には使わない）----
        private List<cost_record> _raw_records = new();
        public IReadOnlyList<cost_record> raw_records => _raw_records;

        // ---- 合計（_raw_records から計算）----
        public decimal total_engineer_cost => _raw_records.Sum(r => r.engineer_cost);
        public decimal total_assistant_cost => _raw_records.Sum(r => r.assistant_cost);
        public decimal total_transport_cost => _raw_records.Sum(r => r.transport_cost);
        public decimal total_equipment_cost => _raw_records.Sum(r => r.equipment_cost);
        public decimal grand_total => _raw_records.Sum(r => r.total_cost);

        public string display_total_engineer_cost => total_engineer_cost.ToString("#,##0");
        public string display_total_assistant_cost => total_assistant_cost.ToString("#,##0");
        public string display_total_transport_cost => total_transport_cost.ToString("#,##0");
        public string display_total_equipment_cost => total_equipment_cost.ToString("#,##0");
        public string display_grand_total => grand_total.ToString("#,##0");

        private string _info_tooltip = "";
        public string info_tooltip
        {
            get => _info_tooltip;
            private set => SetProperty(ref _info_tooltip, value);
        }

        private bool _is_loading;
        public bool is_loading
        {
            get => _is_loading;
            set => SetProperty(ref _is_loading, value);
        }

        private bool _is_empty_result;
        public bool is_empty_result
        {
            get => _is_empty_result;
            private set => SetProperty(ref _is_empty_result, value);
        }

        // ================================================================
        // ▼▼▼ 追加：DataGridフィルター機能（Sprint 4）▼▼▼
        // ICollectionView で DataGrid の表示行を制御する
        // 折り畳み（is_hidden）・グローバル検索・列別フィルターを統合管理
        // ================================================================

        // ---- ICollectionView（DataGridのItemsSourceとして使用）----
        private ICollectionView? _filtered_view;
        public ICollectionView? filtered_records => _filtered_view;

        // ---- グローバル検索テキスト（作業内容・技師氏名・助手氏名 を OR 検索）----
        private string _search_text = "";
        public string search_text
        {
            get => _search_text;
            set { if (SetProperty(ref _search_text, value)) update_search_filter(); }
        }

        // ---- 列別フィルターテキスト（AND条件）----
        // DataGrid の列ヘッダーの TextBox にバインドする
        private string _col_filter_work = "";
        public string col_filter_work
        {
            get => _col_filter_work;
            set { if (SetProperty(ref _col_filter_work, value)) update_search_filter(); }
        }

        private string _col_filter_eng = "（すべて）";
        public string col_filter_eng
        {
            get => _col_filter_eng;
            set { if (SetProperty(ref _col_filter_eng, value)) update_search_filter(); }
        }

        private string _col_filter_ast = "（すべて）";
        public string col_filter_ast
        {
            get => _col_filter_ast;
            set { if (SetProperty(ref _col_filter_ast, value)) update_search_filter(); }
        }

        // ---- いずれかのフィルターが有効か ----
        // true：検索テキストで表示制御（折り畳み状態を無視）
        // false：is_hidden で折り畳み制御
        // ComboBox は「（すべて）」が「フィルターなし」を意味する
        private bool has_any_search_filter =>
            !string.IsNullOrWhiteSpace(_search_text) ||
            !string.IsNullOrWhiteSpace(_col_filter_work) ||
            (_col_filter_eng != "（すべて）" && !string.IsNullOrEmpty(_col_filter_eng)) ||
            (_col_filter_ast != "（すべて）" && !string.IsNullOrEmpty(_col_filter_ast));

        // ---- ComboBox用：一意の氏名リスト ----
        // 「（すべて）」を先頭に追加し、技師氏名・助手氏名を「・」で分割して収集する
        public List<string> distinct_eng_names =>
            new List<string> { "（すべて）" }
            .Concat(
                _cost_records
                    .Where(r => r.is_normal)
                    .SelectMany(r => (r.engineer_names ?? "")
                        .Split('・', StringSplitOptions.RemoveEmptyEntries))
                    .Select(n => n.Trim())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .OrderBy(n => n)
            )
            .ToList();

        public List<string> distinct_ast_names =>
            new List<string> { "（すべて）" }
            .Concat(
                _cost_records
                    .Where(r => r.is_normal)
                    .SelectMany(r => (r.assistant_names ?? "")
                        .Split('・', StringSplitOptions.RemoveEmptyEntries))
                    .Select(n => n.Trim())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .OrderBy(n => n)
            )
            .ToList();

        // ---- ヘッダー・小計行の表示制御用：一致する通常行がある月度セット ----
        // update_search_filter() で事前計算 → filter_row() が参照する
        private HashSet<string> _matching_months = new();

        // ▼▼▼ 追加：年折りたたみ状態管理 ▼▼▼
        // key=年キー（例："2025"）/ value=折りたたみ状態
        private Dictionary<string, bool> _year_collapsed = new();
        // 検索フィルターあり時に表示すべき年セット
        private HashSet<string> _matching_years = new();

        // ================================================================

        // ---- コンストラクタ ----
        public filter_tab_view_model(cost_filter_tab model)
        {
            filter_tab_id = model.id;
            project_id = model.project_id;
            tab_name = model.tab_name;
            filter_month = model.filter_month;
            filter_content = model.filter_content;
            filter_match = model.filter_match;
            filter_names = model.filter_names;
            is_single_mode = model.is_single_mode == 1;
            filter_date_from = model.filter_date_from;
            filter_date_to = model.filter_date_to;
            use_custom_rates = model.use_custom_rates == 1;
            // ▼▼▼ 追加(B)：集計モードを受け取る（空なら daily） ▼▼▼
            _agg_mode = string.IsNullOrWhiteSpace(model.agg_mode) ? "daily" : model.agg_mode;
        }

        // ---- データロード ----
        /// <summary>
        /// cost_recordsをDBからロードする
        /// ▼▼▼ [Sprint 5E] child_codes引数追加：親コード＋子コードを合算してロード ▼▼▼
        /// </summary>
        public async Task load_records_async(string category_code,
            IEnumerable<string>? child_codes = null)
        {
            is_loading = true;
            // 親コード＋子コードをまとめた検索対象コード一覧
            var all_codes = new[] { category_code }
                .Concat(child_codes ?? Enumerable.Empty<string>())
                .Distinct()
                .ToArray();

            try
            {
                // ▼▼▼ 追加(B)：1業務ごと(1人ずつ)モード（最優先で分岐） ▼▼▼
                if (agg_mode == "task")
                {
                    await load_task_mode_async(category_code, all_codes);
                    return;
                }

                if (is_single_mode && !string.IsNullOrWhiteSpace(filter_names))
                {
                    await load_single_mode_async(category_code, all_codes);
                    return;
                }

                if (use_custom_rates)
                {
                    await load_custom_rate_mode_async(category_code, all_codes);
                    return;
                }

                using var conn = database_manager.create_connection();

                // ▼▼▼ [Sprint 5E] IN句で親＋子コードを一括取得 ▼▼▼
                var sql = "SELECT * FROM cost_records WHERE category_code IN @codes";
                var param = new DynamicParameters();
                param.Add("codes", all_codes);

                if (!string.IsNullOrWhiteSpace(filter_month))
                {
                    sql += " AND fiscal_month = @month";
                    param.Add("month", filter_month);
                }

                sql += " ORDER BY record_date";

                var rows = (await conn.QueryAsync<cost_record>(sql, param)).ToList();

                // 表示上は全て親コードに統一
                foreach (var r in rows) r.category_code = category_code;

                if (!string.IsNullOrWhiteSpace(filter_date_from)
                    && !string.IsNullOrWhiteSpace(filter_date_to))
                {
                    rows = rows.Where(r =>
                        string.Compare(r.record_date, filter_date_from) >= 0 &&
                        string.Compare(r.record_date, filter_date_to) <= 0
                    ).ToList();
                }

                rows = apply_filters(rows);

                _raw_records = rows;
                is_empty_result = rows.Count == 0;
                cost_records = build_display_rows(rows);

                // ▼▼▼ 追加：DB保存済みの折りたたみ状態を復元 ▼▼▼
                await restore_collapse_states_async();
            }
            finally
            {
                is_loading = false;
                await build_tooltip_async();
            }
        }

        // ---- 1業務ごと(1人ずつ)モード（B：daily_reports から非破壊で都度集計）----
        // 粒度Y：日付 × 作業者 × 業務内容 で1行。1人が1日に3業務なら3行に割れる。
        // 交通費・損料も daily_report 単位で紐づくため、その業務行に正しく配分される。
        // ※ 損料は「同一機材を6時間以上使用」で計上。1人1業務単位だと6時間未満になり計上されない場合がある（仕様）。
        private async Task load_task_mode_async(string category_code, string[] all_codes)
        {
            using var conn = database_manager.create_connection();

            var settings = await conn.QueryAsync<app_setting>("SELECT * FROM app_settings");
            var setting_dict = settings.ToDictionary(s => s.key, s => s.value);

            decimal engineer_rate = get_rate(setting_dict, "engineer_daily_rate", 34800m);
            decimal assistant_rate = get_rate(setting_dict, "assistant_daily_rate", 28000m);
            double base_hours = (double)get_rate(setting_dict, "base_hours_per_day", 8m);
            if (base_hours <= 0) base_hours = 8; // ゼロ除算ガード
            decimal road_rate = get_rate(setting_dict, "road_cost_per_km", 50m);
            decimal highway_rate = get_rate(setting_dict, "highway_cost_per_km", 100m);

            // 機材日額マスタ（旧形式日報の daily_rate=0 を補完）
            var equip_rate_map = (await conn.QueryAsync<(string name, decimal rate)>(
                "SELECT equipment_name, daily_rate FROM equipment_rates WHERE is_active = 1"))
                .ToDictionary(r => r.name, r => r.rate, StringComparer.OrdinalIgnoreCase);

            // 登録車両（台数カウント対象）
            var registered_vehicles = (await conn.QueryAsync<string>(
                "SELECT vehicle_name FROM vehicles WHERE is_active = 1")).ToHashSet();

            // 対象日報（id付き・有給/有休除外）
            var reports = (await conn.QueryAsync<dynamic>(@"
                SELECT dr.id, dr.report_date, dr.employee_name, dr.job_type,
                       dr.category_code, dr.detail, dr.hours
                FROM daily_reports dr
                WHERE dr.category_code IN @codes
                  AND dr.category_code NOT IN ('有給','有休')
                ORDER BY dr.report_date",
                new { codes = all_codes })).ToList();

            // job_type が空/不明の場合に employees から補完するための辞書
            var emp_job_map = (await conn.QueryAsync<(string name, string job)>(
                "SELECT employee_name, job_type FROM employees WHERE job_type != '' AND job_type != '不明'"))
                .ToDictionary(e => e.name, e => e.job);

            var report_ids = reports.Select(r => (int)System.Convert.ToInt32(r.id)).ToList();   // ▼修正(B)：(int)で静的型化しCS8197回避

            // 機材・交通を daily_report_id で引けるよう辞書化
            var equip_map = new Dictionary<int, List<(string name, decimal rate, double hours)>>();
            var trans_map = new Dictionary<int, List<(double dist, string vehicle, string method)>>();
            if (report_ids.Count > 0)
            {
                var eq = (await conn.QueryAsync<dynamic>(@"
                    SELECT de.daily_report_id, de.equipment_name, de.daily_rate, dr.hours
                    FROM daily_equipment de
                    JOIN daily_reports dr ON de.daily_report_id = dr.id
                    WHERE de.daily_report_id IN @ids",
                    new { ids = report_ids })).ToList();
                foreach (var e in eq)
                {
                    int rid = System.Convert.ToInt32(e.daily_report_id);
                    if (!equip_map.TryGetValue(rid, out var list)) { list = new(); equip_map[rid] = list; }
                    list.Add(((string?)e.equipment_name ?? "",
                              System.Convert.ToDecimal(e.daily_rate ?? 0),
                              System.Convert.ToDouble(e.hours ?? 0)));
                }

                var tr = (await conn.QueryAsync<dynamic>(@"
                    SELECT daily_report_id, distance, vehicle, travel_method
                    FROM daily_transport
                    WHERE daily_report_id IN @ids",
                    new { ids = report_ids })).ToList();
                foreach (var t in tr)
                {
                    int rid = System.Convert.ToInt32(t.daily_report_id);
                    if (!trans_map.TryGetValue(rid, out var list)) { list = new(); trans_map[rid] = list; }
                    list.Add((System.Convert.ToDouble(t.distance ?? 0),
                              (string?)t.vehicle ?? "",
                              (string?)t.travel_method ?? ""));
                }
            }

            // 粒度Y：日付 × 作業者 × 業務内容 でグループ化
            var grouped = reports
                .GroupBy(r => (
                    date: (string)r.report_date,
                    emp: (string)(r.employee_name ?? ""),
                    task: ((string?)r.detail ?? "").Replace("、", "・").Trim()))
                .ToList();

            var result = new List<cost_record>();

            foreach (var g in grouped)
            {
                var date = DateTime.Parse(g.Key.date);

                // 職種判定（空/不明は employees から補完）
                string job = "";
                foreach (var r in g)
                {
                    string jt = (string?)r.job_type ?? "";
                    if (string.IsNullOrWhiteSpace(jt) || jt == "不明")
                        emp_job_map.TryGetValue(g.Key.emp, out jt);
                    if (!string.IsNullOrWhiteSpace(jt)) { job = jt; break; }
                }
                bool is_eng = job.Contains("技師");
                bool is_ast = job.Contains("助手");

                // ▼修正(B)：g(dynamic要素)への .Sum は動的ディスパッチで型を誤選択するため、ループで明示加算
                double total_h = 0;
                foreach (var rr in g) total_h += (double)System.Convert.ToDouble(rr.hours ?? 0);
                decimal days = base_hours > 0 ? (decimal)(total_h / base_hours) : 0m;
                days = Math.Round(days, 4);

                decimal eng_cost = is_eng ? Math.Round(days * engineer_rate, 0) : 0m;
                decimal ast_cost = is_ast ? Math.Round(days * assistant_rate, 0) : 0m;

                var ids = g.Select(r => (int)System.Convert.ToInt32(r.id)).ToList();   // ▼修正(B)：(int)で静的型化しCS8197回避

                // 機材（6時間以上で計上・このグループの日報分のみ）
                decimal equ_c = 0m; int equ_qty = 0;
                var equ_names_parts = new List<string>();
                var equ_detail_parts = new List<string>();
                var equ_by_name = ids
                    .SelectMany(id => equip_map.TryGetValue(id, out var l)
                        ? l : Enumerable.Empty<(string name, decimal rate, double hours)>())
                    .Where(e => !string.IsNullOrWhiteSpace(e.name) && e.name != "なし")
                    .GroupBy(e => e.name);
                foreach (var eg in equ_by_name)
                {
                    double h = eg.Sum(x => x.hours);
                    if (h < 6.0) continue;
                    decimal rate = eg.First().rate;
                    if (rate == 0 && equip_rate_map.TryGetValue(eg.Key, out decimal mr)) rate = mr;
                    equ_c += rate;
                    equ_qty++;
                    equ_names_parts.Add(eg.Key);
                    equ_detail_parts.Add($"{eg.Key}（{g.Key.emp} {h:0.0}h）");
                }

                // 交通（往復×単価／高速でも往復250km超は下道単価／「なし」除外）
                decimal tra_c = 0m; double dist_total = 0;
                var veh_set = new HashSet<string>();
                foreach (var id in ids)
                {
                    if (!trans_map.TryGetValue(id, out var tlist)) continue;
                    foreach (var t in tlist)
                    {
                        if (string.IsNullOrWhiteSpace(t.vehicle) || t.vehicle == "なし") continue;
                        double round = t.dist * 2;
                        decimal unit = (t.method == "高速" && round <= 250) ? highway_rate : road_rate;
                        tra_c += (decimal)round * unit;
                        dist_total += round;
                        if (registered_vehicles.Contains(t.vehicle)) veh_set.Add(t.vehicle);
                    }
                }
                tra_c = Math.Round(tra_c, 0);
                int veh_count = Math.Min(veh_set.Count, 2);

                result.Add(new cost_record
                {
                    category_code = category_code,
                    record_date = g.Key.date,
                    fiscal_month = $"{date.Year}年{date.Month}月度",
                    work_content = g.Key.task,
                    engineer_names = is_eng ? g.Key.emp : "",
                    engineer_hours = is_eng ? total_h : 0,
                    engineer_days = is_eng ? (double)days : 0,
                    engineer_cost = eng_cost,
                    engineer_count = is_eng ? 1 : 0,
                    assistant_names = is_ast ? g.Key.emp : "",
                    assistant_hours = is_ast ? total_h : 0,
                    assistant_days = is_ast ? (double)days : 0,
                    assistant_cost = ast_cost,
                    assistant_count = is_ast ? 1 : 0,
                    personnel_cost = eng_cost + ast_cost,
                    transport_cost = tra_c,
                    distance_total = Math.Round(dist_total, 1),
                    vehicle_count = veh_count,
                    equipment_names = string.Join("・", equ_names_parts),
                    equipment_detail = string.Join("　", equ_detail_parts),
                    equipment_cost = Math.Round(equ_c, 0),
                    equipment_quantity = equ_qty,
                    total_cost = Math.Round(eng_cost + ast_cost + tra_c + equ_c, 0),
                });
            }

            // 既存の絞り込み（月度・期間・列フィルタ等）を適用
            if (!string.IsNullOrWhiteSpace(filter_month))
                result = result.Where(r => r.fiscal_month == filter_month).ToList();
            if (!string.IsNullOrWhiteSpace(filter_date_from) && !string.IsNullOrWhiteSpace(filter_date_to))
                result = result.Where(r =>
                    string.Compare(r.record_date, filter_date_from) >= 0 &&
                    string.Compare(r.record_date, filter_date_to) <= 0).ToList();

            result = apply_filters(result);
            result = result.OrderBy(r => r.record_date).ToList();

            _raw_records = result;
            is_empty_result = result.Count == 0;
            cost_records = build_display_rows(result);

            await restore_collapse_states_async();
        }

        // ---- 個人別集計モード（daily_reports から直接集計） ----
        private async Task load_single_mode_async(string category_code, string[] all_codes)
        {
            var target_names = filter_names
                .Split(',')
                .Select(n => n.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            if (target_names.Count == 0)
            {
                _raw_records = new List<cost_record>();
                is_empty_result = true;
                cost_records = new ObservableCollection<cost_row_item>();
                return;
            }

            using var conn = database_manager.create_connection();

            var settings = await conn.QueryAsync<app_setting>("SELECT * FROM app_settings");
            var setting_dict = settings.ToDictionary(s => s.key, s => s.value);

            decimal engineer_rate = get_rate(setting_dict, "engineer_daily_rate", 34800m);
            decimal assistant_rate = get_rate(setting_dict, "assistant_daily_rate", 28000m);
            decimal base_hours = get_rate(setting_dict, "base_hours_per_day", 8m);

            var name_placeholders = string.Join(",", target_names.Select((_, i) => $"@n{i}"));
            // ▼▼▼ [Sprint 5E] IN句で親＋子コードを対象に ▼▼▼
            var sql = $@"
                SELECT dr.report_date, dr.employee_name, dr.job_type, dr.hours,
                       dr.category_code, dr.detail,
                       de.equipment_name, de.daily_rate AS equip_daily_rate
                FROM daily_reports dr
                LEFT JOIN daily_equipment de ON de.daily_report_id = dr.id
                WHERE dr.category_code IN @codes
                  AND dr.employee_name IN ({name_placeholders})
                  AND dr.category_code NOT IN ('有給','有休')
                ORDER BY dr.report_date";

            var dp = new DynamicParameters();
            dp.Add("codes", all_codes);
            for (int i = 0; i < target_names.Count; i++)
                dp.Add($"n{i}", target_names[i]);

            var report_rows = (await conn.QueryAsync<dynamic>(sql, dp)).ToList();

            var grouped = report_rows
                .GroupBy(r => (date: (string)r.report_date, cat: (string)r.category_code))
                .ToList();

            var result = new List<cost_record>();

            foreach (var g in grouped)
            {
                var date = DateTime.Parse(g.Key.date);
                var engineers = g.Where(r => ((string?)r.job_type ?? "").Contains("技師")).ToList();
                var assistants = g.Where(r => ((string?)r.job_type ?? "").Contains("助手")).ToList();

                decimal eng_hours_dec = engineers.Sum(r => (decimal)(r.hours ?? 0));
                decimal ast_hours_dec = assistants.Sum(r => (decimal)(r.hours ?? 0));
                decimal eng_days_dec = base_hours > 0 ? eng_hours_dec / base_hours : 0m;
                decimal ast_days_dec = base_hours > 0 ? ast_hours_dec / base_hours : 0m;
                decimal eng_cost = eng_days_dec * engineer_rate;
                decimal ast_cost = ast_days_dec * assistant_rate;

                decimal equip_cost = 0m;
                var equip_groups = g
                    .Where(r => !string.IsNullOrWhiteSpace((string?)r.equipment_name))
                    .GroupBy(r => (string)r.equipment_name);

                foreach (var eq in equip_groups)
                {
                    decimal total_h = g
                        .Where(r => (string?)r.equipment_name == eq.Key)
                        .Sum(r => (decimal)(r.hours ?? 0));
                    if (total_h >= 6m)
                        equip_cost += (decimal)(eq.First().equip_daily_rate ?? 0);
                }

                var rec = new cost_record
                {
                    category_code = g.Key.cat,
                    record_date = g.Key.date,
                    fiscal_month = $"{date.Year}年{date.Month}月度",
                    work_content = string.Join("・", g
                        .Select(r => (string?)r.detail ?? "")
                        .Where(d => !string.IsNullOrWhiteSpace(d))
                        .Distinct()),
                    engineer_names = string.Join("・", engineers.Select(r => (string)r.employee_name).Distinct()),
                    engineer_hours = (double)eng_hours_dec,
                    engineer_days = (double)eng_days_dec,
                    engineer_cost = eng_cost,
                    assistant_names = string.Join("・", assistants.Select(r => (string)r.employee_name).Distinct()),
                    assistant_hours = (double)ast_hours_dec,
                    assistant_days = (double)ast_days_dec,
                    assistant_cost = ast_cost,
                    transport_cost = 0m,
                    distance_total = 0.0,
                    vehicle_count = 0,
                    equipment_cost = equip_cost,
                    personnel_cost = eng_cost + ast_cost,
                    total_cost = eng_cost + ast_cost + equip_cost,
                };
                result.Add(rec);
            }

            if (!string.IsNullOrWhiteSpace(filter_month))
                result = result.Where(r => r.fiscal_month == filter_month).ToList();

            if (!string.IsNullOrWhiteSpace(filter_date_from) && !string.IsNullOrWhiteSpace(filter_date_to))
            {
                result = result.Where(r =>
                    string.Compare(r.record_date, filter_date_from) >= 0 &&
                    string.Compare(r.record_date, filter_date_to) <= 0
                ).ToList();
            }

            result = apply_filters(result);
            _raw_records = result;
            is_empty_result = result.Count == 0;
            cost_records = build_display_rows(result);

            // ▼▼▼ 追加：DB保存済みの折りたたみ状態を復元 ▼▼▼
            await restore_collapse_states_async();
        }

        // ---- 現場別単価モード（Sprint 3.7）----
        private async Task load_custom_rate_mode_async(string category_code, string[] all_codes)
        {
            using var conn = database_manager.create_connection();

            var settings = await conn.QueryAsync<app_setting>("SELECT * FROM app_settings");
            var setting_dict = settings.ToDictionary(s => s.key, s => s.value);

            decimal engineer_rate = get_rate(setting_dict, "engineer_daily_rate", 34800m);
            decimal assistant_rate = get_rate(setting_dict, "assistant_daily_rate", 28000m);
            double base_hours = (double)get_rate(setting_dict, "base_hours_per_day", 8m);
            if (base_hours <= 0) base_hours = 8;
            decimal road_rate = get_rate(setting_dict, "road_cost_per_km", 50m);
            decimal highway_rate = get_rate(setting_dict, "highway_cost_per_km", 100m);

            var individual_rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            var rates = (await conn.QueryAsync<dynamic>(
                "SELECT rate_type, employee_name, daily_rate FROM filter_tab_rates WHERE filter_tab_id = @tid",
                new { tid = filter_tab_id })).ToList();

            foreach (var r in rates)
            {
                string rt = (string)r.rate_type;
                decimal dr_val = (decimal)r.daily_rate;
                if (rt == "技師") engineer_rate = dr_val;
                else if (rt == "助手") assistant_rate = dr_val;
                else if (rt == "個人" && r.employee_name != null)
                    individual_rates[(string)r.employee_name] = dr_val;
            }

            // ▼▼▼ [Sprint 5E] IN句で親＋子コードを対象に ▼▼▼
            var reports = (await conn.QueryAsync<dynamic>(@"
                SELECT id, report_date, employee_name, job_type, hours, detail
                FROM daily_reports
                WHERE category_code IN @codes
                  AND category_code NOT IN ('有給','有休')
                ORDER BY report_date",
                new { codes = all_codes })).ToList();

            if (reports.Count == 0)
            {
                _raw_records = new List<cost_record>();
                is_empty_result = true;
                cost_records = new ObservableCollection<cost_row_item>();
                return;
            }

            var id_list = string.Join(",", reports.Select(r => (long)r.id));

            var transports = (await conn.QueryAsync<dynamic>(
                $"SELECT daily_report_id, distance, vehicle, travel_method FROM daily_transport WHERE daily_report_id IN ({id_list})"
            )).ToList();

            var equipments = (await conn.QueryAsync<dynamic>(
                $@"SELECT de.daily_report_id, de.equipment_name, de.daily_rate AS equip_daily_rate,
                          dr.employee_name, dr.hours
                   FROM daily_equipment de
                   JOIN daily_reports dr ON de.daily_report_id = dr.id
                   WHERE de.daily_report_id IN ({id_list})"
            )).ToList();

            var equip_rate_map = (await conn.QueryAsync<dynamic>(
                "SELECT equipment_name, daily_rate FROM equipment_rates WHERE is_active = 1"))
                .ToDictionary(r => (string)r.equipment_name, r => (decimal)r.daily_rate,
                    StringComparer.OrdinalIgnoreCase);

            var registered_vehicles = (await conn.QueryAsync<string>(
                "SELECT vehicle_name FROM vehicles WHERE is_active = 1")).ToHashSet();

            var emp_job_map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unknown_names = reports
                .Where(r => string.IsNullOrWhiteSpace((string?)r.job_type) || (string?)r.job_type == "不明")
                .Select(r => (string)r.employee_name).Distinct().ToList();

            if (unknown_names.Count > 0)
            {
                var emp_rows = await conn.QueryAsync<dynamic>(
                    "SELECT employee_name, job_type FROM employees WHERE job_type != '' AND job_type != '不明'");
                emp_job_map = emp_rows.ToDictionary(e => (string)e.employee_name, e => (string)e.job_type,
                    StringComparer.OrdinalIgnoreCase);
            }

            var grouped = reports.GroupBy(r => (string)r.report_date).ToList();
            var result = new List<cost_record>();

            string resolve_job_type(dynamic r)
            {
                string jt = (string?)r.job_type ?? "";
                if ((string.IsNullOrWhiteSpace(jt) || jt == "不明")
                    && emp_job_map.TryGetValue((string)r.employee_name, out string? ej))
                    jt = ej ?? "";
                return jt;
            }

            foreach (var g in grouped)
            {
                var day_ids = new HashSet<long>(g.Select(r => (long)r.id));
                var date = DateTime.Parse(g.Key);
                string fiscal_month = $"{date.Year}年{date.Month}月度";

                var engineers = g.Where(r => resolve_job_type(r).Contains("技師")).ToList();
                var assistants = g.Where(r => resolve_job_type(r).Contains("助手")).ToList();

                double eng_h = engineers.Sum(r => (double)(r.hours ?? 0));
                double ast_h = assistants.Sum(r => (double)(r.hours ?? 0));
                decimal eng_d = Math.Round((decimal)(eng_h / base_hours), 4);
                decimal ast_d = Math.Round((decimal)(ast_h / base_hours), 4);

                decimal eng_c = engineers.Aggregate(0m, (sum, r) =>
                {
                    string name = (string)r.employee_name;
                    decimal rate = individual_rates.TryGetValue(name, out decimal ir) ? ir : engineer_rate;
                    return sum + Math.Round((decimal)((double)(r.hours ?? 0) / base_hours) * rate, 0);
                });
                decimal ast_c = assistants.Aggregate(0m, (sum, r) =>
                {
                    string name = (string)r.employee_name;
                    decimal rate = individual_rates.TryGetValue(name, out decimal ir) ? ir : assistant_rate;
                    return sum + Math.Round((decimal)((double)(r.hours ?? 0) / base_hours) * rate, 0);
                });

                var day_transport = transports
                    .Where(t => day_ids.Contains((long)t.daily_report_id)
                             && !string.IsNullOrWhiteSpace((string?)t.vehicle)
                             && (string?)t.vehicle != "なし").ToList();

                decimal tra_c = 0m;
                double dist_total = 0.0;
                foreach (var t in day_transport)
                {
                    double round_trip = (double)(t.distance ?? 0) * 2;
                    string travel_method = (string?)t.travel_method ?? "";
                    decimal unit = (travel_method == "高速" && round_trip <= 250) ? highway_rate : road_rate;
                    tra_c += (decimal)round_trip * unit;
                    dist_total += round_trip;
                }
                tra_c = Math.Round(tra_c, 0);

                int veh_count = Math.Min(
                    day_transport
                        .Where(t => !string.IsNullOrWhiteSpace((string?)t.vehicle)
                                 && (string?)t.vehicle != "なし"
                                 && registered_vehicles.Contains((string)t.vehicle))
                        .Select(t => (string)t.vehicle).Distinct().Count(), 2);

                var day_equipment = equipments
                    .Where(e => day_ids.Contains((long)e.daily_report_id)
                             && !string.IsNullOrWhiteSpace((string?)e.equipment_name)
                             && (string?)e.equipment_name != "なし").ToList();

                var equ_groups = day_equipment.GroupBy(e => (string)e.equipment_name)
                    .Where(eg => eg.Sum(e => (double)(e.hours ?? 0)) >= 6.0).ToList();

                decimal equ_c = 0m;
                int equ_qty = equ_groups.Count;
                var detail_parts = new List<string>();

                foreach (var eg in equ_groups)
                {
                    decimal item_rate = (decimal)(eg.First().equip_daily_rate ?? 0);
                    if (item_rate == 0 && equip_rate_map.TryGetValue(eg.Key, out decimal master_rate))
                        item_rate = master_rate;
                    equ_c += item_rate;

                    var user_parts = eg.GroupBy(e => (string?)e.employee_name ?? "")
                        .Where(ug => !string.IsNullOrWhiteSpace(ug.Key) && ug.Sum(u => (double)(u.hours ?? 0)) > 0)
                        .Select(ug => $"{ug.Key} {ug.Sum(u => (double)(u.hours ?? 0)):0.0}h").ToList();
                    detail_parts.Add($"{eg.Key}（{string.Join("・", user_parts)}）");
                }

                string equ_names = string.Join("・", equ_groups.Select(eg => eg.Key));
                string equipment_detail_str = string.Join("　", detail_parts);
                string eng_names = string.Join("・", engineers.Select(r => (string)r.employee_name)
                    .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
                string ast_names = string.Join("・", assistants.Select(r => (string)r.employee_name)
                    .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
                string work = string.Join("・", g.Select(r => (string?)r.detail ?? "")
                    .Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Replace("、", "・")).Distinct());

                result.Add(new cost_record
                {
                    category_code = category_code,
                    record_date = g.Key,
                    fiscal_month = fiscal_month,
                    work_content = work,
                    engineer_names = eng_names,
                    engineer_hours = eng_h,
                    engineer_days = (double)eng_d,
                    engineer_cost = eng_c,
                    assistant_names = ast_names,
                    assistant_hours = ast_h,
                    assistant_days = (double)ast_d,
                    assistant_cost = ast_c,
                    transport_cost = tra_c,
                    distance_total = Math.Round(dist_total, 1),
                    vehicle_count = veh_count,
                    equipment_names = equ_names,
                    equipment_detail = equipment_detail_str,
                    equipment_cost = equ_c,
                    equipment_quantity = equ_qty,
                    personnel_cost = eng_c + ast_c,
                    total_cost = Math.Round(eng_c + ast_c + tra_c + equ_c, 0),
                });
            }

            if (!string.IsNullOrWhiteSpace(filter_month))
                result = result.Where(r => r.fiscal_month == filter_month).ToList();

            if (!string.IsNullOrWhiteSpace(filter_date_from) && !string.IsNullOrWhiteSpace(filter_date_to))
            {
                result = result.Where(r =>
                    string.Compare(r.record_date, filter_date_from) >= 0 &&
                    string.Compare(r.record_date, filter_date_to) <= 0).ToList();
            }

            result = apply_filters(result);
            _raw_records = result;
            is_empty_result = result.Count == 0;
            cost_records = build_display_rows(result);

            // ▼▼▼ 追加：DB保存済みの折りたたみ状態を復元 ▼▼▼
            await restore_collapse_states_async();
        }

        // ---- ツールチップ構築 ----
        private async Task build_tooltip_async()
        {
            if (filter_tab_id == 0) { info_tooltip = ""; return; }

            var sb = new System.Text.StringBuilder();

            if (use_custom_rates)
            {
                sb.AppendLine("【適用中の単価】");
                try
                {
                    using var conn = Data.database_manager.create_connection();
                    var rates = (await conn.QueryAsync<dynamic>(
                        "SELECT rate_type, employee_name, daily_rate FROM filter_tab_rates WHERE filter_tab_id = @tid ORDER BY rate_type, employee_name",
                        new { tid = filter_tab_id })).ToList();

                    if (rates.Count == 0) { sb.AppendLine("（単価設定なし）"); }
                    else
                    {
                        foreach (var r in rates.Where(r => (string)r.rate_type != "個人"))
                            sb.AppendLine($"{(string)r.rate_type}単価：{(decimal)r.daily_rate:#,##0} 円/日");
                        var individual = rates.Where(r => (string)r.rate_type == "個人").ToList();
                        if (individual.Count > 0)
                        {
                            sb.AppendLine("【個人別単価】");
                            foreach (var r in individual)
                                sb.AppendLine($"  {(string)(r.employee_name ?? "")}：{(decimal)r.daily_rate:#,##0} 円/日");
                        }
                    }
                }
                catch { sb.AppendLine("（単価情報の取得に失敗しました）"); }
            }

            bool has_filter =
                !string.IsNullOrWhiteSpace(filter_month) ||
                (!string.IsNullOrWhiteSpace(filter_date_from) && !string.IsNullOrWhiteSpace(filter_date_to)) ||
                !string.IsNullOrWhiteSpace(filter_content) ||
                !string.IsNullOrWhiteSpace(filter_names) ||
                is_single_mode ||
                agg_mode == "task";   // ▼追加(B)

            if (has_filter || !use_custom_rates)
            {
                if (use_custom_rates && sb.Length > 0) sb.AppendLine("─────────────");
                sb.AppendLine("【絞り込み条件】");
                if (!string.IsNullOrWhiteSpace(filter_month)) sb.AppendLine($"月度：{filter_month}");
                if (!string.IsNullOrWhiteSpace(filter_date_from) && !string.IsNullOrWhiteSpace(filter_date_to))
                    sb.AppendLine($"期間：{filter_date_from} ～ {filter_date_to}");
                if (!string.IsNullOrWhiteSpace(filter_content))
                {
                    string m = filter_match == "exact" ? "完全一致" : "部分一致";
                    sb.AppendLine($"作業内容：{filter_content}（{m}）");
                }
                if (!string.IsNullOrWhiteSpace(filter_names)) sb.AppendLine($"氏名：{filter_names}");
                if (is_single_mode) sb.AppendLine("個人別集計モード：ON");
                if (agg_mode == "task") sb.AppendLine("集計：業務単位集計（担当者別）");   // ▼追加(B)
                if (!has_filter) sb.AppendLine("（条件なし・全件）");
            }

            info_tooltip = sb.ToString().TrimEnd();
        }

        // ---- 全フィルタークリア（現場情報バーの✕ボタンから呼ばれる）----
        public void clear_all_filters()
        {
            _search_text = "";
            _col_filter_work = "";
            _col_filter_eng = "（すべて）";
            _col_filter_ast = "（すべて）";
            OnPropertyChanged(nameof(search_text));
            OnPropertyChanged(nameof(col_filter_work));
            OnPropertyChanged(nameof(col_filter_eng));
            OnPropertyChanged(nameof(col_filter_ast));
            update_search_filter();
        }

        // ---- 月度グループ折り畳み ----
        // ▼▼▼ 追加：全月度の折りたたみ状態フラグ ▼▼▼
        // true：全月度が折りたたまれている / false：1つでも展開されている（または混在）
        // XAMLのDataTriggerでボタンカラー・テキスト切替に使用
        private bool _is_all_collapsed = false;
        public bool is_all_collapsed
        {
            get => _is_all_collapsed;
            private set => SetProperty(ref _is_all_collapsed, value);
        }

        public void toggle_month_group(string fiscal_month)
        {
            var header = cost_records.FirstOrDefault(r => r.is_header && r.fiscal_month == fiscal_month);
            if (header == null) return;

            header.is_collapsed = !header.is_collapsed;
            bool hide = header.is_collapsed;

            foreach (var row in cost_records.Where(r => r.fiscal_month == fiscal_month && !r.is_header))
                row.is_hidden = hide;

            // ▼▼▼ 追加：ICollectionView を更新して折り畳み状態を反映 ▼▼▼
            // 検索フィルターがない場合のみ更新（フィルターありのときは is_hidden を無視するため不要）
            if (!has_any_search_filter)
                _filtered_view?.Refresh();

            // 全月度の折りたたみ状態を再計算して通知
            recalc_is_all_collapsed();

            // ▼▼▼ 追加：折りたたみ状態をDBに保存（fire-and-forget） ▼▼▼
            _ = save_collapse_state_async(fiscal_month, header.is_collapsed);
        }

        // ▼▼▼ 追加：全年度の折りたたみ状態フラグ ▼▼▼
        // XAMLのDataTriggerでボタンカラー・テキスト切替に使用
        private bool _is_all_years_collapsed = false;
        public bool is_all_years_collapsed
        {
            get => _is_all_years_collapsed;
            private set => SetProperty(ref _is_all_years_collapsed, value);
        }

        // ▼▼▼ 追加：年ヘッダーの折りたたみトグル ▼▼▼
        public void toggle_year_group(string year_key)
        {
            bool current = _year_collapsed.TryGetValue(year_key, out bool c) && c;
            bool new_state = !current;
            _year_collapsed[year_key] = new_state;

            var year_header = cost_records.FirstOrDefault(r => r.is_year_header && r.year_key == year_key);
            if (year_header != null) year_header.is_collapsed = new_state;

            if (!has_any_search_filter)
                _filtered_view?.Refresh();

            recalc_is_all_years_collapsed();
        }

        // ▼▼▼ 追加：トグルボタン用：全年度展開/折りたたみを切り替える ▼▼▼
        public void toggle_all_years()
        {
            if (_is_all_years_collapsed)
                expand_all_years();
            else
                collapse_all_years();
        }

        // ▼▼▼ 追加：すべての年を展開する ▼▼▼
        public void expand_all_years()
        {
            var year_keys = cost_records
                .Where(r => r.is_year_header)
                .Select(r => r.year_key)
                .Distinct().ToList();

            foreach (var yk in year_keys)
            {
                _year_collapsed[yk] = false;
                var yh = cost_records.FirstOrDefault(r => r.is_year_header && r.year_key == yk);
                if (yh != null) yh.is_collapsed = false;
            }

            if (!has_any_search_filter)
                _filtered_view?.Refresh();

            is_all_years_collapsed = false;
        }

        // ▼▼▼ 追加：すべての年を折りたたむ ▼▼▼
        public void collapse_all_years()
        {
            var year_keys = cost_records
                .Where(r => r.is_year_header)
                .Select(r => r.year_key)
                .Distinct().ToList();

            foreach (var yk in year_keys)
            {
                _year_collapsed[yk] = true;
                var yh = cost_records.FirstOrDefault(r => r.is_year_header && r.year_key == yk);
                if (yh != null) yh.is_collapsed = true;
            }

            if (!has_any_search_filter)
                _filtered_view?.Refresh();

            is_all_years_collapsed = true;
        }

        // ▼▼▼ 追加：全年折りたたみ状態を再計算する ▼▼▼
        private void recalc_is_all_years_collapsed()
        {
            var year_headers = cost_records.Where(r => r.is_year_header).ToList();
            is_all_years_collapsed = year_headers.Count > 0 && year_headers.All(r => r.is_collapsed);
        }

        // ▼▼▼ 追加：指定年キーが折りたたまれているか返す ▼▼▼
        public bool is_year_collapsed(string year_key) =>
            _year_collapsed.TryGetValue(year_key, out bool c) && c;

        // ▼▼▼ 追加：トグルボタン用：全展開 / 全折りたたみを切り替える ▼▼▼
        // 全折りたたみ状態 → 全展開、それ以外（展開・混在）→ 全折りたたみ
        public void toggle_all_months()
        {
            if (_is_all_collapsed)
                expand_all_months();
            else
                collapse_all_months();
        }

        // ▼▼▼ 追加：すべての月度グループを展開する ▼▼▼
        // 全ヘッダーの is_collapsed を false にして全非ヘッダー行を表示状態に戻す
        public void expand_all_months()
        {
            foreach (var header in cost_records.Where(r => r.is_header))
                header.is_collapsed = false;
            foreach (var row in cost_records.Where(r => !r.is_header))
                row.is_hidden = false;

            if (!has_any_search_filter)
                _filtered_view?.Refresh();

            is_all_collapsed = false;

            // ▼▼▼ 追加：全状態をDBに保存（fire-and-forget） ▼▼▼
            _ = save_all_collapse_states_async();
        }

        // ▼▼▼ 追加：すべての月度グループを折りたたむ ▼▼▼
        // 全ヘッダーの is_collapsed を true にして全非ヘッダー行を非表示状態にする
        public void collapse_all_months()
        {
            foreach (var header in cost_records.Where(r => r.is_header))
                header.is_collapsed = true;
            foreach (var row in cost_records.Where(r => !r.is_header))
                row.is_hidden = true;

            if (!has_any_search_filter)
                _filtered_view?.Refresh();

            is_all_collapsed = true;

            // ▼▼▼ 追加：全状態をDBに保存（fire-and-forget） ▼▼▼
            _ = save_all_collapse_states_async();
        }

        // ▼▼▼ 追加：個別トグル後に全折りたたみ状態を再計算する ▼▼▼
        // ヘッダーが1つもない（データなし）場合は false とする
        private void recalc_is_all_collapsed()
        {
            var headers = cost_records.Where(r => r.is_header).ToList();
            is_all_collapsed = headers.Count > 0 && headers.All(r => r.is_collapsed);
        }

        // ▼▼▼ 追加：起動時・タブ切替時に折りたたみ状態をDBから復元する ▼▼▼
        // is_collapsed=1 の月度のみ取得（展開はデフォルトのためスキップ）
        private async Task restore_collapse_states_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var collapsed_months = (await conn.QueryAsync<string>(
                    @"SELECT fiscal_month FROM month_collapse_states
                      WHERE pc_user_id = @uid AND filter_tab_id = @tid AND is_collapsed = 1",
                    new { uid = UserSession.user_id, tid = filter_tab_id })).ToHashSet();

                if (collapsed_months.Count == 0) return;

                // 保存済みの折りたたみ月度をメモリ上のデータに反映
                foreach (var header in cost_records.Where(r => r.is_header && collapsed_months.Contains(r.fiscal_month)))
                {
                    header.is_collapsed = true;
                    foreach (var row in cost_records.Where(r => r.fiscal_month == header.fiscal_month && !r.is_header))
                        row.is_hidden = true;
                }

                if (!has_any_search_filter)
                    _filtered_view?.Refresh();

                recalc_is_all_collapsed();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"折りたたみ状態復元エラー: {ex.Message}");
            }
        }

        // ▼▼▼ 追加：個別トグル時に1月度の状態をDBに保存する ▼▼▼
        // INSERT OR REPLACE でUPSERT（既存行は更新・なければ挿入）
        private async Task save_collapse_state_async(string fiscal_month, bool is_collapsed)
        {
            // ▼ 追加 [Sprint 8 / Phase 0]：読取専用PCは折りたたみ状態を NAS へ書かない（同時書込を避ける）
            if (UserSession.is_read_only) return;
            try
            {
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(@"
                    INSERT OR REPLACE INTO month_collapse_states
                        (pc_user_id, filter_tab_id, fiscal_month, is_collapsed, updated_at)
                    VALUES (@uid, @tid, @month, @col, datetime('now','localtime'))",
                    new
                    {
                        uid = UserSession.user_id,
                        tid = filter_tab_id,
                        month = fiscal_month,
                        col = is_collapsed ? 1 : 0
                    });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"折りたたみ状態保存エラー: {ex.Message}");
            }
        }

        // ▼▼▼ 追加：全折/全展時に全月度の状態をまとめてDBに保存する ▼▼▼
        // 一旦このタブの全行を削除してから再挿入（トランザクションで整合性確保）
        private async Task save_all_collapse_states_async()
        {
            // ▼ 追加 [Sprint 8 / Phase 0]：読取専用PCは折りたたみ状態を NAS へ書かない（同時書込を避ける）
            if (UserSession.is_read_only) return;
            try
            {
                using var conn = database_manager.create_connection();
                using var tx = conn.BeginTransaction();

                // このユーザー×タブの全状態を削除してから再挿入
                await conn.ExecuteAsync(
                    "DELETE FROM month_collapse_states WHERE pc_user_id = @uid AND filter_tab_id = @tid",
                    new { uid = UserSession.user_id, tid = filter_tab_id }, tx);

                foreach (var header in cost_records.Where(r => r.is_header))
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO month_collapse_states
                            (pc_user_id, filter_tab_id, fiscal_month, is_collapsed, updated_at)
                        VALUES (@uid, @tid, @month, @col, datetime('now','localtime'))",
                        new
                        {
                            uid = UserSession.user_id,
                            tid = filter_tab_id,
                            month = header.fiscal_month,
                            col = header.is_collapsed ? 1 : 0
                        }, tx);
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"折りたたみ状態一括保存エラー: {ex.Message}");
            }
        }

        // ================================================================
        // ▼▼▼ 追加：ICollectionView 関連メソッド ▼▼▼
        // ================================================================

        /// <summary>
        /// cost_records が変わったとき ICollectionView を再構築する
        /// コレクションの参照が変わるため必ず再作成が必要
        /// </summary>
        private void rebuild_filtered_view()
        {
            _filtered_view = CollectionViewSource.GetDefaultView(_cost_records);
            _filtered_view.Filter = filter_row;
            OnPropertyChanged(nameof(filtered_records));
            update_search_filter(); // 既存の検索テキストがあれば再適用
        }

        /// <summary>
        /// 検索条件が変わったとき _matching_months を事前計算してビューを更新する
        /// ヘッダー・小計行の表示制御に「その月度にマッチする通常行があるか」が必要なため
        /// 事前計算で月度セットを作り filter_row() が参照する
        /// </summary>
        private void update_search_filter()
        {
            if (!has_any_search_filter)
            {
                _matching_months = new HashSet<string>();
            }
            else
            {
                // マッチする通常行の月度・年を事前収集
                _matching_months = _cost_records
                    .Where(item => item.is_normal && normal_row_matches(item))
                    .Select(item => item.fiscal_month)
                    .ToHashSet();
                // ▼▼▼ 追加：マッチした月度が属する年セットも収集 ▼▼▼
                _matching_years = _cost_records
                    .Where(item => item.is_normal && normal_row_matches(item))
                    .Select(item => item.year_key)
                    .ToHashSet();
            }
            _filtered_view?.Refresh();
        }

        /// <summary>
        /// ICollectionView のフィルター述語
        /// フィルターなし → is_hidden / 年折りたたみで制御
        /// フィルターあり → マッチした通常行 + 一致月度・年のヘッダー・小計のみ表示
        /// </summary>
        private bool filter_row(object obj)
        {
            if (obj is not cost_row_item item) return true;

            if (!has_any_search_filter)
            {
                // 年ヘッダー行：常に表示
                if (item.is_year_header) return true;

                // 年が折りたたまれている場合 → 配下の月ヘッダー・データ行・小計行を非表示
                if (_year_collapsed.TryGetValue(item.year_key, out bool year_col) && year_col)
                    return false;

                // 月ヘッダー・小計：年が展開中なら常に表示
                if (!item.is_normal) return true;

                // 通常行：is_hidden で月折りたたみ制御
                return !item.is_hidden;
            }

            // フィルターあり
            // 年ヘッダー：マッチした年のみ表示
            if (item.is_year_header) return _matching_years.Contains(item.year_key);

            // 月ヘッダー・小計：マッチ月度のみ表示
            if (!item.is_normal)
                return _matching_months.Contains(item.fiscal_month);

            return normal_row_matches(item);
        }

        /// <summary>
        /// 通常行が検索条件にマッチするか判定する
        /// グローバル検索（OR）+ 列別フィルター（AND）の複合条件
        /// </summary>
        private bool normal_row_matches(cost_row_item item)
        {
            // グローバル検索：作業内容・技師氏名・助手氏名 の OR 検索
            if (!string.IsNullOrWhiteSpace(_search_text))
            {
                bool global_hit =
                    item.work_content?.Contains(_search_text, StringComparison.OrdinalIgnoreCase) == true ||
                    item.engineer_names?.Contains(_search_text, StringComparison.OrdinalIgnoreCase) == true ||
                    item.assistant_names?.Contains(_search_text, StringComparison.OrdinalIgnoreCase) == true;
                if (!global_hit) return false;
            }

            // 列別フィルター（AND 条件）
            if (!string.IsNullOrWhiteSpace(_col_filter_work) &&
                item.work_content?.Contains(_col_filter_work, StringComparison.OrdinalIgnoreCase) != true)
                return false;

            // ComboBox列フィルター：「（すべて）」または空の場合はフィルターなし
            if (!string.IsNullOrEmpty(_col_filter_eng) && _col_filter_eng != "（すべて）" &&
                item.engineer_names?.Contains(_col_filter_eng, StringComparison.OrdinalIgnoreCase) != true)
                return false;

            if (!string.IsNullOrEmpty(_col_filter_ast) && _col_filter_ast != "（すべて）" &&
                item.assistant_names?.Contains(_col_filter_ast, StringComparison.OrdinalIgnoreCase) != true)
                return false;

            return true;
        }

        // ================================================================

        // ---- フィルター適用（タブ定義の作業内容・氏名条件）----
        private List<cost_record> apply_filters(List<cost_record> rows)
        {
            if (!string.IsNullOrWhiteSpace(filter_content))
            {
                var keywords = filter_content.Split(',')
                    .Select(k => k.Trim()).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
                bool is_partial = filter_match != "exact";
                rows = rows.Where(r =>
                {
                    string wc = r.work_content ?? "";
                    return keywords.Any(keyword =>
                        is_partial ? wc.Contains(keyword) : wc.Split('・').Any(part => part.Trim() == keyword));
                }).ToList();
            }

            if (!string.IsNullOrWhiteSpace(filter_names))
            {
                var names = filter_names.Split(',').Select(n => n.Trim())
                    .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                rows = rows.Where(r =>
                    names.Any(n =>
                        (r.engineer_names ?? "").Contains(n) ||
                        (r.assistant_names ?? "").Contains(n))
                ).ToList();
            }

            return rows;
        }

        // ---- 表示行ビルド（月度ヘッダー・小計行を挿入）----
        // cost_row_item は class 型のため with 式は使用不可（CS8858対策）
        // fiscal_month は set プロパティで直接代入（cost_row_item.cs 側を init → set に変更済み）
        private static ObservableCollection<cost_row_item> build_display_rows(List<cost_record> rows)
        {
            var result = new ObservableCollection<cost_row_item>();

            // ▼▼▼ 修正：年単位でグループ化 → 年ヘッダー → 月度ヘッダー → データ行 → 小計行 の順で構築 ▼▼▼
            // fiscal_month の形式は「2025年4月度」なので先頭4文字が年
            var by_year = rows
                .GroupBy(r => r.fiscal_month?.Length >= 4 ? r.fiscal_month[..4] : "不明")
                .OrderBy(g => g.Key);

            foreach (var year_group in by_year)
            {
                // 年ヘッダー行を挿入
                var year_header = cost_row_item.make_year_header(year_group.Key);
                result.Add(year_header);

                // 年グループ内を月度でグループ化
                var by_month = year_group
                    .GroupBy(r => r.fiscal_month)
                    .OrderBy(g => g.Min(r => r.record_date));

                foreach (var month_group in by_month)
                {
                    // 月度ヘッダー行（fiscal_monthとyear_keyを両方セット）
                    var month_header = cost_row_item.make_header(month_group.Key);
                    month_header.year_key = year_group.Key;
                    result.Add(month_header);

                    foreach (var rec in month_group.OrderBy(r => r.record_date))
                    {
                        var item = cost_row_item.from_record(rec);
                        item.fiscal_month = month_group.Key;
                        item.year_key = year_group.Key;
                        result.Add(item);
                    }

                    var subtotal = cost_row_item.make_subtotal(month_group.Key, month_group);
                    subtotal.year_key = year_group.Key;
                    result.Add(subtotal);
                }
            }

            return result;
        }

        // ---- 合計変更通知 ----
        private void notify_totals()
        {
            OnPropertyChanged(nameof(total_engineer_cost));
            OnPropertyChanged(nameof(total_assistant_cost));
            OnPropertyChanged(nameof(total_transport_cost));
            OnPropertyChanged(nameof(total_equipment_cost));
            OnPropertyChanged(nameof(grand_total));
            OnPropertyChanged(nameof(display_total_engineer_cost));
            OnPropertyChanged(nameof(display_total_assistant_cost));
            OnPropertyChanged(nameof(display_total_transport_cost));
            OnPropertyChanged(nameof(display_total_equipment_cost));
            OnPropertyChanged(nameof(display_grand_total));
            // ▼▼▼ 追加：ComboBox用の一意リストも更新通知 ▼▼▼
            OnPropertyChanged(nameof(distinct_eng_names));
            OnPropertyChanged(nameof(distinct_ast_names));
        }

        // ---- ヘルパー ----

        /// <summary>
        /// ▼▼▼ 追加：一括取得済みの records_map から直接データをセットする（起動時専用）▼▼▼
        /// cost_view_model.load_projects_async() で全現場分を一括取得したデータを受け取り、
        /// DB呼び出しなしでこのタブのレコードをセットする。
        ///
        /// 【適用対象】通常モードのタブのみ（is_single_mode=false かつ use_custom_rates=false）
        /// 【適用外】is_single_mode=true または use_custom_rates=true のタブ
        ///   → これらは daily_reports からの直接集計が必要なため load_records_async() を使い続ける
        ///   → project_cost_view_model.set_filter_tabs_bulk_async() 側でこの判定を行う
        /// </summary>
        public void set_records_from_map(
            string category_code,
            List<string> child_codes,
            Dictionary<string, List<cost_record>> records_map)
        {
            // 親コード＋子コードのレコードをDictionaryから取得してマージ
            var all_codes = new List<string> { category_code };
            all_codes.AddRange(child_codes);

            var rows = all_codes
                .SelectMany(code => records_map.TryGetValue(code, out var r) ? r : new List<cost_record>())
                .OrderBy(r => r.record_date)
                .ToList();

            // 子コードのレコードは表示上親コードとして扱う（元データは変更しない）
            foreach (var r in rows)
                r.category_code = category_code;

            // filter_month フィルター適用
            if (!string.IsNullOrWhiteSpace(filter_month))
                rows = rows.Where(r => r.fiscal_month == filter_month).ToList();

            // 期間フィルター適用
            if (!string.IsNullOrWhiteSpace(filter_date_from)
                && !string.IsNullOrWhiteSpace(filter_date_to))
            {
                rows = rows.Where(r =>
                    string.Compare(r.record_date, filter_date_from) >= 0 &&
                    string.Compare(r.record_date, filter_date_to) <= 0
                ).ToList();
            }

            // 作業内容・氏名フィルター適用
            rows = apply_filters(rows);

            _raw_records = rows;
            is_empty_result = rows.Count == 0;
            cost_records = build_display_rows(rows);
            // 注：折りたたみ状態の復元（restore_collapse_states_async）は
            // 起動時の一括セットでは省略する。タブを実際に開いたときに
            // load_records_async が呼ばれた際に復元される。

            // ▼修正：起動・再オープン時もⓘ（絞り込み条件）の内容を生成する。
            //   これが無いと info_tooltip が空のまま → Styleの「空なら非表示」トリガでⓘが消えていた。
            //   build_tooltip_async は UI スレッド起点のため fire-and-forget で安全（完了時に info_tooltip を更新）。
            _ = build_tooltip_async();
        }

        private static decimal get_rate(Dictionary<string, string> dict, string key, decimal fallback)
        {
            if (dict.TryGetValue(key, out string? val) && decimal.TryParse(val, out decimal d))
                return d;
            return fallback;
        }
    }
}
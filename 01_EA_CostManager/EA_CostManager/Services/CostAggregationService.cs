using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Models;

namespace EA_CostManager.Services
{
    /// <summary>
    /// 原価集計サービス
    /// daily_reports を日付×区分コードでグループ化し cost_records に集約する
    /// 洗い替え方式（同期間・同区分の既存レコードを削除→再INSERT）
    /// </summary>
    public class CostAggregationService
    {
        // ---- 内部クエリ用 POCO（DBモデルに依存しない独立定義） ----

        private class dr_row
        {
            public int id { get; set; }
            public string report_date { get; set; } = "";
            public string employee_name { get; set; } = "";
            public string job_type { get; set; } = "";
            public string category_code { get; set; } = "";
            public string detail { get; set; } = "";
            public double hours { get; set; }
        }

        private class dt_row
        {
            public int daily_report_id { get; set; }
            public double distance { get; set; }
            public string vehicle { get; set; } = "";
            // ▼▼▼ 追加：交通費再計算に必要 ▼▼▼
            public string travel_method { get; set; } = "";
        }

        private class de_row
        {
            public int daily_report_id { get; set; }
            public string equipment_name { get; set; } = "";
            public decimal daily_rate { get; set; }
            public int quantity { get; set; }
            // ▼▼▼ 追加：使用者・時間（daily_reports JOIN） ▼▼▼
            public string employee_name { get; set; } = "";
            public double hours { get; set; }
        }

        private class setting_row
        {
            public string key { get; set; } = "";
            public string value { get; set; } = "";
        }

        // ---- 公開メソッド ----

        /// <summary>
        /// 指定カテゴリ・期間の原価集計を実行（洗い替え方式）
        /// </summary>
        /// <param name="category_code">区分コード</param>
        /// <param name="start_date">開始日 yyyy-MM-dd</param>
        /// <param name="end_date">終了日 yyyy-MM-dd</param>
        /// <param name="operator_name">操作者名（ログ用）</param>
        /// <returns>(集計行数, エラーメッセージ) ※正常時はエラーメッセージ空文字</returns>
        public async Task<(int count, string error)> aggregate_async(
            string category_code,
            string start_date,
            string end_date,
            string operator_name = "",
            int? project_id = null)  // ▼▼▼ 追加：現場別単価適用時に渡すproject_id ▼▼▼
        {
            // ▼ 追加 [Sprint 8 / Phase 0]：読取専用モードでは集計（cost_records の DELETE→INSERT 洗い替え）を止める。
            //   例外ではなくエラー文字列で返す（呼び出し側は既にエラー表示に対応済み）。
            if (UserSession.is_read_only)
                return (0, "閲覧のみのモードのため、集計（保存）は実行できません。");

            try
            {
                using var conn = database_manager.create_connection();

                // 1. 設定値をロード
                var raw = (await conn.QueryAsync<setting_row>(
                    "SELECT key, value FROM app_settings")).ToList();
                var cfg = raw.ToDictionary(s => s.key, s => s.value);

                decimal eng_rate = parse_m(cfg, "engineer_daily_rate", 34800m);
                decimal ast_rate = parse_m(cfg, "assistant_daily_rate", 28000m);

                // ▼▼▼ 追加：project_ratesが設定されていれば上書き ▼▼▼
                // project_id が指定された場合は project_rates テーブルを参照して
                // 技師・助手・個人別の単価をデフォルトから上書きする
                var individual_rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                if (project_id.HasValue)
                {
                    var rates = (await conn.QueryAsync<dynamic>(
                        "SELECT rate_type, employee_name, daily_rate FROM project_rates WHERE project_id = @pid",
                        new { pid = project_id.Value })).ToList();
                    foreach (var r in rates)
                    {
                        string rt = (string)r.rate_type;
                        decimal dr = (decimal)r.daily_rate;
                        if (rt == "技師") eng_rate = dr;
                        else if (rt == "助手") ast_rate = dr;
                        else if (rt == "個人" && r.employee_name != null)
                            individual_rates[(string)r.employee_name] = dr;
                    }
                }
                double base_hours = parse_d(cfg, "base_hours_per_day", 8.0);
                if (base_hours <= 0) base_hours = 8; // ゼロ除算ガード

                // 2. 対象の日報をロード
                var reports = (await conn.QueryAsync<dr_row>(@"
                    SELECT id, report_date, employee_name, job_type,
                           category_code, detail, hours
                    FROM daily_reports
                    WHERE category_code = @cat
                      AND report_date BETWEEN @s AND @e",
                    new { cat = category_code, s = start_date, e = end_date }
                )).ToList();

                // 日報が0件の場合は集計不要（エラーではない）
                if (reports.Count == 0)
                    return (0, string.Empty);

                // 3. 関連する交通・機材データをロード
                //    ▼ 弱点：件数が多い場合はIN句の上限（SQLite制限）に注意
                string id_list = string.Join(",", reports.Select(r => r.id));

                var transports = (await conn.QueryAsync<dt_row>(
                    // ▼▼▼ 修正：transport_cost削除・travel_method追加 ▼▼▼
                    $"SELECT daily_report_id, distance, vehicle, travel_method FROM daily_transport WHERE daily_report_id IN ({id_list})"
                )).ToList();

                // ▼▼▼ 修正：daily_reportsとJOINして使用者・時間を取得（6h判定用） ▼▼▼
                var equipments = (await conn.QueryAsync<de_row>(
                    $@"SELECT de.daily_report_id, de.equipment_name, de.daily_rate, de.quantity,
                              dr.employee_name, dr.hours
                       FROM daily_equipment de
                       JOIN daily_reports dr ON de.daily_report_id = dr.id
                       WHERE de.daily_report_id IN ({id_list})"
                )).ToList();

                // ▼▼▼ 追加：equipment_rates テーブルから機材日額マスタを取得 ▼▼▼
                // 旧形式日報はdaily_equipment.daily_rate=0のため、マスタ参照で補完する
                // キー：equipment_name（大文字小文字無視）/ 値：日額
                var equip_rate_map = (await conn.QueryAsync<(string name, decimal rate)>(
                    "SELECT equipment_name, daily_rate FROM equipment_rates WHERE is_active = 1"))
                    .ToDictionary(r => r.name, r => r.rate, StringComparer.OrdinalIgnoreCase);

                // ▼▼▼ 追加：job_typeが空/"不明"の場合をemployeesテーブルから補完 ▼▼▼
                // 旧形式日報は職種列がないため "不明" で取込まれる。
                // 集計前にemployeesテーブルから正しいjob_typeを上書きする。
                var unknown_names = reports
                    .Where(r => string.IsNullOrWhiteSpace(r.job_type) || r.job_type == "不明")
                    .Select(r => r.employee_name)
                    .Distinct()
                    .ToList();

                if (unknown_names.Count > 0)
                {
                    var emp_map = (await conn.QueryAsync<(string name, string job)>(
                        "SELECT employee_name, job_type FROM employees WHERE job_type != '' AND job_type != '不明'"))
                        .ToDictionary(e => e.name, e => e.job);

                    foreach (var r in reports)
                    {
                        if ((string.IsNullOrWhiteSpace(r.job_type) || r.job_type == "不明")
                            && emp_map.TryGetValue(r.employee_name, out string? emp_job))
                        {
                            r.job_type = emp_job;
                        }
                    }
                }

                // 4. 日付でグループ化して集計レコードを生成
                var groups = reports
                    .GroupBy(r => r.report_date)
                    .OrderBy(g => g.Key)
                    .ToList();

                var records = new List<cost_record>();

                foreach (var g in groups)
                {
                    var g_ids = g.Select(r => r.id).ToHashSet();

                    // 月度計算（カレンダー月：1日〜末日）
                    // フォーマット："YYYY年M月度"（例: "2025年10月度"）
                    string fiscal_month = calc_fiscal_month(g.Key);

                    // 職種で分類（フライトオペレータは今回対象外）
                    var engineers = g.Where(r => r.job_type.Contains("技師")).ToList();
                    var assistants = g.Where(r => r.job_type.Contains("助手")).ToList();

                    // 時間集計
                    double eng_h = engineers.Sum(r => r.hours);
                    double ast_h = assistants.Sum(r => r.hours);

                    // 人工計算（double で除算後 decimal に変換・小数4桁）
                    decimal eng_d = Math.Round((decimal)(eng_h / base_hours), 4);
                    decimal ast_d = Math.Round((decimal)(ast_h / base_hours), 4);

                    // ▼▼▼ 修正：個人別単価対応 ▼▼▼
                    // individual_rates に氏名があれば個人単価を使用、なければ職種単価
                    decimal eng_c = engineers.Aggregate(0m, (sum, r) =>
                    {
                        decimal rate = individual_rates.TryGetValue(r.employee_name, out decimal ir)
                            ? ir : eng_rate;
                        return sum + Math.Round((decimal)(r.hours / base_hours) * rate, 0);
                    });
                    decimal ast_c = assistants.Aggregate(0m, (sum, r) =>
                    {
                        decimal rate = individual_rates.TryGetValue(r.employee_name, out decimal ir)
                            ? ir : ast_rate;
                        return sum + Math.Round((decimal)(r.hours / base_hours) * rate, 0);
                    });
                    decimal per_c = eng_c + ast_c;

                    // ▼▼▼ 修正：transport_cost合算→distance×2×単価で再計算 / 「なし」除外 ▼▼▼
                    var day_transport = transports
                        .Where(t => g_ids.Contains(t.daily_report_id)
                                 && !string.IsNullOrWhiteSpace(t.vehicle)
                                 && t.vehicle != "なし")
                        .ToList();

                    decimal road_rate = parse_m(cfg, "road_cost_per_km", 50m);
                    decimal highway_rate = parse_m(cfg, "highway_cost_per_km", 100m);

                    decimal tra_c = 0;
                    double dist_total = 0;
                    foreach (var t in day_transport)
                    {
                        double round_trip = t.distance * 2;
                        // ▼▼▼ 追加：高速でも往復250km超は下道単価（50円/km）で計上 ▼▼▼
                        // 例：片道130km → 往復260km > 250km → 高速利用でも50円/km
                        decimal unit = (t.travel_method == "高速" && round_trip <= 250)
                            ? highway_rate
                            : road_rate;
                        tra_c += (decimal)round_trip * unit;
                        dist_total += round_trip;
                    }
                    tra_c = Math.Round(tra_c, 0);

                    // ▼▼▼ 修正：「なし」除外 + 上限2台 ▼▼▼
                    var registered_vehicles = (await conn.QueryAsync<string>(
                        "SELECT vehicle_name FROM vehicles WHERE is_active = 1")).ToHashSet();

                    // 「なし」「空白」はカウント対象外・上限2台
                    const int MAX_VEHICLES = 2;
                    int veh_count = Math.Min(
                        day_transport
                            .Where(t => !string.IsNullOrWhiteSpace(t.vehicle)
                                     && t.vehicle != "なし"
                                     && registered_vehicles.Contains(t.vehicle))
                            .Select(t => t.vehicle)
                            .Distinct()
                            .Count(),
                        MAX_VEHICLES);

                    // ▼▼▼ 修正：「なし」「空白」を機材名から除外 ▼▼▼
                    var day_equipment = equipments
                        .Where(e => g_ids.Contains(e.daily_report_id)
                                 && !string.IsNullOrWhiteSpace(e.equipment_name)
                                 && e.equipment_name != "なし")
                        .ToList();

                    // 機材名でグループ化し、合計使用時間が6h以上のもののみ対象
                    var equ_groups = day_equipment
                        .GroupBy(e => e.equipment_name)
                        .Where(eg => eg.Sum(e => e.hours) >= 6.0)
                        .ToList();

                    decimal equ_c = 0;
                    int equ_qty = equ_groups.Count;
                    var detail_parts = new List<string>();

                    foreach (var eg in equ_groups)
                    {
                        // ▼▼▼ 修正：equipment_ratesマスタを優先参照（旧形式対応） ▼▼▼
                        // daily_equipment.daily_rate=0（旧形式）の場合はマスタから取得
                        decimal item_rate = eg.First().daily_rate;
                        if (item_rate == 0 && equip_rate_map.TryGetValue(eg.Key, out decimal master_rate))
                            item_rate = master_rate;
                        equ_c += item_rate;

                        // ▼▼▼ 修正：0hのユーザーは使用者詳細から除外 ▼▼▼
                        var user_parts = eg
                            .GroupBy(e => e.employee_name)
                            .Where(ug => !string.IsNullOrWhiteSpace(ug.Key)
                                      && ug.Sum(u => u.hours) > 0)
                            .Select(ug => $"{ug.Key} {ug.Sum(u => u.hours):0.0}h")
                            .ToList();
                        detail_parts.Add($"{eg.Key}（{string.Join("・", user_parts)}）");
                    }

                    // 機材名（6h以上のもののみ・「・」区切り）
                    string equ_names = string.Join("・", equ_groups.Select(eg => eg.Key));

                    // 機材使用詳細（「AutoCAD（ズイ 8.0h・タイ 7.0h）」形式）
                    string equipment_detail = string.Join("　", detail_parts);

                    // 氏名（重複除去・「・」区切り）
                    string eng_names = string.Join("・",
                        engineers.Select(r => r.employee_name)
                                 .Where(n => !string.IsNullOrWhiteSpace(n))
                                 .Distinct());
                    string ast_names = string.Join("・",
                        assistants.Select(r => r.employee_name)
                                  .Where(n => !string.IsNullOrWhiteSpace(n))
                                  .Distinct());

                    // ▼▼▼ 追加：技師・助手の人数（重複除去後のカウント） ▼▼▼
                    int eng_count = engineers.Select(r => r.employee_name)
                                            .Where(n => !string.IsNullOrWhiteSpace(n))
                                            .Distinct().Count();
                    int ast_count = assistants.Select(r => r.employee_name)
                                             .Where(n => !string.IsNullOrWhiteSpace(n))
                                             .Distinct().Count();

                    // 作業内容（重複除去・「・」区切り、既存の「、」も統一変換）
                    string work = string.Join("・",
                        g.Select(r => r.detail)
                         .Where(d => !string.IsNullOrWhiteSpace(d))
                         .Select(d => d.Replace("、", "・"))
                         .Distinct());

                    records.Add(new cost_record
                    {
                        category_code = category_code,
                        record_date = g.Key,
                        fiscal_month = fiscal_month,
                        work_content = work,
                        engineer_names = eng_names,
                        engineer_hours = eng_h,
                        engineer_days = (double)eng_d,
                        engineer_cost = eng_c,
                        // ▼▼▼ 追加：技師・助手の人数 ▼▼▼
                        engineer_count = eng_count,
                        assistant_names = ast_names,
                        assistant_hours = ast_h,
                        assistant_days = (double)ast_d,
                        assistant_cost = ast_c,
                        assistant_count = ast_count,
                        personnel_cost = per_c,
                        transport_cost = Math.Round(tra_c, 0),
                        distance_total = Math.Round(dist_total, 1),
                        vehicle_count = veh_count,
                        // ▼▼▼ 追加：equipment_detail ▼▼▼
                        equipment_names = equ_names,
                        equipment_detail = equipment_detail,
                        equipment_cost = Math.Round(equ_c, 0),
                        equipment_quantity = equ_qty,
                        total_cost = Math.Round(per_c + tra_c + equ_c, 0),
                        updated_by = operator_name
                    });
                }

                // 5. 洗い替え（DELETE → INSERT をトランザクションで原子的に実行）
                using var tx = conn.BeginTransaction();
                try
                {
                    await conn.ExecuteAsync(@"
                        DELETE FROM cost_records
                        WHERE category_code = @cat
                          AND record_date BETWEEN @s AND @e",
                        new { cat = category_code, s = start_date, e = end_date }, tx);

                    foreach (var rec in records)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO cost_records (
                                category_code, record_date, fiscal_month, work_content,
                                engineer_names, engineer_hours, engineer_days, engineer_cost, engineer_count,
                                assistant_names, assistant_hours, assistant_days, assistant_cost, assistant_count,
                                personnel_cost, transport_cost, distance_total, vehicle_count,
                                equipment_names, equipment_detail, equipment_cost, equipment_quantity,
                                total_cost, updated_by
                            ) VALUES (
                                @category_code, @record_date, @fiscal_month, @work_content,
                                @engineer_names, @engineer_hours, @engineer_days, @engineer_cost, @engineer_count,
                                @assistant_names, @assistant_hours, @assistant_days, @assistant_cost, @assistant_count,
                                @personnel_cost, @transport_cost, @distance_total, @vehicle_count,
                                @equipment_names, @equipment_detail, @equipment_cost, @equipment_quantity,
                                @total_cost, @updated_by
                            )", rec, tx);
                    }

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }

                // 6. 操作ログ記録
                await conn.ExecuteAsync(@"
                    INSERT INTO operation_logs
                        (operator_name, operation_type, target_table, record_count, detail)
                    VALUES
                        (@op, '原価集計', 'cost_records', @cnt, @det)",
                    new
                    {
                        op = operator_name,
                        cnt = records.Count,
                        det = $"{category_code} / {start_date}〜{end_date}"
                    });

                return (records.Count, string.Empty);
            }
            catch (Exception ex)
            {
                // エラーログ記録を試みる（失敗しても握りつぶす）
                try
                {
                    // ▼▼▼ [Sprint 5D バグ修正] error_logsのカラム名を正しい仕様に修正 ▼▼▼
                    // 旧：operator_name, error_type, error_message, stack_trace
                    // 正：mac_address, user_name, error_type, source, message, stack_trace
                    await EA_CostManager.Services.ErrorLogService.log_caught_async(
                        $"CostAggregationService.aggregate_async（{category_code}）", ex);
                }
                catch { /* ログ失敗は無視 */ }

                return (0, ex.Message);
            }
        }

        // ---- プライベートユーティリティ ----

        /// <summary>
        /// 作業日からカレンダー月度文字列を計算する
        /// 1日〜末日のカレンダー月
        /// フォーマット："YYYY年M月度"（例: "2025年10月度"）
        /// </summary>
        private static string calc_fiscal_month(string date_str)
        {
            if (!DateTime.TryParse(date_str, out var dt))
                return "";

            return $"{dt.Year}年{dt.Month}月度";
        }

        private static decimal parse_m(Dictionary<string, string> dict, string key, decimal fallback)
        {
            if (dict.TryGetValue(key, out string? val) &&
                decimal.TryParse(val, out decimal result))
                return result;
            return fallback;
        }

        private static double parse_d(Dictionary<string, string> dict, string key, double fallback)
        {
            if (dict.TryGetValue(key, out string? val) &&
                double.TryParse(val, out double result))
                return result;
            return fallback;
        }
    }
}
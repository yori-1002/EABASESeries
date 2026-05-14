using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using EA_DailyReport.Data;
using EA_DailyReport.Models;

namespace EA_DailyReport.Services
{
    /// <summary>
    /// 日報の取得・保存サービス（v0.1.4 設計書 3 章）
    /// daily_reports + work_details の CRUD を担当
    ///
    /// 接続先：database_manager.create_connection()
    ///   → 現在の current_db_path（NAS or ローカル）に接続
    ///   ※ App.xaml.cs が起動時に switch_to_nas() で切り替えている
    /// </summary>
    public static class DailyReportService
    {
        // ────────────────────────────────────────────────
        // 取得
        // ────────────────────────────────────────────────

        /// <summary>
        /// 月度範囲（例：4/21〜5/20）の日報を DataGrid 表示用の行リストで取得する
        /// 1人1日に複数行（work_details の数だけ展開）
        /// 削除済み（is_deleted=1）の作業詳細は除外
        /// </summary>
        /// <param name="month_start">開始日（含む）</param>
        /// <param name="month_end">終了日（含む）</param>
        /// <param name="employee_filter">氏名フィルター（null/空で全員）</param>
        public static async Task<List<DailyReportRow>> get_rows_async(
            DateTime month_start,
            DateTime month_end,
            string? employee_filter = null)
        {
            var rows = new List<DailyReportRow>();

            using var conn = database_manager.create_connection();

            // 氏名フィルターの WHERE 句を動的に追加
            string where_employee = string.IsNullOrEmpty(employee_filter)
                ? ""
                : "AND dr.employee_name = @employee";

            // daily_reports と work_details を LEFT JOIN
            // work_details が無い日報も表示（出退勤だけ入力済みの状態）
            string sql = $@"
                SELECT
                    dr.id              AS daily_report_id,
                    dr.report_date     AS report_date,
                    dr.employee_id     AS employee_id,
                    dr.employee_name   AS employee_name,
                    dr.clock_in        AS clock_in,
                    dr.clock_out       AS clock_out,
                    dr.early_morning_min AS early_morning_min,
                    dr.overtime_min    AS overtime_min,
                    dr.late_night_min  AS late_night_min,
                    dr.start_location  AS start_location,
                    dr.end_location    AS end_location,
                    wd.id              AS work_detail_id,
                    wd.category_code   AS category_code,
                    wd.project_name    AS project_name,
                    wd.detail          AS detail,
                    wd.hours           AS hours,
                    wd.software        AS software,
                    wd.software_cost   AS software_cost,
                    wd.vehicle         AS vehicle,
                    wd.from_location   AS from_location,
                    wd.to_location     AS to_location,
                    wd.distance        AS distance,
                    wd.transport       AS transport,
                    wd.note            AS note,
                    wd.sort_order      AS sort_order
                FROM daily_reports dr
                LEFT JOIN work_details wd
                    ON wd.daily_report_id = dr.id AND wd.is_deleted = 0
                WHERE dr.report_date >= @start AND dr.report_date <= @end
                  {where_employee}
                ORDER BY dr.report_date, dr.employee_name, wd.sort_order, wd.id";

            var raw_rows = (await conn.QueryAsync(sql, new
            {
                start = month_start.ToString("yyyy-MM-dd"),
                end = month_end.ToString("yyyy-MM-dd"),
                employee = employee_filter ?? ""
            })).ToList();

            // 1人1日でグループ化し、最初の行だけ出退勤を表示する
            var grouped = raw_rows.GroupBy(r => new
            {
                date = (string)r.report_date,
                name = (string)r.employee_name
            });

            foreach (var group in grouped)
            {
                bool is_first = true;
                foreach (var raw in group)
                {
                    rows.Add(build_row(raw, is_first));
                    is_first = false;
                }
            }

            return rows;
        }

        /// <summary>
        /// 1レコードを DailyReportRow に変換する
        /// 1人1日の最初の行のみ出退勤を表示し、2行目以降は "-" にする
        /// </summary>
        private static DailyReportRow build_row(dynamic raw, bool is_first)
        {
            var row = new DailyReportRow
            {
                daily_report_id = safe_int(raw.daily_report_id),
                work_detail_id = safe_int(raw.work_detail_id),
                is_first_row_of_day = is_first,
                report_date_display = format_date_display(safe_str(raw.report_date)),
                employee_name = safe_str(raw.employee_name),
                job_type = "助手",  // ※ 推測：employees.job_type から取得すべきだが現状は固定

                // 出退勤情報：最初の行のみ表示、以降は "-"
                clock_in_display = is_first ? format_time(safe_str(raw.clock_in)) : "-",
                clock_out_display = is_first ? format_time(safe_str(raw.clock_out)) : "-",
                early_morning_display = is_first ? WorkTimeCalculator.format_minutes(safe_int(raw.early_morning_min)) : "-",
                overtime_display = is_first ? WorkTimeCalculator.format_minutes(safe_int(raw.overtime_min)) : "-",
                late_night_display = is_first ? WorkTimeCalculator.format_minutes(safe_int(raw.late_night_min)) : "-",
                start_location_display = is_first ? non_empty(safe_str(raw.start_location), "-") : "-",
                end_location_display = is_first ? non_empty(safe_str(raw.end_location), "-") : "-",

                // 作業詳細
                category_code = safe_str(raw.category_code),
                project_name = safe_str(raw.project_name),
                detail = safe_str(raw.detail),
                hours = safe_double(raw.hours),
                software = non_empty(safe_str(raw.software), "なし"),
                software_cost_display = format_cost(safe_int(raw.software_cost)),
                vehicle = non_empty(safe_str(raw.vehicle), "なし"),
                from_location = non_empty(safe_str(raw.from_location), "-"),
                to_location = non_empty(safe_str(raw.to_location), "-"),
                distance_display = format_distance(safe_double(raw.distance)),
                transport = non_empty(safe_str(raw.transport), "-"),
                note = safe_str(raw.note),
            };
            return row;
        }

        // ────────────────────────────────────────────────
        // 保存
        // ────────────────────────────────────────────────

        /// <summary>
        /// 日報を保存する（INSERT または UPDATE）
        /// daily_reports と work_details を 1 トランザクションで処理
        /// 保存後、sync_status に pending として登録（NAS 同期はバックグラウンド）
        /// </summary>
        /// <returns>保存された daily_reports.id</returns>
        public static async Task<int> save_async(DailyReport report, List<WorkDetail> details)
        {
            using var conn = database_manager.create_connection();
            using var tx = conn.BeginTransaction();

            try
            {
                int report_id;

                if (report.id == 0)
                {
                    // ─── 新規登録 ───
                    string sql_insert = @"
                        INSERT INTO daily_reports
                            (report_date, employee_id, employee_name,
                             clock_in, clock_out,
                             early_morning_min, overtime_min, late_night_min,
                             start_location, end_location,
                             entered_by, source_pc, source, gdrive_file_id,
                             created_at, updated_at)
                        VALUES
                            (@report_date, @employee_id, @employee_name,
                             @clock_in, @clock_out,
                             @early_morning_min, @overtime_min, @late_night_min,
                             @start_location, @end_location,
                             @entered_by, @source_pc, @source, @gdrive_file_id,
                             datetime('now','localtime'),
                             datetime('now','localtime'));
                        SELECT last_insert_rowid();";
                    report_id = (int)await conn.ExecuteScalarAsync<long>(
                        sql_insert, report, tx);
                }
                else
                {
                    // ─── 既存更新 ───
                    string sql_update = @"
                        UPDATE daily_reports SET
                            report_date       = @report_date,
                            employee_id       = @employee_id,
                            employee_name     = @employee_name,
                            clock_in          = @clock_in,
                            clock_out         = @clock_out,
                            early_morning_min = @early_morning_min,
                            overtime_min      = @overtime_min,
                            late_night_min    = @late_night_min,
                            start_location    = @start_location,
                            end_location      = @end_location,
                            updated_at        = datetime('now','localtime')
                        WHERE id = @id";
                    await conn.ExecuteAsync(sql_update, report, tx);
                    report_id = report.id;

                    // 既存 work_details を論理削除（再登録するため）
                    // 物理削除しないのは過去履歴を残すため（設計書 6 章）
                    await conn.ExecuteAsync(@"
                        UPDATE work_details
                        SET is_deleted = 1,
                            deleted_at = datetime('now','localtime'),
                            is_recalc_needed = 1
                        WHERE daily_report_id = @id AND is_deleted = 0",
                        new { id = report_id }, tx);
                }

                // ─── work_details を INSERT ───
                foreach (var d in details)
                {
                    d.daily_report_id = report_id;
                    d.is_recalc_needed = 1;  // CostManager 再集計対象としてマーク
                    string sql_d = @"
                        INSERT INTO work_details
                            (daily_report_id, category_code, project_name, detail, hours,
                             software, software_cost, vehicle,
                             from_location, to_location, distance, transport, note,
                             sort_order, is_deleted, is_recalc_needed,
                             created_at, updated_at)
                        VALUES
                            (@daily_report_id, @category_code, @project_name, @detail, @hours,
                             @software, @software_cost, @vehicle,
                             @from_location, @to_location, @distance, @transport, @note,
                             @sort_order, 0, 1,
                             datetime('now','localtime'),
                             datetime('now','localtime'))";
                    await conn.ExecuteAsync(sql_d, d, tx);
                }

                // ─── sync_status に pending として登録 ───
                // NasSyncService がバックグラウンドで NAS に反映する
                await conn.ExecuteAsync(@"
                    INSERT OR REPLACE INTO sync_status
                        (table_name, record_id, status, created_at)
                    VALUES
                        ('daily_reports', @id, 'pending',
                         datetime('now','localtime'))",
                    new { id = report_id }, tx);

                tx.Commit();
                return report_id;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        // ────────────────────────────────────────────────
        // デモ用シードデータ投入
        // ────────────────────────────────────────────────

        /// <summary>
        /// DB が空の場合のみ、デモ用ダミーデータを投入する
        /// App.xaml.cs の起動フローから呼び出す想定
        /// 設計書 10.7（Excel 取込のバリデーション）の趣旨に沿いつつ、
        /// 今回はハードコードで簡易データを投入
        ///
        /// ▼ v0.1.5 動作確認：
        /// NAS 接続が成功して NasDataFetchService がデータを投入した場合、
        /// daily_reports は 0 件ではなくなるため、本メソッドは何もせず終了する。
        /// → NAS 未接続 or 取得失敗時のみ自動的に発動する（条件変更不要）
        /// </summary>
        public static async Task seed_demo_data_if_empty_async()
        {
            using var conn = database_manager.create_connection();

            // 既存データがあれば何もしない（冪等性）
            int count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM daily_reports");
            if (count > 0) return;

            // ─── ダミーデータ定義 ───
            // 設計書サンプルとExcel現行データから抽出
            var employees = new (string name, string job_type)[] {
                ("金本", "助手"), ("内藤", "助手"), ("仲", "助手"),
                ("ズイ",  "助手"), ("バタイ", "助手"), ("人見", "助手"),
                ("竹本", "技師"), ("村上", "助手"), ("天野", "助手"),
            };

            var projects = new (string code, string name)[] {
                ("EA45",  "北陸"),       ("EA94",  "なにわ筋"),
                ("EA105", "鳥居水門"),   ("EA156", "R7寺田拡幅"),
                ("研修",  "研修"),       ("事務",  "事務"),
                ("営業",  "営業"),       ("開発",  "開発関係"),
            };

            var detail_samples = new[] {
                "資料作成",         "現地確認",     "解析データ確認",
                "メール対応",       "打ち合わせ",   "モデル作成",
                "報告書作成",       "システム開発",
            };

            var softwares = new (string name, int cost)[] {
                ("なし",       0),    ("AUTODESK",    1750),
                ("Trend-Point", 600), ("武蔵",         480),
            };

            // 固定 seed で再現性を確保（デモ毎に同じデータ）
            var rng = new Random(42);
            var start_date = new DateTime(2026, 4, 21);
            int eid = 1;

            // ─── 平日 10 営業日分を投入 ───
            for (int day_offset = 0; day_offset < 14 && eid < 100; day_offset++)
            {
                var date = start_date.AddDays(day_offset);
                if (date.DayOfWeek == DayOfWeek.Saturday ||
                    date.DayOfWeek == DayOfWeek.Sunday)
                    continue;

                foreach (var emp in employees)
                {
                    string clock_in = "08:30";
                    // 3 日に 1 回くらい残業（30 分）
                    string clock_out = (day_offset % 3 == 0) ? "18:00" : "17:30";

                    var ti = TimeSpan.Parse(clock_in);
                    var to = TimeSpan.Parse(clock_out);
                    var calc = WorkTimeCalculator.calculate(ti, to);

                    var dr = new DailyReport
                    {
                        report_date = date.ToString("yyyy-MM-dd"),
                        employee_id = eid++,
                        employee_name = emp.name,
                        clock_in = clock_in,
                        clock_out = clock_out,
                        early_morning_min = calc.early_morning_min,
                        overtime_min = calc.overtime_min,
                        late_night_min = calc.late_night_min,
                        start_location = "本社",
                        end_location = "本社",
                        entered_by = 0,
                        source_pc = "DEMO",
                        source = "desktop",
                    };

                    // 1〜3 個の作業詳細を生成（合計 8h になるよう調整）
                    int num_details = rng.Next(1, 4);
                    var wds = new List<WorkDetail>();
                    double remaining = 8.0;
                    for (int i = 0; i < num_details; i++)
                    {
                        var (proj_code, proj_name) = projects[rng.Next(projects.Length)];
                        var (sw_name, sw_cost) = softwares[rng.Next(softwares.Length)];

                        double h = (i == num_details - 1)
                            ? remaining
                            : Math.Round(
                                remaining / (num_details - i)
                                * (0.5 + rng.NextDouble() * 0.5), 1);
                        if (h > remaining) h = remaining;
                        if (h <= 0) h = 0.5;
                        remaining = Math.Max(0, remaining - h);

                        wds.Add(new WorkDetail
                        {
                            category_code = proj_code,
                            project_name = proj_name,
                            detail = detail_samples[rng.Next(detail_samples.Length)],
                            hours = h,
                            software = sw_name,
                            software_cost = sw_cost,
                            vehicle = "なし",
                            from_location = "-",
                            to_location = "-",
                            distance = 0,
                            transport = "-",
                            note = "",
                            sort_order = i,
                        });
                    }

                    await save_async(dr, wds);
                }
            }
        }

        // ────────────────────────────────────────────────
        // 表示整形ユーティリティ
        // ────────────────────────────────────────────────

        private static string format_date_display(string yyyymmdd)
        {
            if (DateTime.TryParse(yyyymmdd, out var dt))
                return dt.ToString("MM/dd");
            return yyyymmdd;
        }

        private static string format_time(string? hhmm)
        {
            if (string.IsNullOrWhiteSpace(hhmm) || hhmm == "-") return "-";
            return hhmm;
        }

        private static string format_cost(int cost)
        {
            if (cost <= 0) return "-";
            return cost.ToString("N0");
        }

        private static string format_distance(double dist)
        {
            if (dist <= 0) return "-";
            return dist.ToString("0.#");
        }

        // dynamic からの安全変換（NULL や long → int 変換に対応）
        private static int safe_int(dynamic? v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }

        private static double safe_double(dynamic? v)
        {
            if (v == null) return 0;
            try { return Convert.ToDouble(v); } catch { return 0; }
        }

        private static string safe_str(dynamic? v)
        {
            if (v == null) return "";
            return v.ToString() ?? "";
        }

        private static string non_empty(string s, string fallback)
        {
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }
    }
}
// ============================================================
// Services/NasDataFetchService.cs
// EA_DailyReport の NAS → ローカル取得サービス（v0.1.5 新設）
//
// 役割：
//   ・起動時 + 「最新を取得」ボタンから呼ばれる
//   ・NAS DB（CostManager 形式）から日報・マスタを取得し、
//     ローカル DB（v0.1.4 形式）に変換コピーする
//
// スキーマ変換の方針：
//   NAS（CostManager）：daily_reports は「1作業=1行」（複数行可）
//                       daily_equipment / daily_transport は子テーブル
//   ローカル（v0.1.4）：daily_reports は「1人×1日=1行」（出退勤情報）
//                       work_details に作業行を別テーブルで保持
//
//   → NAS の各 daily_reports.id を ローカル work_details.nas_source_id に保存
//      ローカル daily_reports は (date, employee_name) でグルーピングして再構築
//
// 差分同期ルール（設計書 4.3 ＋ yori 確定方針）：
//   ・NAS の updated_at > ローカル → NAS で上書き
//   ・ローカル sync_status='pending'（未同期）→ 絶対スキップ（消えない）
//   ・NAS から消えたレコード → ローカルに残す（is_deleted無いため検出不可）
//   ・親 daily_reports が更新対象 → 子（equipment/transport）も全件再取得
//     （CostManager は Excel 取込型のため親と子は常に同時更新される前提）
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using EA_DailyReport.Data;
using Microsoft.Data.Sqlite;

namespace EA_DailyReport.Services
{
    public static class NasDataFetchService
    {
        // ────────────────────────────────────────────────
        // 公開エントリポイント
        // ────────────────────────────────────────────────

        /// <summary>
        /// 起動時用：マスタ + 日報を一括取得する
        /// App.xaml.cs の起動フロー⑥から呼び出す想定
        /// NAS 接続失敗時は例外を投げず false を返す（ローカル DB だけで起動継続）
        /// </summary>
        /// <param name="nas_path">NAS DB のフルパス</param>
        /// <returns>取得成功時 true・NAS未接続/失敗時 false</returns>
        public static async Task<bool> fetch_all_on_startup_async(string nas_path)
        {
            // NAS パス未指定 or NAS フォルダ未到達なら何もしない
            if (string.IsNullOrWhiteSpace(nas_path)) return false;
            if (!File.Exists(nas_path)) return false;

            try
            {
                // マスタ → 日報の順に取得（マスタが先にローカルにある方が後続処理が安全）
                await fetch_masters_async(nas_path);
                await fetch_daily_reports_async(nas_path);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[NasDataFetchService] 起動時取得失敗（ローカルDBのみで継続）: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 「最新を取得」ボタン用：日報のみ差分取得する
        /// マスタは起動時のみ取得する方針（v0.1.5 yori確定）
        /// </summary>
        /// <returns>取得件数（0件含む）。失敗時は -1</returns>
        public static async Task<int> fetch_latest_daily_reports_async(string nas_path)
        {
            if (string.IsNullOrWhiteSpace(nas_path)) return -1;
            if (!File.Exists(nas_path)) return -1;

            try
            {
                return await fetch_daily_reports_async(nas_path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[NasDataFetchService] 最新取得失敗: {ex.Message}");
                return -1;
            }
        }

        // ────────────────────────────────────────────────
        // マスタ取得（employees / projects）
        // ────────────────────────────────────────────────

        /// <summary>
        /// employees / projects マスタを NAS から取得してローカルに反映する
        /// updated_at 比較で差分のみ更新（CostManager側スキーマには両方 updated_at あり）
        /// </summary>
        private static async Task fetch_masters_async(string nas_path)
        {
            using var nas_conn = database_manager.create_nas_connection(nas_path);
            using var local_conn = database_manager.create_connection();

            // ─── employees マスタ取得 ───
            var nas_employees = (await nas_conn.QueryAsync<NasEmployeeRow>(@"
                SELECT id, employee_name, job_type, daily_rate,
                       is_active, created_at, updated_at, updated_by
                FROM employees
            ")).ToList();

            // ローカル既存の employees を id でマップ
            var local_emp_map = (await local_conn.QueryAsync<(int id, string updated_at)>(
                "SELECT id, updated_at FROM employees"
            )).ToDictionary(r => r.id, r => r.updated_at ?? "");

            int emp_inserted = 0, emp_updated = 0;
            foreach (var emp in nas_employees)
            {
                if (local_emp_map.TryGetValue(emp.id, out var local_updated))
                {
                    // ローカルの updated_at が NAS と同じ or 新しいならスキップ
                    if (string.CompareOrdinal(local_updated, emp.updated_at) >= 0)
                        continue;

                    // NAS の方が新しい → UPDATE
                    await local_conn.ExecuteAsync(@"
                        UPDATE employees SET
                            employee_name = @employee_name,
                            job_type      = @job_type,
                            daily_rate    = @daily_rate,
                            is_active     = @is_active,
                            updated_at    = @updated_at,
                            updated_by    = @updated_by
                        WHERE id = @id", emp);
                    emp_updated++;
                }
                else
                {
                    // ローカルに無い → INSERT（明示的に id を指定）
                    await local_conn.ExecuteAsync(@"
                        INSERT INTO employees
                            (id, employee_name, job_type, daily_rate,
                             is_active, created_at, updated_at, updated_by)
                        VALUES
                            (@id, @employee_name, @job_type, @daily_rate,
                             @is_active, @created_at, @updated_at, @updated_by)", emp);
                    emp_inserted++;
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[NasDataFetchService] employees: {emp_inserted}件追加・{emp_updated}件更新");

            // ─── projects マスタ取得 ───
            // CostManager 側のスキーマに合わせて取得（attribute / tab_color / sort_order 含む）
            var nas_projects = (await nas_conn.QueryAsync<NasProjectRow>(@"
                SELECT id, category_code, site_name, company_name, detail,
                       attribute, tab_color, is_active, sort_order,
                       created_at, updated_at, updated_by
                FROM projects
            ")).ToList();

            var local_proj_map = (await local_conn.QueryAsync<(int id, string updated_at)>(
                "SELECT id, updated_at FROM projects"
            )).ToDictionary(r => r.id, r => r.updated_at ?? "");

            int proj_inserted = 0, proj_updated = 0;
            foreach (var p in nas_projects)
            {
                if (local_proj_map.TryGetValue(p.id, out var local_updated))
                {
                    if (string.CompareOrdinal(local_updated, p.updated_at) >= 0)
                        continue;

                    await local_conn.ExecuteAsync(@"
                        UPDATE projects SET
                            category_code = @category_code,
                            site_name     = @site_name,
                            company_name  = @company_name,
                            detail        = @detail,
                            attribute     = @attribute,
                            tab_color     = @tab_color,
                            is_active     = @is_active,
                            sort_order    = @sort_order,
                            updated_at    = @updated_at,
                            updated_by    = @updated_by
                        WHERE id = @id", p);
                    proj_updated++;
                }
                else
                {
                    await local_conn.ExecuteAsync(@"
                        INSERT INTO projects
                            (id, category_code, site_name, company_name, detail,
                             attribute, tab_color, is_active, sort_order,
                             created_at, updated_at, updated_by)
                        VALUES
                            (@id, @category_code, @site_name, @company_name, @detail,
                             @attribute, @tab_color, @is_active, @sort_order,
                             @created_at, @updated_at, @updated_by)", p);
                    proj_inserted++;
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[NasDataFetchService] projects: {proj_inserted}件追加・{proj_updated}件更新");

            // ▼ 追加：v0.1.7 pc_users マスタ取得（PC × ユーザー認証情報）
            // CostManager と EA_DailyReport で共有するため software 区別カラムは不要
            // App.xaml.cs の check_user_async() がローカル DB の pc_users を検索するため、
            // ここで NAS から取得しておかないと「ゲスト」になる
            try
            {
                var nas_pc_users = (await nas_conn.QueryAsync<NasPcUserRow>(@"
                    SELECT id, mac_address, user_name, employee_id, is_admin, pc_name,
                           created_at, updated_at
                    FROM pc_users
                ")).ToList();

                var local_pcu_map = (await local_conn.QueryAsync<(int id, string updated_at)>(
                    "SELECT id, updated_at FROM pc_users"
                )).ToDictionary(r => r.id, r => r.updated_at ?? "");

                int pcu_inserted = 0, pcu_updated = 0;
                foreach (var pu in nas_pc_users)
                {
                    if (local_pcu_map.TryGetValue(pu.id, out var local_updated))
                    {
                        // ローカルが新しい or 同じならスキップ
                        if (string.CompareOrdinal(local_updated, pu.updated_at ?? "") >= 0)
                            continue;

                        await local_conn.ExecuteAsync(@"
                            UPDATE pc_users SET
                                mac_address = @mac_address,
                                user_name = @user_name,
                                employee_id = @employee_id,
                                is_admin = @is_admin,
                                pc_name = @pc_name,
                                updated_at = @updated_at
                            WHERE id = @id", pu);
                        pcu_updated++;
                    }
                    else
                    {
                        await local_conn.ExecuteAsync(@"
                            INSERT INTO pc_users
                                (id, mac_address, user_name, employee_id, is_admin, pc_name,
                                 created_at, updated_at)
                            VALUES
                                (@id, @mac_address, @user_name, @employee_id, @is_admin, @pc_name,
                                 @created_at, @updated_at)", pu);
                        pcu_inserted++;
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[NasDataFetchService] pc_users: {pcu_inserted}件追加・{pcu_updated}件更新");
            }
            catch (Exception ex_pcu)
            {
                // pc_users 取得失敗してもアプリは続行（ローカルキャッシュで動作可能）
                System.Diagnostics.Debug.WriteLine(
                    $"[NasDataFetchService] pc_users 取得失敗（無視）: {ex_pcu.Message}");
            }
        }

        // ────────────────────────────────────────────────
        // 日報取得（daily_reports + daily_equipment + daily_transport）
        // ────────────────────────────────────────────────

        /// <summary>
        /// NAS の日報を取得してローカルに変換コピーする
        /// 戻り値：実際にローカルに反映した件数（INSERT + UPDATE 合計）
        /// </summary>
        private static async Task<int> fetch_daily_reports_async(string nas_path)
        {
            using var nas_conn = database_manager.create_nas_connection(nas_path);
            using var local_conn = database_manager.create_connection();

            // ─── ① ローカルの最終取得時刻を取得 ───
            // app_settings.last_sync_at に前回取得時の最大 updated_at が保存されている
            // 初回起動時は空文字 → 全件取得対象になる
            string last_sync_at = await local_conn.ExecuteScalarAsync<string>(
                "SELECT value FROM app_settings WHERE key='last_sync_at'") ?? "";

            // ─── ② NAS から差分取得（updated_at > 最終取得時刻 のレコード）+ 子テーブル JOIN ───
            // CostManager の daily_reports + daily_equipment + daily_transport を 1 クエリで取得
            // 各 NAS daily_reports に対して、daily_equipment / daily_transport は最大1件のみ想定
            //   （CostManager の運用：1作業1行に対して機材・移動はそれぞれ1件）
            //   ※もし複数件あった場合は最初の1件だけ採用される（実害なし）
            var nas_rows = (await nas_conn.QueryAsync<NasDailyReportRow>(@"
                SELECT
                    dr.id              AS id,
                    dr.report_date     AS report_date,
                    dr.employee_name   AS employee_name,
                    dr.job_type        AS job_type,
                    dr.category_code   AS category_code,
                    dr.project_name    AS project_name,
                    dr.detail          AS detail,
                    dr.hours           AS hours,
                    dr.clock_in        AS clock_in,
                    dr.clock_out       AS clock_out,
                    dr.overtime        AS overtime,
                    dr.start_location  AS start_location,
                    dr.end_location    AS end_location,
                    dr.created_at      AS created_at,
                    dr.updated_at      AS updated_at,
                    de.equipment_name  AS equipment_name,
                    de.daily_rate      AS equipment_rate,
                    dt.vehicle         AS vehicle,
                    dt.departure       AS departure,
                    dt.arrival         AS arrival,
                    dt.distance        AS distance,
                    dt.travel_method   AS travel_method
                FROM daily_reports dr
                LEFT JOIN daily_equipment de ON de.daily_report_id = dr.id
                LEFT JOIN daily_transport dt ON dt.daily_report_id = dr.id
                WHERE dr.updated_at > @last_sync_at
                ORDER BY dr.report_date, dr.employee_name, dr.id
            ", new { last_sync_at })).ToList();

            if (nas_rows.Count == 0) return 0;

            // ─── ③ ローカル work_details の nas_source_id → ローカル状態のマップを作成 ───
            // 「この NAS レコードがローカルに既にあるか」と「ローカルでの状態」を判定するため
            var local_state_map = (await local_conn.QueryAsync<LocalWorkDetailState>(@"
                SELECT
                    wd.nas_source_id AS nas_source_id,
                    wd.id            AS work_detail_id,
                    wd.daily_report_id AS daily_report_id,
                    wd.updated_at    AS updated_at,
                    COALESCE(ss.status, '') AS sync_status
                FROM work_details wd
                LEFT JOIN sync_status ss
                    ON ss.table_name = 'daily_reports'
                   AND ss.record_id  = wd.daily_report_id
                WHERE wd.nas_source_id > 0
                  AND wd.is_deleted = 0
            ")).ToDictionary(r => r.nas_source_id, r => r);

            // ─── ④ 各 NAS レコードを処理 ───
            // (date, employee_name) で親 daily_reports をグルーピングする必要がある
            // → 同じ親に属する作業を集めてから一気に処理する

            // 親グループ単位で処理（date + employee_name）
            var grouped = nas_rows.GroupBy(r => new
            {
                date = r.report_date ?? "",
                name = r.employee_name ?? ""
            });

            int affected = 0;
            string max_updated_at = last_sync_at;  // 今回取得分の最大値を記録

            using var tx = local_conn.BeginTransaction();
            try
            {
                foreach (var group in grouped)
                {
                    // この (date, employee_name) のグループ内で：
                    //   1. 全 NAS レコードについて「ローカルにある & スキップすべきか」を判定
                    //   2. スキップしないものだけ処理する
                    var to_process = new List<NasDailyReportRow>();
                    bool any_pending_in_group = false;

                    foreach (var nas_r in group)
                    {
                        // 今回取得分の最大 updated_at を更新（スキップ判定とは別に常に記録）
                        if (string.CompareOrdinal(nas_r.updated_at, max_updated_at) > 0)
                            max_updated_at = nas_r.updated_at;

                        if (local_state_map.TryGetValue(nas_r.id, out var local_state))
                        {
                            // ローカルが pending（未同期）→ 絶対スキップ
                            if (local_state.sync_status == "pending")
                            {
                                any_pending_in_group = true;
                                continue;
                            }
                            // ローカルが editing（編集中）→ スキップ（自分が編集中のものを上書きしない）
                            if (local_state.sync_status == "editing")
                            {
                                any_pending_in_group = true;
                                continue;
                            }
                            // ローカルの updated_at >= NAS → スキップ（ローカル優先）
                            if (string.CompareOrdinal(
                                    local_state.updated_at ?? "",
                                    nas_r.updated_at) >= 0)
                                continue;
                        }
                        // NAS の方が新しい or ローカルに無い → 処理対象
                        to_process.Add(nas_r);
                    }

                    if (to_process.Count == 0) continue;

                    // ─── このグループのローカル親 daily_reports を取得 or 新規作成 ───
                    // 1人1日に対するローカルの daily_reports は1件だけある想定（v0.1.4 構造）
                    int local_daily_report_id;
                    var existing_parent = await local_conn.QueryFirstOrDefaultAsync<(int id, string updated_at)>(
                        @"SELECT id, updated_at FROM daily_reports
                          WHERE report_date = @date AND employee_name = @name
                          LIMIT 1",
                        new { date = group.Key.date, name = group.Key.name }, tx);

                    // グループ内で出退勤情報を持つ最初の行を抽出（CostManager は最初の1行に出退勤）
                    var parent_src = group.FirstOrDefault(
                        r => !string.IsNullOrWhiteSpace(r.clock_in)
                          && !string.IsNullOrWhiteSpace(r.clock_out));

                    string clock_in = parent_src?.clock_in ?? "";
                    string clock_out = parent_src?.clock_out ?? "";
                    string start_location = parent_src?.start_location ?? "";
                    string end_location = parent_src?.end_location ?? "";

                    // overtime（CostManager は HH:mm 文字列）→ 分に変換
                    int overtime_min = parse_overtime_text_to_min(parent_src?.overtime);

                    // 早朝・深夜は CostManager に情報無し → 出退勤から自動算出
                    int early_min = 0, late_min = 0;
                    if (!string.IsNullOrWhiteSpace(clock_in)
                     && !string.IsNullOrWhiteSpace(clock_out))
                    {
                        var ti = WorkTimeCalculator.parse_time(clock_in);
                        var to = WorkTimeCalculator.parse_time(clock_out);
                        if (ti != null && to != null)
                        {
                            var calc = WorkTimeCalculator.calculate(ti.Value, to.Value);
                            early_min = calc.early_morning_min;
                            late_min = calc.late_night_min;
                            // overtime は CostManager 側を優先（既に入力者の意図が反映されている）
                            // ただし CostManager 値が 0 なら自動算出値を使う
                            if (overtime_min == 0) overtime_min = calc.overtime_min;
                        }
                    }

                    if (existing_parent.id > 0)
                    {
                        // 既存親を更新（出退勤情報・updated_at）
                        // ※ pending な子がグループに居る場合でも、親の出退勤は NAS 側で更新する
                        //   （子のスキップは独立。親の出退勤は最新性を優先）
                        local_daily_report_id = existing_parent.id;
                        await local_conn.ExecuteAsync(@"
                            UPDATE daily_reports SET
                                clock_in          = @clock_in,
                                clock_out         = @clock_out,
                                early_morning_min = @early_min,
                                overtime_min      = @overtime_min,
                                late_night_min    = @late_min,
                                start_location    = @start_location,
                                end_location      = @end_location,
                                source            = 'nas_fetch',
                                updated_at        = @updated_at
                            WHERE id = @id",
                            new
                            {
                                id = local_daily_report_id,
                                clock_in,
                                clock_out,
                                early_min,
                                overtime_min,
                                late_min,
                                start_location,
                                end_location,
                                updated_at = parent_src?.updated_at ?? group.First().updated_at,
                            }, tx);
                    }
                    else
                    {
                        // 新規親を作成
                        local_daily_report_id = (int)await local_conn.ExecuteScalarAsync<long>(@"
                            INSERT INTO daily_reports
                                (report_date, employee_id, employee_name,
                                 clock_in, clock_out,
                                 early_morning_min, overtime_min, late_night_min,
                                 start_location, end_location,
                                 entered_by, source_pc, source, gdrive_file_id,
                                 created_at, updated_at)
                            VALUES
                                (@report_date, 0, @employee_name,
                                 @clock_in, @clock_out,
                                 @early_min, @overtime_min, @late_min,
                                 @start_location, @end_location,
                                 0, 'NAS', 'nas_fetch', '',
                                 @created_at, @updated_at);
                            SELECT last_insert_rowid();",
                            new
                            {
                                report_date = group.Key.date,
                                employee_name = group.Key.name,
                                clock_in,
                                clock_out,
                                early_min,
                                overtime_min,
                                late_min,
                                start_location,
                                end_location,
                                created_at = group.First().created_at ?? "",
                                updated_at = parent_src?.updated_at ?? group.First().updated_at,
                            }, tx);
                    }

                    // ─── このグループの work_details を全件再構築 ───
                    // CostManager は Excel 取込型のため、子テーブル（equipment/transport）は
                    // 親が更新されたら全件入れ替わっている。ローカル側も同じ運用で再構築する。
                    //
                    // ただし pending/editing な子がグループに居た場合は、
                    // それら以外のみ削除（pending/editing は保持）→ 後段で NAS 分を追加 INSERT
                    if (any_pending_in_group)
                    {
                        // pending/editing 以外（synced/'') の nas由来 work_details を物理削除
                        // pending な子は nas_source_id を持たない（自分入力分）か、別レコード扱い
                        await local_conn.ExecuteAsync(@"
                            DELETE FROM work_details
                            WHERE daily_report_id = @id
                              AND nas_source_id > 0
                              AND id NOT IN (
                                SELECT wd.id FROM work_details wd
                                LEFT JOIN sync_status ss
                                    ON ss.table_name = 'daily_reports'
                                   AND ss.record_id  = wd.daily_report_id
                                WHERE wd.daily_report_id = @id
                                  AND ss.status IN ('pending', 'editing')
                              )",
                            new { id = local_daily_report_id }, tx);
                    }
                    else
                    {
                        // クリーンに全件削除して再構築
                        await local_conn.ExecuteAsync(
                            "DELETE FROM work_details WHERE daily_report_id = @id AND nas_source_id > 0",
                            new { id = local_daily_report_id }, tx);
                    }

                    // 処理対象の NAS レコードを順次 INSERT（sort_order は元の id 順を維持）
                    int sort_order = 0;
                    foreach (var nas_r in to_process)
                    {
                        await local_conn.ExecuteAsync(@"
                            INSERT INTO work_details
                                (daily_report_id, category_code, project_name, detail, hours,
                                 software, software_cost, vehicle,
                                 from_location, to_location, distance, transport, note,
                                 sort_order, is_deleted, is_recalc_needed,
                                 nas_source_id,
                                 created_at, updated_at)
                            VALUES
                                (@daily_report_id, @category_code, @project_name, @detail, @hours,
                                 @software, @software_cost, @vehicle,
                                 @from_location, @to_location, @distance, @transport, '',
                                 @sort_order, 0, 0,
                                 @nas_source_id,
                                 @created_at, @updated_at)",
                            new
                            {
                                daily_report_id = local_daily_report_id,
                                category_code = nas_r.category_code ?? "",
                                project_name = nas_r.project_name ?? "",
                                detail = nas_r.detail ?? "",
                                hours = nas_r.hours,
                                software = nas_r.equipment_name ?? "なし",
                                software_cost = (int)(nas_r.equipment_rate ?? 0),
                                vehicle = nas_r.vehicle ?? "なし",
                                from_location = nas_r.departure ?? "-",
                                to_location = nas_r.arrival ?? "-",
                                distance = nas_r.distance ?? 0,
                                transport = nas_r.travel_method ?? "-",
                                sort_order = sort_order++,
                                nas_source_id = nas_r.id,
                                created_at = nas_r.created_at ?? "",
                                updated_at = nas_r.updated_at ?? "",
                            }, tx);

                        affected++;
                    }

                    // 親 daily_reports の sync_status を 'synced' に設定
                    // （NAS 由来データは同期済み扱い。pending/editing が居た場合は別途）
                    if (!any_pending_in_group)
                    {
                        await local_conn.ExecuteAsync(@"
                            INSERT OR REPLACE INTO sync_status
                                (table_name, record_id, status, synced_at)
                            VALUES
                                ('daily_reports', @id, 'synced',
                                 datetime('now','localtime'))",
                            new { id = local_daily_report_id }, tx);
                    }
                }

                // ─── ⑤ last_sync_at を更新 ───
                // 今回取得分の最大 updated_at を保存。次回はこれより新しいレコードのみ取得する
                await local_conn.ExecuteAsync(@"
                    INSERT OR REPLACE INTO app_settings (key, value, updated_at)
                    VALUES ('last_sync_at', @v, datetime('now','localtime'))",
                    new { v = max_updated_at }, tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return affected;
        }

        // ────────────────────────────────────────────────
        // ユーティリティ
        // ────────────────────────────────────────────────

        /// <summary>
        /// CostManager の overtime（"HH:mm" or "00:00"）を分に変換する
        /// 不正値・空文字は 0 を返す
        ///
        /// 注：CostManager の overtime は実データの大部分が "00:00" のため、
        ///     残業時間を保持してるのか他用途なのかは要確認（今回は残業分とみなす）
        /// </summary>
        private static int parse_overtime_text_to_min(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            // "HH:mm" 形式
            var ts = WorkTimeCalculator.parse_time(text);
            if (ts != null) return (int)ts.Value.TotalMinutes;
            return 0;
        }

        // ────────────────────────────────────────────────
        // 内部 DTO（NAS DB のレコード受け皿）
        // ────────────────────────────────────────────────

        /// <summary>NAS employees レコード（CostManager スキーマ）</summary>
        private class NasEmployeeRow
        {
            public int id { get; set; }
            public string employee_name { get; set; } = "";
            public string job_type { get; set; } = "";
            public double daily_rate { get; set; }
            public int is_active { get; set; }
            public string created_at { get; set; } = "";
            public string updated_at { get; set; } = "";
            public string updated_by { get; set; } = "";
        }

        /// <summary>NAS projects レコード（CostManager スキーマ）</summary>
        private class NasProjectRow
        {
            public int id { get; set; }
            public string category_code { get; set; } = "";
            public string? site_name { get; set; }
            public string? company_name { get; set; }
            public string? detail { get; set; }
            public string attribute { get; set; } = "";
            public string tab_color { get; set; } = "";
            public int is_active { get; set; }
            public int sort_order { get; set; }
            public string created_at { get; set; } = "";
            public string updated_at { get; set; } = "";
            public string updated_by { get; set; } = "";
        }

        /// <summary>
        /// ▼ 追加：v0.1.7
        /// NAS pc_users レコード（PC × ユーザー認証マスタ）
        /// CostManager と EA_DailyReport で共有
        /// </summary>
        private class NasPcUserRow
        {
            public int id { get; set; }
            public string mac_address { get; set; } = "";
            public string user_name { get; set; } = "";
            public int employee_id { get; set; }
            public int is_admin { get; set; }
            public string pc_name { get; set; } = "";
            public string created_at { get; set; } = "";
            public string updated_at { get; set; } = "";
        }

        /// <summary>NAS daily_reports + daily_equipment + daily_transport の JOIN 結果</summary>
        private class NasDailyReportRow
        {
            public int id { get; set; }
            public string? report_date { get; set; }
            public string? employee_name { get; set; }
            public string? job_type { get; set; }
            public string? category_code { get; set; }
            public string? project_name { get; set; }
            public string? detail { get; set; }
            public double hours { get; set; }
            public string? clock_in { get; set; }
            public string? clock_out { get; set; }
            public string? overtime { get; set; }
            public string? start_location { get; set; }
            public string? end_location { get; set; }
            public string? created_at { get; set; }
            public string updated_at { get; set; } = "";
            // daily_equipment（LEFT JOIN）
            public string? equipment_name { get; set; }
            public double? equipment_rate { get; set; }
            // daily_transport（LEFT JOIN）
            public string? vehicle { get; set; }
            public string? departure { get; set; }
            public string? arrival { get; set; }
            public double? distance { get; set; }
            public string? travel_method { get; set; }
        }

        /// <summary>ローカル work_details の状態（差分判定用）</summary>
        private class LocalWorkDetailState
        {
            public int nas_source_id { get; set; }
            public int work_detail_id { get; set; }
            public int daily_report_id { get; set; }
            public string? updated_at { get; set; }
            public string sync_status { get; set; } = "";
        }
    }
}
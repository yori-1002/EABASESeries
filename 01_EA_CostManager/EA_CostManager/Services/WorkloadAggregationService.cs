using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EA_CostManager.Services
{
    /// <summary>
    /// ▼ 追加 [Sprint 7A]：工数表 集計サービス
    ///
    /// 【設計方針】
    /// ・原価・人日の計算ルールは filter_tab_view_model.load_task_mode_async
    ///  （業務単位集計（担当者別））と完全に同一にしている。
    ///    粒度   ＝ 日付 × 作業者 × 業務内容（detail「、」→「・」変換＋Trim）
    ///    人日   ＝ 合計時間 ÷ base_hours_per_day（小数4桁丸め）
    ///    人件費 ＝ Round(人日 × 単価, 0円)
    ///    機材費 ＝ グループ内で同一機材の合計使用6時間以上のみ計上（単価0はマスタ補完）
    ///    交通費 ＝ 往復距離 × 単価（「高速」かつ往復250km以下は高速単価、それ以外は下道単価）
    ///   → 工数表の総合計は業務単位集計タブの合計と必ず一致する。
    /// ・単価は複数キー参照。設定画面が保存し他の集計も読む engineer_daily_rate /
    ///   assistant_daily_rate を優先し、DB既定値の engineer_rate / assistant_rate は
    ///   互換フォールバックとする（[9A-fix4]。詳細は aggregate_async 内のコメント参照）。
    /// ・現場別単価（project_rates）は業務単位集計タブと同様、適用しない。
    /// ・区分結合（category_groups）は原価集計と同一の CategoryGroupService で解決する。
    /// ・区分/サブ分類への振り分けは workload_* テーブルのキーワード部分一致（正規化後）。
    ///   判定順 ＝ 区分の sort_order → キーワードの priority。最初にマッチした区分で確定。
    ///   どの区分にもマッチしない行は category_id = null（未分類）として返す。
    ///
    /// 【注意点（既存仕様の引き継ぎ）】
    /// ・機材の6時間判定は「1グループ（1人1業務）内」で行うため、日単位集計では計上される
    ///   機材が工数表では計上されない場合がある（業務単位集計タブと同じ挙動・仕様）。
    /// ・職種が技師/助手のどちらにも該当しない行は人件費0のまま
    ///   unknown_days（人区分不明）として人日を保持する（数字を黙って捨てない）。
    /// </summary>
    public class WorkloadAggregationService
    {
        // ============================================================
        // メイン：案件1件分の工数集計（分類済み業務行を返す）
        // ============================================================
        public async Task<workload_result> aggregate_async(int project_id)
        {
            var result = new workload_result();
            using var conn = database_manager.create_connection();

            // ---- 1. 案件情報 ----
            // ▼ 修正 [9A-fix2]：業務単位集計の設定先は2箇所あるため両方を見る
            //   ・全件タブに適用   → projects.agg_mode
            //   ・絞り込みタブに適用 → cost_filter_tabs.agg_mode
            //   （旧実装は projects.agg_mode のみを見ており、複製タブで業務単位に
            //     している運用では is_task_mode が誤って false になっていた）
            var proj = (await conn.QueryAsync<dynamic>(@"
                SELECT p.id, p.category_code, p.site_name, p.agg_mode,
                       (SELECT COUNT(*) FROM cost_filter_tabs f
                         WHERE f.project_id = p.id
                           AND f.agg_mode = 'task'
                           AND f.is_archived = 0) AS task_tab_count
                FROM projects p WHERE p.id = @id",
                new { id = project_id })).FirstOrDefault();
            if (proj == null)
            {
                result.error_message = "指定された案件が見つかりません。";
                return result;
            }
            result.category_code = (string?)proj.category_code ?? "";
            result.site_name = (string?)proj.site_name ?? "";
            result.is_task_mode =
                ((string?)proj.agg_mode ?? "") == "task" ||
                System.Convert.ToInt32(proj.task_tab_count ?? 0) > 0;
            if (string.IsNullOrWhiteSpace(result.category_code))
            {
                result.error_message = "案件に区分コードが設定されていません。";
                return result;
            }

            // ---- 2. 区分結合の解決（原価集計タブと同一のサービスを使用） ----
            var child_codes = await CategoryGroupService.get_child_codes_async(result.category_code);
            var all_codes = new List<string> { result.category_code };
            all_codes.AddRange(child_codes);

            // ---- 3. 分類マスタの読み込み ----
            result.categories = (await conn.QueryAsync<workload_category>(@"
                SELECT * FROM workload_categories
                WHERE project_id = @p AND is_active = 1
                ORDER BY sort_order, id", new { p = project_id })).ToList();

            var cat_keywords = (await conn.QueryAsync<workload_category_keyword>(@"
                SELECT k.* FROM workload_category_keywords k
                JOIN workload_categories c ON c.id = k.category_id
                WHERE c.project_id = @p AND c.is_active = 1
                ORDER BY k.priority, k.id", new { p = project_id })).ToList();

            result.subgroups = (await conn.QueryAsync<workload_subgroup>(@"
                SELECT * FROM workload_subgroups
                WHERE project_id = @p AND is_active = 1
                ORDER BY sort_order, id", new { p = project_id })).ToList();

            var sub_keywords = (await conn.QueryAsync<workload_subgroup_keyword>(@"
                SELECT k.* FROM workload_subgroup_keywords k
                JOIN workload_subgroups s ON s.id = k.subgroup_id
                WHERE s.project_id = @p AND s.is_active = 1
                ORDER BY k.priority, k.id", new { p = project_id })).ToList();

            // ▼ 追加 [Sprint 7D]：サブ分類の適用先（どの区分タブに出すか）
            result.subgroup_links = (await conn.QueryAsync<workload_subgroup_link>(@"
                SELECT l.* FROM workload_subgroup_links l
                JOIN workload_subgroups s ON s.id = l.subgroup_id
                WHERE s.project_id = @p AND s.is_active = 1", new { p = project_id })).ToList();

            var mode_rows = (await conn.QueryAsync<workload_subgroup_mode>(
                "SELECT * FROM workload_subgroup_modes WHERE project_id = @p",
                new { p = project_id })).ToList();

            // 区分ID → モード（行が無い区分は common）
            foreach (var c in result.categories)
            {
                result.mode_by_category[c.id] =
                    mode_rows.FirstOrDefault(m => m.category_id == c.id)?.mode
                    ?? workload_subgroup_mode.MODE_COMMON;
            }

            // ---- 4. 分類用の前処理（キーワードは正規化して保持） ----
            // 区分ID → 正規化済みキーワードリスト（priority順を維持）
            var kw_by_category = result.categories.ToDictionary(
                c => c.id,
                c => cat_keywords.Where(k => k.category_id == c.id)
                                 .Select(k => normalize(k.keyword))
                                 .Where(k => k.Length > 0)
                                 .ToList());

            // サブ分類ID → 正規化済みキーワードリスト（priority順を維持）
            var kw_by_subgroup = result.subgroups.ToDictionary(
                s => s.id,
                s => sub_keywords.Where(k => k.subgroup_id == s.id)
                                 .Select(k => normalize(k.keyword))
                                 .Where(k => k.Length > 0)
                                 .ToList());

            // 区分ID → その区分で使用するサブ分類セット（適用先リンク解決済み・sort_order順）
            // ▼ 修正 [Sprint 7D]：解決ルールを workload_result.get_subgroup_set に一本化した。
            //   以前はここと画面側で同じ判定を二重に書いていたため、
            //   振り分け結果と表示行がずれる余地があった。
            var subgroup_set_by_category = result.categories.ToDictionary(
                c => c.id,
                c => result.get_subgroup_set(c.id));

            // ---- 5. 集計用の設定・マスタ（load_task_mode_async と同一） ----
            var setting_rows = (await conn.QueryAsync<(string key, string value)>(
                "SELECT key, value FROM app_settings")).ToList();
            var setting_dict = setting_rows.ToDictionary(s => s.key, s => s.value);

            // ▼ 修正 [9A-fix4]：単価キーの優先順を逆にした（engineer_daily_rate を先に見る）
            //   旧実装は engineer_rate / assistant_rate を優先していたが、これは誤りだった。
            //   ・engineer_rate / assistant_rate
            //       … database_manager.initialize_database() の insert_default_setting
            //         （INSERT OR IGNORE）でDB作成時に一度だけ入る既定値。以後どこからも
            //         更新されない（設定画面も書かない・setting_keys 定数も未使用）＝死にキー。
            //   ・engineer_daily_rate / assistant_daily_rate
            //       … 設定画面（master_amount_view_model.save_async）が保存し、
            //         日単位集計（CostAggregationService）と業務単位集計
            //         （filter_tab_view_model.load_task_mode_async）が読む＝生きているキー。
            //   旧実装は死にキーを優先していたため、設定画面で単価を変更すると
            //   工数表だけが既定値（34800/28000）を使い続け、業務単位集計と合計がズレた。
            //   ※ 変更前の本番NAS DBには engineer_rate 行のみ存在し engineer_daily_rate 行が
            //     無かったため、両者がたまたま 34800 に一致して表面化していなかった
            //     （設定画面で単価を1回変更した時点で顕在化する潜在バグ）。
            //   旧キーは互換フォールバックとして残す（engineer_daily_rate 行が無いDBでも
            //   従来どおり engineer_rate → 既定値の順で解決できるようにするため）。
            decimal engineer_rate = get_rate_any(setting_dict, new[] { "engineer_daily_rate", "engineer_rate" }, 34800m);
            decimal assistant_rate = get_rate_any(setting_dict, new[] { "assistant_daily_rate", "assistant_rate" }, 28000m);
            double base_hours = (double)get_rate_any(setting_dict, new[] { "base_hours_per_day" }, 8m);
            if (base_hours <= 0) base_hours = 8; // ゼロ除算ガード
            decimal road_rate = get_rate_any(setting_dict, new[] { "road_cost_per_km" }, 50m);
            decimal highway_rate = get_rate_any(setting_dict, new[] { "highway_cost_per_km" }, 100m);

            // 機材日額マスタ（旧形式日報の daily_rate=0 を補完）
            var equip_rate_map = (await conn.QueryAsync<(string name, decimal rate)>(
                "SELECT equipment_name, daily_rate FROM equipment_rates WHERE is_active = 1"))
                .ToDictionary(r => r.name, r => r.rate, StringComparer.OrdinalIgnoreCase);

            // job_type が空/不明の場合に employees から補完するための辞書
            var emp_job_map = (await conn.QueryAsync<(string name, string job)>(
                "SELECT employee_name, job_type FROM employees WHERE job_type != '' AND job_type != '不明'"))
                .ToDictionary(e => e.name, e => e.job);

            // ---- 6. 対象日報＋明細の読み込み（load_task_mode_async と同一SQL） ----
            var reports = (await conn.QueryAsync<dynamic>(@"
                SELECT dr.id, dr.report_date, dr.employee_name, dr.job_type,
                       dr.category_code, dr.detail, dr.hours
                FROM daily_reports dr
                WHERE dr.category_code IN @codes
                  AND dr.category_code NOT IN ('有給','有休')
                ORDER BY dr.report_date",
                new { codes = all_codes })).ToList();

            var report_ids = reports.Select(r => (int)System.Convert.ToInt32(r.id)).ToList();

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

            // ---- 7. 粒度：日付 × 作業者 × 業務内容 でグループ化（同一粒度） ----
            var grouped = reports
                .GroupBy(r => (
                    date: (string)r.report_date,
                    emp: (string)(r.employee_name ?? ""),
                    task: ((string?)r.detail ?? "").Replace("、", "・").Trim()))
                .ToList();

            foreach (var g in grouped)
            {
                // 職種判定（空/不明は employees から補完）※既存と同一
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

                // 合計時間 → 人日（4桁丸め）※既存と同一（動的要素のSumは避けループで加算）
                double total_h = 0;
                foreach (var rr in g) total_h += (double)System.Convert.ToDouble(rr.hours ?? 0);
                decimal days = base_hours > 0 ? (decimal)(total_h / base_hours) : 0m;
                days = Math.Round(days, 4);

                decimal eng_cost = is_eng ? Math.Round(days * engineer_rate, 0) : 0m;
                decimal ast_cost = is_ast ? Math.Round(days * assistant_rate, 0) : 0m;

                var ids = g.Select(r => (int)System.Convert.ToInt32(r.id)).ToList();

                // 機材（このグループ内で同一機材6時間以上のみ計上）※既存と同一
                decimal equ_c = 0m;
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
                }

                // 交通（往復×単価／「高速」かつ往復250km以下は高速単価／「なし」除外）※既存と同一
                decimal tra_c = 0m;
                foreach (var id in ids)
                {
                    if (!trans_map.TryGetValue(id, out var tlist)) continue;
                    foreach (var t in tlist)
                    {
                        if (string.IsNullOrWhiteSpace(t.vehicle) || t.vehicle == "なし") continue;
                        double round = t.dist * 2;
                        decimal unit = (t.method == "高速" && round <= 250) ? highway_rate : road_rate;
                        tra_c += (decimal)round * unit;
                    }
                }
                tra_c = Math.Round(tra_c, 0);

                // ---- 8. 区分・サブ分類への振り分け ----
                // 分類判定は正規化した業務内容で行う（表示用の task は元の文字列を維持）
                string norm_task = normalize(g.Key.task);
                int? cat_id = classify_category(norm_task, result.categories, kw_by_category);
                int? sub_id = null;
                if (cat_id.HasValue)
                    sub_id = classify_subgroup(norm_task, subgroup_set_by_category[cat_id.Value], kw_by_subgroup);

                result.rows.Add(new workload_task_row
                {
                    record_date = g.Key.date,
                    employee_name = g.Key.emp,
                    job_type = job,
                    task = g.Key.task,
                    hours = total_h,
                    engineer_days = is_eng ? (double)days : 0,
                    assistant_days = is_ast ? (double)days : 0,
                    unknown_days = (!is_eng && !is_ast) ? (double)days : 0,
                    personnel_cost = eng_cost + ast_cost,
                    transport_cost = tra_c,
                    equipment_cost = Math.Round(equ_c, 0),
                    total_cost = Math.Round(eng_cost + ast_cost + tra_c + equ_c, 0),
                    category_id = cat_id,
                    subgroup_id = sub_id,
                });
            }

            result.rows = result.rows.OrderBy(r => r.record_date).ToList();
            return result;
        }

        // ============================================================
        // 分類判定
        // ============================================================

        /// <summary>
        /// 業務区分の判定。区分の sort_order 順（categories は取得時にソート済み）→
        /// キーワードの priority 順で評価し、最初にマッチした区分IDを返す。
        /// どの区分にもマッチしなければ null（未分類）。
        /// </summary>
        private static int? classify_category(
            string norm_task,
            List<workload_category> categories,
            Dictionary<int, List<string>> kw_by_category)
        {
            if (norm_task.Length == 0) return null;
            foreach (var cat in categories)
            {
                if (!kw_by_category.TryGetValue(cat.id, out var kws)) continue;
                foreach (var kw in kws)
                {
                    if (norm_task.Contains(kw, StringComparison.Ordinal))
                        return cat.id;
                }
            }
            return null;
        }

        /// <summary>
        /// サブ分類の判定。渡されたセット（モード解決済み）の sort_order 順 →
        /// キーワードの priority 順で評価し、最初にマッチしたサブ分類IDを返す。
        /// マッチしなければ null（区分内のサブ未分類）。セットが空（mode=none）なら常に null。
        /// </summary>
        private static int? classify_subgroup(
            string norm_task,
            List<workload_subgroup> subgroup_set,
            Dictionary<int, List<string>> kw_by_subgroup)
        {
            if (norm_task.Length == 0) return null;
            foreach (var sub in subgroup_set)
            {
                if (!kw_by_subgroup.TryGetValue(sub.id, out var kws)) continue;
                foreach (var kw in kws)
                {
                    if (norm_task.Contains(kw, StringComparison.Ordinal))
                        return sub.id;
                }
            }
            return null;
        }

        /// <summary>
        /// 分類判定用の文字列正規化。キーワード側・業務内容側の両方に適用する。
        /// ・全角英数記号（！〜～）→ 半角、全角スペース → 半角
        /// ・「、」→「・」（業務単位集計のグルーピングと同じ変換）
        /// ・前後の空白を除去、英字は小文字化（大小ゆらぎ対策）
        /// これにより「余呉川第2」の登録で「余呉川第２」（全角数字）にもマッチする。
        /// </summary>
        /// <summary>
        /// ▼ 追加 [Sprint 7C-1]：正規化処理の公開版
        /// 分類設定ダイアログの「該当件数プレビュー」から呼び出す。
        /// プレビューと実際の集計で判定基準がずれないよう、同一の normalize を共用する
        /// （ここを別実装にすると「プレビューでは当たるのに集計されない」といった不整合が起きる）。
        /// </summary>
        public static string normalize_public(string? s) => normalize(s);

        private static string normalize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                char c = ch;
                if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0); // 全角英数記号 → 半角
                else if (c == '　') c = ' ';                        // 全角スペース → 半角
                sb.Append(c);
            }
            return sb.ToString().Replace("、", "・").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// 複数キーを優先順に探索して単価を取得する。
        /// 先に見つかったキーの値を採用し、どれも無ければ fallback を返す。
        /// filter_tab_view_model（get_rate）・CostAggregationService（parse_m）は
        /// 単一キー＋既定値だが、本サービスは新旧キーの併存を吸収するため複数キー対応にしている。
        /// </summary>
        private static decimal get_rate_any(Dictionary<string, string> dict, string[] keys, decimal fallback)
        {
            foreach (var key in keys)
            {
                if (dict.TryGetValue(key, out string? val) && decimal.TryParse(val, out decimal d))
                    return d;
            }
            return fallback;
        }
    }
}
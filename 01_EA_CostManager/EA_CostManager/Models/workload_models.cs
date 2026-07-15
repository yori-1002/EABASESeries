using System.Collections.Generic;
using System.Linq; // ▼ 追加 [Sprint 7B]：get_subgroup_set で使用

namespace EA_CostManager.Models
{
    /// <summary>
    /// ▼ 追加 [Sprint 7A]：業務区分（案件ごと・工数表のタブ）
    /// workload_categories テーブルの1行に対応（Dapper自動マッピング）
    /// </summary>
    public class workload_category
    {
        public int id { get; set; }
        public int project_id { get; set; }
        public string name { get; set; } = "";
        /// <summary>タブ表示順 ＝ キーワード判定の評価順（小さいほど先に評価）</summary>
        public int sort_order { get; set; }
        public int is_active { get; set; } = 1;
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string updated_by { get; set; } = "";
    }

    /// <summary>
    /// ▼ 追加 [Sprint 7A]：業務区分の検出キーワード
    /// workload_category_keywords テーブルの1行に対応
    /// </summary>
    public class workload_category_keyword
    {
        public int id { get; set; }
        public int category_id { get; set; }
        /// <summary>daily_reports.detail（正規化後）への部分一致文字列</summary>
        public string keyword { get; set; } = "";
        /// <summary>区分内の評価順（小さいほど先に評価）</summary>
        public int priority { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string updated_by { get; set; } = "";
    }

    /// <summary>
    /// ▼ 追加 [Sprint 7A]：サブ分類（橋名など。区分の中をさらに分ける単位）
    /// workload_subgroups テーブルの1行に対応
    /// </summary>
    public class workload_subgroup
    {
        public int id { get; set; }
        public int project_id { get; set; }
        /// <summary>NULL=案件共通セット／値あり=その区分専用セット（mode=custom用）</summary>
        public int? category_id { get; set; }
        public string name { get; set; } = "";
        public int sort_order { get; set; }
        public int is_active { get; set; } = 1;
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string updated_by { get; set; } = "";
    }

    /// <summary>
    /// ▼ 追加 [Sprint 7A]：サブ分類の検出キーワード
    /// workload_subgroup_keywords テーブルの1行に対応
    /// </summary>
    public class workload_subgroup_keyword
    {
        public int id { get; set; }
        public int subgroup_id { get; set; }
        public string keyword { get; set; } = "";
        public int priority { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string updated_by { get; set; } = "";
    }

    /// <summary>
    /// ▼ 追加 [Sprint 7A]：区分ごとのサブ分類モード
    /// workload_subgroup_modes テーブルの1行に対応。
    /// (project_id, category_id) の行が存在しない区分は MODE_COMMON として扱う
    /// </summary>
    public class workload_subgroup_mode
    {
        /// <summary>案件共通のサブ分類セットを使用（既定）</summary>
        public const string MODE_COMMON = "common";
        /// <summary>この区分専用のサブ分類セットを使用（共通セットは無視）</summary>
        public const string MODE_CUSTOM = "custom";
        /// <summary>サブ分類で分けない（区分合計のみ表示）</summary>
        public const string MODE_NONE = "none";

        public int id { get; set; }
        public int project_id { get; set; }
        public int category_id { get; set; }
        public string mode { get; set; } = MODE_COMMON;
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string updated_by { get; set; } = "";
    }

    /// <summary>
    /// ▼ 追加 [Sprint 7A]：工数表の集計行
    /// 「日付 × 作業者 × 業務内容」の1業務行（業務単位集計タブと同じ粒度）に
    /// 分類結果（category_id / subgroup_id）を付与したもの。
    /// 金額・人日の算出ルールは load_task_mode_async と完全同一。
    /// </summary>
    public class workload_task_row
    {
        public string record_date { get; set; } = "";
        public string employee_name { get; set; } = "";
        /// <summary>職種（employees からの補完適用後）</summary>
        public string job_type { get; set; } = "";
        /// <summary>業務内容（detail を「、」→「・」変換＋Trim したもの）</summary>
        public string task { get; set; } = "";
        public double hours { get; set; }
        /// <summary>技師としての人日（職種に「技師」を含む場合のみ）</summary>
        public double engineer_days { get; set; }
        /// <summary>助手としての人日（職種に「助手」を含む場合のみ）</summary>
        public double assistant_days { get; set; }
        /// <summary>職種が技師/助手のどちらにも該当しない行の人日（人区分不明として見える化）</summary>
        public double unknown_days { get; set; }
        public decimal personnel_cost { get; set; }
        public decimal transport_cost { get; set; }
        public decimal equipment_cost { get; set; }
        /// <summary>合計原価（人件費＋交通費＋機材費・円丸め）</summary>
        public decimal total_cost { get; set; }
        /// <summary>振り分け先の業務区分ID。null＝未分類</summary>
        public int? category_id { get; set; }
        /// <summary>振り分け先のサブ分類ID。null＝サブ未分類（区分確定時のみ意味を持つ）</summary>
        public int? subgroup_id { get; set; }
    }

    /// <summary>
    /// ▼ 追加 [Sprint 7A]：工数表の集計結果（案件1件分）
    /// ViewModel はこの rows を category_id / subgroup_id で束ねて表を組み立てる
    /// </summary>
    public class workload_result
    {
        /// <summary>エラー時のメッセージ。空文字＝正常</summary>
        public string error_message { get; set; } = "";
        public string category_code { get; set; } = "";
        public string site_name { get; set; } = "";
        /// <summary>対象案件が業務単位集計（agg_mode='task'）か</summary>
        public bool is_task_mode { get; set; }
        /// <summary>業務区分（sort_order順・有効のみ）</summary>
        public List<workload_category> categories { get; set; } = new();
        /// <summary>サブ分類（共通・個別の両方を含む。有効のみ）</summary>
        public List<workload_subgroup> subgroups { get; set; } = new();
        /// <summary>区分ID → サブ分類モード（common/custom/none）。行が無い区分は common</summary>
        public Dictionary<int, string> mode_by_category { get; set; } = new();
        /// <summary>分類済みの全業務行（日付順）</summary>
        public List<workload_task_row> rows { get; set; } = new();

        // ▼ 追加 [Sprint 7B]：区分が使用するサブ分類セットを解決して返す
        //   モード common＝案件共通セット（category_id が NULL の行）
        //         custom＝その区分専用セット（category_id 一致の行）
        //         none  ＝空リスト（サブ分類で分けない）
        //   ※ WorkloadAggregationService の振り分け時と同一ルール。
        //     画面（ViewModel）が表の行を組み立てるときにも同じ解決を使うことで、
        //     振り分け結果と表示行のセットが必ず一致する。
        public List<workload_subgroup> get_subgroup_set(int category_id)
        {
            string mode = mode_by_category.TryGetValue(category_id, out var m)
                ? m : workload_subgroup_mode.MODE_COMMON;
            if (mode == workload_subgroup_mode.MODE_NONE)
                return new List<workload_subgroup>();
            if (mode == workload_subgroup_mode.MODE_CUSTOM)
                return subgroups.Where(s => s.category_id == category_id).ToList();
            return subgroups.Where(s => s.category_id == null).ToList();
        }
    }
}
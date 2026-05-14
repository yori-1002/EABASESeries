namespace EA_DailyReport.Models
{
    /// <summary>
    /// work_details テーブルのレコード（v0.1.4 設計書 3.2）
    /// daily_reports に紐づく作業行（1日報に複数行）
    /// is_deleted（論理削除）/ is_recalc_needed（CostManager再集計フラグ）で連携
    /// </summary>
    public class WorkDetail
    {
        /// <summary>主キー</summary>
        public int id { get; set; }

        /// <summary>親レコード ID（daily_reports.id）</summary>
        public int daily_report_id { get; set; }

        /// <summary>区分コード（EA45 / 営業 / 研修 等）</summary>
        public string category_code { get; set; } = "";

        /// <summary>業務名（北陸 / なにわ筋 等）</summary>
        public string project_name { get; set; } = "";

        /// <summary>作業詳細</summary>
        public string detail { get; set; } = "";

        /// <summary>作業時間（h）</summary>
        public double hours { get; set; }

        /// <summary>使用ソフト・機材名（AUTODESK / Trend-Point 等）</summary>
        public string software { get; set; } = "";

        /// <summary>ソフト・機材料金（円）</summary>
        public int software_cost { get; set; }

        /// <summary>使用車両（ステップワゴン / ハイエース 等）</summary>
        public string vehicle { get; set; } = "";

        /// <summary>移動：発</summary>
        public string from_location { get; set; } = "";

        /// <summary>移動：着</summary>
        public string to_location { get; set; } = "";

        /// <summary>移動距離（km）</summary>
        public double distance { get; set; }

        /// <summary>移動方法（下道 / 高速）</summary>
        public string transport { get; set; } = "";

        /// <summary>備考</summary>
        public string note { get; set; } = "";

        /// <summary>同一日報内での並び順</summary>
        public int sort_order { get; set; }

        /// <summary>論理削除フラグ（1=削除済み）</summary>
        public int is_deleted { get; set; }

        /// <summary>再集計フラグ（1=CostManager 再集計対象）</summary>
        public int is_recalc_needed { get; set; }

        /// <summary>論理削除日時</summary>
        public string deleted_at { get; set; } = "";

        /// <summary>レコード作成日時</summary>
        public string created_at { get; set; } = "";

        /// <summary>最終更新日時</summary>
        public string updated_at { get; set; } = "";

        /// <summary>
        /// ▼ 追加：v0.1.5
        /// NAS（CostManager）由来レコードの紐付けID
        /// CostManager の daily_reports.id（NASは1作業=1行構造のため）を保持する
        /// 用途：「最新を取得」時に同一レコードを判定し、updated_at 比較で同期する
        ///
        /// 値の意味：
        ///   0  = ローカルで新規作成された未同期データ
        ///   >0 = NASから取得済みのデータ（NAS側のIDを保持）
        ///
        /// UNIQUE制約は nas_source_id > 0 のときのみ適用（DailyReportMigration参照）
        /// </summary>
        public int nas_source_id { get; set; }
    }
}
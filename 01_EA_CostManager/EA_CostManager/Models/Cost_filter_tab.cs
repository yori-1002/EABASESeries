namespace EA_CostManager.Models
{
    /// <summary>
    /// 絞り込みタブ保存モデル（cost_filter_tabs テーブルのマッピング）
    /// </summary>
    public class cost_filter_tab
    {
        public int id { get; set; }

        /// <summary>紐づく現場ID（projects.id）</summary>
        public int project_id { get; set; }

        /// <summary>タブ表示名</summary>
        public string tab_name { get; set; } = "";

        /// <summary>月度フィルター（空文字=全月）例: "2025年10月度"</summary>
        public string filter_month { get; set; } = "";

        /// <summary>作業内容フィルター（空文字=未適用）</summary>
        public string filter_content { get; set; } = "";

        /// <summary>マッチング方式（"partial"=部分一致 / "exact"=完全一致）</summary>
        public string filter_match { get; set; } = "partial";

        /// <summary>氏名フィルター（空文字=全員 / 複数は,区切り）</summary>
        public string filter_names { get; set; } = "";

        /// <summary>
        /// 個人別集計モード（1=ON / 0=OFF）
        /// ONのとき daily_reports から直接集計し、指定氏名の時間・人件費・損料のみを算出する。
        /// 交通費は個人特定が不可能なため含まない。
        /// </summary>
        public int is_single_mode { get; set; } = 0;

        // ▼▼▼ 追加：日付範囲フィルター（yyyy-MM-dd形式。空文字=未設定） ▼▼▼
        /// <summary>
        /// 期間絞り込みの開始日（yyyy-MM-dd形式。空文字=未設定）
        /// daily_reports.report_date で絞り込む。月度フィルターとは独立して動作する。
        /// </summary>
        public string filter_date_from { get; set; } = "";

        /// <summary>
        /// 期間絞り込みの終了日（yyyy-MM-dd形式。空文字=未設定）
        /// </summary>
        public string filter_date_to { get; set; } = "";

        // ▼▼▼ 追加：現場別単価適用フラグ（1=project_ratesを使用 / 0=デフォルト単価） ▼▼▼
        /// <summary>
        /// 現場別単価を使用するフラグ（1=ON / 0=OFF）
        /// ONの場合、集計時に project_rates テーブルの単価を参照する
        /// </summary>
        public int use_custom_rates { get; set; } = 0;
    }
}
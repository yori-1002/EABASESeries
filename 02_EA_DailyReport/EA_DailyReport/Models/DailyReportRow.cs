namespace EA_DailyReport.Models
{
    /// <summary>
    /// 日報一覧 DataGrid 表示用の統合行（v0.1.4 案_日報レイアウト 22列）
    /// daily_reports + work_details を結合した1行を表す
    ///
    /// 1人1日に複数行：最初の行に出退勤情報を表示、2行目以降は「-」表示
    /// （Excel 日報の慣習と同じ。is_first_row_of_day で判定）
    ///
    /// すべての時刻・数値は表示用の文字列に整形済み（DataGrid に直接バインド）
    /// </summary>
    public class DailyReportRow
    {
        // ─── 内部 ID（編集・削除時に使用） ───
        public int daily_report_id { get; set; }
        public int work_detail_id { get; set; }

        /// <summary>当日の最初の行か（出退勤を表示するか）</summary>
        public bool is_first_row_of_day { get; set; }

        // ─── 表示用カラム（22列） ───

        /// <summary>日付（"MM/dd"）</summary>
        public string report_date_display { get; set; } = "";

        /// <summary>氏名</summary>
        public string employee_name { get; set; } = "";

        /// <summary>出勤（"HH:mm" / "-"）</summary>
        public string clock_in_display { get; set; } = "";

        /// <summary>退勤（"HH:mm" / "-"）</summary>
        public string clock_out_display { get; set; } = "";

        /// <summary>早朝（"H:mm" / "-"）</summary>
        public string early_morning_display { get; set; } = "";

        /// <summary>残業（"H:mm" / "-"）</summary>
        public string overtime_display { get; set; } = "";

        /// <summary>深夜（"H:mm" / "-"）</summary>
        public string late_night_display { get; set; } = "";

        /// <summary>開始場所</summary>
        public string start_location_display { get; set; } = "";

        /// <summary>終了場所</summary>
        public string end_location_display { get; set; } = "";

        /// <summary>職種（助手 / 技師）※ 推測ですが、現状は固定値</summary>
        public string job_type { get; set; } = "";

        /// <summary>区分</summary>
        public string category_code { get; set; } = "";

        /// <summary>業務名</summary>
        public string project_name { get; set; } = "";

        /// <summary>作業詳細</summary>
        public string detail { get; set; } = "";

        /// <summary>時間(h)</summary>
        public double hours { get; set; }

        /// <summary>ソフト・機材</summary>
        public string software { get; set; } = "";

        /// <summary>料金（"1,750" / "-"）</summary>
        public string software_cost_display { get; set; } = "";

        /// <summary>車両</summary>
        public string vehicle { get; set; } = "";

        /// <summary>発</summary>
        public string from_location { get; set; } = "";

        /// <summary>着</summary>
        public string to_location { get; set; } = "";

        /// <summary>距離（"21.0" / "-"）</summary>
        public string distance_display { get; set; } = "";

        /// <summary>移動方法</summary>
        public string transport { get; set; } = "";

        /// <summary>備考</summary>
        public string note { get; set; } = "";

        // ─── ステータスマーク（v0.1.4 設計書 2.2） ───

        /// <summary>同期状態マーク（🔄=未同期 / ✏️=編集中 / 🔒=期限切れ / 空=同期済み）</summary>
        public string status_mark { get; set; } = "";
    }
}

namespace EA_DailyReport.Models
{
    /// <summary>
    /// daily_reports テーブルのレコード（v0.1.4 設計書 3.1）
    /// 1人1日に1行。複数の作業詳細は work_details で別管理
    /// Dapper で SELECT/INSERT に直接マップする想定のため、
    /// プロパティ名は SQL カラム名と完全一致させる
    /// </summary>
    public class DailyReport
    {
        /// <summary>主キー（INSERT 時は 0 を渡す→AUTOINCREMENT）</summary>
        public int id { get; set; }

        /// <summary>日付（"yyyy-MM-dd" 形式・ISO 8601）</summary>
        public string report_date { get; set; } = "";

        /// <summary>作業者ID（employees.id）</summary>
        public int employee_id { get; set; }

        /// <summary>作業者名（employees.employee_name のキャッシュ）</summary>
        public string employee_name { get; set; } = "";

        /// <summary>出勤時刻（"HH:mm" 形式）</summary>
        public string clock_in { get; set; } = "";

        /// <summary>退勤時刻（"HH:mm" 形式）</summary>
        public string clock_out { get; set; } = "";

        /// <summary>早朝時間（分）。8:00 前の出勤時間分。WorkTimeCalculator で自動算出</summary>
        public int early_morning_min { get; set; }

        /// <summary>残業時間（分）。定時超過分（22:00 以降は深夜にカウント）</summary>
        public int overtime_min { get; set; }

        /// <summary>深夜時間（分）。22:00 以降の時間分</summary>
        public int late_night_min { get; set; }

        /// <summary>開始場所（本社・現場・リモート 等）</summary>
        public string start_location { get; set; } = "";

        /// <summary>終了場所</summary>
        public string end_location { get; set; } = "";

        /// <summary>入力者の employee_id（代理入力対応）</summary>
        public int entered_by { get; set; }

        /// <summary>入力元 PC の MAC アドレス（同期競合検出用）</summary>
        public string source_pc { get; set; } = "";

        /// <summary>データソース：'desktop' / 'saas' / 'excel'</summary>
        public string source { get; set; } = "desktop";

        /// <summary>GDrive 取込時のファイル ID（重複取込防止）</summary>
        public string gdrive_file_id { get; set; } = "";

        /// <summary>レコード作成日時</summary>
        public string created_at { get; set; } = "";

        /// <summary>最終更新日時（同期の競合解決に使用）</summary>
        public string updated_at { get; set; } = "";
    }
}

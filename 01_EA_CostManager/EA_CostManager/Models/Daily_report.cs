namespace EA_CostManager.Models
{
    /// <summary>
    /// 日報データ（作業記録）
    /// </summary>
    public class daily_report
    {
        public long id { get; set; }
        public string report_date { get; set; } = string.Empty;
        public string employee_name { get; set; } = string.Empty;
        public string job_type { get; set; } = string.Empty;
        public string category_code { get; set; } = string.Empty;
        public string project_name { get; set; } = string.Empty;
        public string detail { get; set; } = string.Empty;
        public double hours { get; set; }
        public string clock_in { get; set; } = string.Empty;
        public string clock_out { get; set; } = string.Empty;
        public string overtime { get; set; } = string.Empty;
        public string start_location { get; set; } = string.Empty;
        public string end_location { get; set; } = string.Empty;
        public string import_batch { get; set; } = string.Empty;
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
        public string updated_by { get; set; } = string.Empty;

        // 表示用
        public string display_date => report_date.Length >= 10
            ? report_date.Substring(0, 10) : report_date;
    }
}
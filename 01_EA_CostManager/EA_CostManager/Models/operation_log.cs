namespace EA_CostManager.Models
{
    /// <summary>
    /// 操作ログ
    /// </summary>
    public class operation_log
    {
        public long id { get; set; }
        public string log_datetime { get; set; } = string.Empty;
        public string operator_name { get; set; } = string.Empty;
        public string operation_type { get; set; } = string.Empty;   // IMPORT/EXPORT/CALC/EDIT/DELETE
        public string target_table { get; set; } = string.Empty;
        public long? target_id { get; set; }
        public string detail { get; set; } = string.Empty;
        public int? record_count { get; set; }
        public string file_path { get; set; } = string.Empty;
    }

    /// <summary>
    /// エラーログ
    /// </summary>
    public class error_log
    {
        public long id { get; set; }
        public string log_datetime { get; set; } = string.Empty;
        public string operator_name { get; set; } = string.Empty;
        public string error_type { get; set; } = string.Empty;
        public string error_message { get; set; } = string.Empty;
        public string stack_trace { get; set; } = string.Empty;
    }
}
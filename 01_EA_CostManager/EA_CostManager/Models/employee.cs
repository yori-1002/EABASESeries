namespace EA_CostManager.Models
{
    /// <summary>
    /// 社員マスタ
    /// </summary>
    public class employee
    {
        public long id { get; set; }
        public string employee_name { get; set; } = string.Empty;
        public string job_type { get; set; } = string.Empty;        // 技師 or 助手
        public decimal daily_rate { get; set; }                      // 日額単価
        public bool is_active { get; set; } = true;                  // 在籍中フラグ
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
        public string updated_by { get; set; } = string.Empty;
    }
}
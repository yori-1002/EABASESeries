namespace EA_CostManager.Models
{
    /// <summary>
    /// 現場マスタ
    /// </summary>
    public class project
    {
        public long id { get; set; }
        public string category_code { get; set; } = string.Empty;   // 区分（EA94等）
        public string site_name { get; set; } = string.Empty;       // 現場名
        public string company_name { get; set; } = string.Empty;    // 企業名
        public string detail { get; set; } = string.Empty;          // 詳細
        public string attribute { get; set; } = string.Empty;       // 属性（契約中等）
        public bool is_active { get; set; } = true;                 // 進行中フラグ
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
        public string updated_by { get; set; } = string.Empty;
    }
}
namespace EA_CostManager.Models
{
    /// <summary>
    /// 機材使用記録（日報から分離）
    /// </summary>
    public class daily_equipment
    {
        public long id { get; set; }
        public long daily_report_id { get; set; }
        public string equipment_name { get; set; } = string.Empty;  // ソフト・機材名
        public decimal daily_rate { get; set; }                      // 日額料金
        public int quantity { get; set; } = 1;                       // 使用数
    }
}
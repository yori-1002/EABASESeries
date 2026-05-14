namespace EA_CostManager.Models
{
    /// <summary>
    /// 交通記録（日報から分離）
    /// </summary>
    public class daily_transport
    {
        public long id { get; set; }
        public long daily_report_id { get; set; }
        public string vehicle { get; set; } = string.Empty;         // 車両名
        public string departure { get; set; } = string.Empty;       // 出発地
        public string arrival { get; set; } = string.Empty;         // 到着地
        public double distance { get; set; }                         // 距離(km)
        public string travel_method { get; set; } = string.Empty;   // 下道 or 高速
        public decimal transport_cost { get; set; }                  // 交通費（自動計算）
    }
}
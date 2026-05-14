namespace EA_CostManager.Models
{
    /// <summary>
    /// アプリ設定
    /// </summary>
    public class app_setting
    {
        public string key { get; set; } = string.Empty;
        public string value { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
        public string updated_by { get; set; } = string.Empty;
    }

    /// <summary>
    /// 設定キーの定数
    /// </summary>
    public static class setting_keys
    {
        public const string engineer_rate = "engineer_rate";
        public const string assistant_rate = "assistant_rate";
        public const string profit_rate = "profit_rate";
        public const string base_hours_per_day = "base_hours_per_day";
        public const string road_cost_per_km = "road_cost_per_km";
        public const string highway_cost_per_km = "highway_cost_per_km";
        public const string db_path = "db_path";
        public const string nas_path = "nas_path";
    }
}
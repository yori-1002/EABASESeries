namespace EA_CostManager.Models
{
    /// <summary>
    /// cost_records テーブルの1行に対応するモデル（設計書4.8）
    /// </summary>
    public class cost_record
    {
        public int id { get; set; }
        public string category_code { get; set; } = "";
        public string record_date { get; set; } = "";
        public string fiscal_month { get; set; } = "";
        public string work_content { get; set; } = "";

        // ---- 技師 ----
        public string engineer_names { get; set; } = "";
        public double engineer_hours { get; set; }
        public double engineer_days { get; set; }
        public decimal engineer_cost { get; set; }
        /// <summary>技師の人数（重複除去後）</summary>
        public int engineer_count { get; set; }

        // ---- 助手 ----
        public string assistant_names { get; set; } = "";
        public double assistant_hours { get; set; }
        public double assistant_days { get; set; }
        public decimal assistant_cost { get; set; }
        /// <summary>助手の人数（重複除去後）</summary>
        public int assistant_count { get; set; }

        // ---- 人件費合計 ----
        public decimal personnel_cost { get; set; }

        // ---- 交通 ----
        public decimal transport_cost { get; set; }
        public double distance_total { get; set; }
        public int vehicle_count { get; set; }

        // ---- 機材 ----
        /// <summary>6h以上使用した機材名（・区切り）</summary>
        public string equipment_names { get; set; } = "";
        /// <summary>機材使用詳細「AutoCAD（ズイ 8.0h・タイ 7.0h）」形式</summary>
        public string equipment_detail { get; set; } = "";
        /// <summary>6h以上使用した機材種類数</summary>
        public int equipment_quantity { get; set; }
        public decimal equipment_cost { get; set; }

        // ---- 合計 ----
        public decimal total_cost { get; set; }

        // ---- 監査 ----
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
        public string updated_by { get; set; } = "";
    }
}
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace EA_CostManager.Models
{
    public enum RowItemType { normal, month_header, subtotal, year_header }

    public class cost_row_item : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ---- 行種別 ----
        public RowItemType row_type { get; init; } = RowItemType.normal;

        public bool is_header => row_type == RowItemType.month_header;
        public bool is_subtotal => row_type == RowItemType.subtotal;
        public bool is_normal => row_type == RowItemType.normal;
        // ▼▼▼ 追加：年ヘッダー行 ▼▼▼
        public bool is_year_header => row_type == RowItemType.year_header;

        // ▼▼▼ 追加：年キー（年ヘッダー折りたたみ制御に使用）▼▼▼
        public string year_key { get; set; } = "";

        // ---- 月度グループキー（折り畳み制御に使用） ----
        public string fiscal_month { get; set; } = "";

        // ---- 折り畳み状態（データ行・小計行に適用） ----
        private bool _is_hidden = false;
        public bool is_hidden
        {
            get => _is_hidden;
            set { if (_is_hidden != value) { _is_hidden = value; notify(); } }
        }

        // ---- 折り畳みボタン表示テキスト（月度ヘッダー行のみ） ----
        private bool _is_collapsed = false;
        public bool is_collapsed
        {
            get => _is_collapsed;
            set { if (_is_collapsed != value) { _is_collapsed = value; notify(); notify(nameof(collapse_icon)); } }
        }
        public string collapse_icon => _is_collapsed ? "▶" : "▼";

        // ---- 作業日列 ----
        public string display_date { get; init; } = "";

        // ---- 作業内容列 ----
        public string work_content { get; init; } = "";

        // ---- 氏名 ----
        public string engineer_names { get; init; } = "";
        public string assistant_names { get; init; } = "";

        // ---- 人数 ----
        public int engineer_count { get; init; }   // 技師の人数
        public int assistant_count { get; init; }  // 助手の人数

        // ---- 数値データ ----
        public double engineer_hours { get; init; }
        public double engineer_days { get; init; }
        public decimal engineer_cost { get; init; }
        public double assistant_hours { get; init; }
        public double assistant_days { get; init; }
        public decimal assistant_cost { get; init; }
        public double total_days { get; init; }
        public decimal personnel_cost { get; init; }
        public double distance_total { get; init; }
        public int vehicle_count { get; init; }
        public decimal transport_cost { get; init; }
        public int equipment_quantity { get; init; }
        public decimal equipment_cost { get; init; }
        public string equipment_detail { get; init; } = "";
        public decimal total_cost { get; init; }

        // ---- 表示用文字列 ----
        public string display_engineer_count => is_normal && engineer_count > 0 ? engineer_count.ToString() : "";
        public string display_assistant_count => is_normal && assistant_count > 0 ? assistant_count.ToString() : "";
        // ▼修正：日数・時間は通常行＋小計行で表示（小計は月度合計）
        public string display_engineer_days => (is_normal || is_subtotal) ? engineer_days.ToString("0.00") : "";
        public string display_engineer_hours => (is_normal || is_subtotal) ? engineer_hours.ToString("0.0") : "";
        public string display_assistant_days => (is_normal || is_subtotal) ? assistant_days.ToString("0.00") : "";
        public string display_assistant_hours => (is_normal || is_subtotal) ? assistant_hours.ToString("0.0") : "";
        public string display_total_days_str => (is_normal || is_subtotal) ? total_days.ToString("0.00") : "";
        public string display_distance => is_normal ? distance_total.ToString("0.0") : "";
        public string display_vehicle_count => is_normal ? vehicle_count.ToString() : "";
        public string display_equipment_qty => is_normal ? equipment_quantity.ToString() : "";
        public string display_equipment_detail => is_normal ? equipment_detail : "";
        // ▼修正：金額系は通常行＋小計行で表示（小計は各列の月度合計）
        public string display_engineer_cost => (is_normal || is_subtotal) ? engineer_cost.ToString("#,##0") : "";
        public string display_assistant_cost => (is_normal || is_subtotal) ? assistant_cost.ToString("#,##0") : "";
        public string display_personnel_cost => (is_normal || is_subtotal) ? personnel_cost.ToString("#,##0") : "";
        public string display_transport_cost => (is_normal || is_subtotal) ? transport_cost.ToString("#,##0") : "";
        public string display_equipment_cost => (is_normal || is_subtotal) ? equipment_cost.ToString("#,##0") : "";
        public string display_total_cost => (is_normal || is_subtotal) ? total_cost.ToString("#,##0") : "";

        // ================================================================
        // ファクトリメソッド
        // ================================================================

        /// <summary>cost_record から通常行を生成</summary>
        public static cost_row_item from_record(cost_record r) => new()
        {
            row_type = RowItemType.normal,
            display_date = r.record_date,
            work_content = r.work_content,
            engineer_names = r.engineer_names,
            engineer_count = r.engineer_count,
            engineer_hours = r.engineer_hours,
            engineer_days = r.engineer_days,
            engineer_cost = r.engineer_cost,
            assistant_names = r.assistant_names,
            assistant_count = r.assistant_count,
            assistant_hours = r.assistant_hours,
            assistant_days = r.assistant_days,
            assistant_cost = r.assistant_cost,
            total_days = r.engineer_days + r.assistant_days,
            personnel_cost = r.personnel_cost,
            distance_total = r.distance_total,
            vehicle_count = r.vehicle_count,
            transport_cost = r.transport_cost,
            equipment_quantity = r.equipment_quantity,
            equipment_cost = r.equipment_cost,
            equipment_detail = r.equipment_detail,
            total_cost = r.total_cost,
        };

        /// <summary>月度ヘッダー行を生成</summary>
        public static cost_row_item make_header(string fiscal_month) => new()
        {
            row_type = RowItemType.month_header,
            fiscal_month = fiscal_month,
            work_content = $"【{fiscal_month}】",
        };

        /// <summary>▼▼▼ 追加：年ヘッダー行を生成 ▼▼▼</summary>
        public static cost_row_item make_year_header(string year_key) => new()
        {
            row_type = RowItemType.year_header,
            year_key = year_key,
            work_content = $"◆ {year_key}",
        };

        /// <summary>月度小計行を生成</summary>
        public static cost_row_item make_subtotal(
            string fiscal_month,
            IEnumerable<cost_record> records)
        {
            var list = records.ToList();

            // ▼修正：各列の月度合計を計算（技師/助手の人件費・交通費も追加）
            double eng_days = list.Sum(r => r.engineer_days);
            double eng_h = list.Sum(r => r.engineer_hours);
            decimal eng_c = list.Sum(r => r.engineer_cost);
            double asst_days = list.Sum(r => r.assistant_days);
            double asst_h = list.Sum(r => r.assistant_hours);
            decimal asst_c = list.Sum(r => r.assistant_cost);
            decimal pers = list.Sum(r => r.personnel_cost);
            decimal trans = list.Sum(r => r.transport_cost);
            decimal equ_c = list.Sum(r => r.equipment_cost);
            decimal total = list.Sum(r => r.total_cost);

            // ▼修正：作業内容のまとめテキストは廃止し、各数値を列ごとに保持（display_* が小計でも返す）
            return new()
            {
                row_type = RowItemType.subtotal,
                fiscal_month = fiscal_month,
                display_date = $"【{fiscal_month}】",   // 作業日列に月度ラベル
                work_content = "月度総合計",             // 作業内容列に合計行ラベル（ぱっと見で合計と分かるように）
                engineer_days = eng_days,
                engineer_hours = eng_h,
                engineer_cost = eng_c,
                assistant_days = asst_days,
                assistant_hours = asst_h,
                assistant_cost = asst_c,
                total_days = eng_days + asst_days,
                personnel_cost = pers,
                transport_cost = trans,
                equipment_cost = equ_c,
                total_cost = total,
            };
        }

    }
}
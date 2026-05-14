// DailyReportRow.cs
// ExcelImportService が使用する日報1行分のデータモデル
// Excelから読み込んだ生データをそのまま保持する
//
// ▼ v0.9.7 追加：バリデーションエラー表示用に row_number / sheet_name を追加
//   エラーが発生した行・シートをユーザーに正確に伝えるため

using System;

namespace EA_CostManager.Models
{
    public class DailyReportRow
    {
        // ── ▼ 追加：行特定用メタ情報（v0.9.7）──────
        // バリデーションエラー時に「どのシートの何行目か」を表示するため
        // 取込・集計ロジック自体には使用しない（識別用メタデータ）

        // Excel上の行番号（1始まり・データ行のみ。ヘッダー行は対象外）
        // 現行形式：Row5以降の連番
        // 旧形式：各日付シート内の作業行Row（シートごとに独立した連番）
        public int row_number { get; set; }

        // データ取得元のシート名
        // 現行形式：常に「日報入力」
        // 旧形式：日付シート名（例：「23」「1」など）
        public string sheet_name { get; set; } = string.Empty;

        // ── 基本情報 ──────────────────────────
        public DateTime work_date { get; set; }
        public string employee_name { get; set; } = string.Empty;
        public string? clock_in { get; set; }
        public string? clock_out { get; set; }
        public string? overtime { get; set; }
        public string start_location { get; set; } = string.Empty;
        public string end_location { get; set; } = string.Empty;

        // ── 業務情報 ──────────────────────────
        public string job_type { get; set; } = string.Empty;
        public string category_code { get; set; } = string.Empty;
        public string project_name { get; set; } = string.Empty;
        public string detail { get; set; } = string.Empty;
        public decimal hours { get; set; }

        // ── 機材情報 ──────────────────────────
        public string equipment_name { get; set; } = string.Empty;
        public decimal equipment_rate { get; set; }

        // ── 移動情報 ──────────────────────────
        public string vehicle { get; set; } = string.Empty;
        public string departure { get; set; } = string.Empty;
        public string arrival { get; set; } = string.Empty;
        public decimal? distance { get; set; }
        public string travel_method { get; set; } = string.Empty;

        // ── 備考 ──────────────────────────────
        public string remarks { get; set; } = string.Empty;

        // ── 計算プロパティ ──────────────────────
        // 移動データが存在するか
        public bool has_transport =>
            !string.IsNullOrWhiteSpace(vehicle) ||
            !string.IsNullOrWhiteSpace(departure) ||
            !string.IsNullOrWhiteSpace(arrival);

        // 機材データが存在するか
        public bool has_equipment =>
            !string.IsNullOrWhiteSpace(equipment_name);
    }
}
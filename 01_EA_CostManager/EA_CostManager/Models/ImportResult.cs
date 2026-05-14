// ImportResult.cs
// ExcelImportService のインポート結果を格納するモデル
//
// ▼ v0.9.7 追加：
//   ・validation_errors          ：取込時に検出したバリデーションエラー一覧
//   ・newly_registered_employees ：今回の取込で employees テーブルに新規追加された社員名一覧
//   ・ImportValidationError クラス：エラー1件分のデータ構造（同ファイル内に定義）
//
// 設計方針：
//   ・新規ファイル禁止のため、ImportValidationError は本ファイル内に同居させる
//   ・ImportResult が List<ImportValidationError> を保持するため、同じ namespace に置く方が自然

using System;
using System.Collections.Generic;

namespace EA_CostManager.Models
{
    public class ImportResult
    {
        // ── 基本情報 ──────────────────────────
        public string file_name { get; set; } = string.Empty;
        public DateTime imported_at { get; set; }

        // ── 処理結果 ──────────────────────────
        public bool is_success { get; set; }
        public string? error_message { get; set; }

        // ── 取込範囲 ──────────────────────────
        public DateTime date_from { get; set; }
        public DateTime date_to { get; set; }
        public int total_rows { get; set; }

        // ── 件数 ──────────────────────────────
        public int inserted_rows { get; set; }
        public int skipped_rows { get; set; }

        // ▼▼▼ 追加：ExcelImportServiceが生成したbatch_id（daily_reportsのimport_batchと一致）▼▼▼
        public string batch_id { get; set; } = string.Empty;

        // ── ▼ 追加（v0.9.7）：バリデーション結果 ──────────────────
        // バリデーションでエラー判定された行の一覧
        // ・該当行は取込対象から除外される（スキップ動作）
        // ・取込完了後、import_view_model 側で MessageBox 一覧表示する
        // ・空リストの場合はエラーなし（メッセージ表示なし）
        public List<ImportValidationError> validation_errors { get; set; }
            = new List<ImportValidationError>();

        // ── ▼ 追加（v0.9.7）：新規登録社員一覧 ────────────────────
        // 今回の取込で employees テーブルに新規 INSERT された社員名のリスト
        // ・既存の UpsertEmployeesAsync は未登録社員を自動 INSERT するため、
        //   その INSERT した社員名をここに集約する
        // ・取込完了後、import_view_model 側で MessageBox 通知する
        //   （「以下の社員が新規登録されました：氏名一覧」）
        // ・既存社員のみで構成されていた場合は空リスト（通知なし）
        public List<string> newly_registered_employees { get; set; }
            = new List<string>();

        // ── 計算プロパティ ──────────────────────
        // 例: "2025年01月"
        public string fiscal_month_label =>
            date_from == default
                ? string.Empty
                : $"{date_from:yyyy年MM月}";
    }

    // ====================================================================
    // ▼ 追加（v0.9.7）：バリデーションエラー1件分のデータ構造
    // 新規ファイル禁止のため本ファイル内に同居
    // ImportResult.validation_errors の List 要素として使用される
    // ====================================================================
    public class ImportValidationError
    {
        // エラーが発生したファイル名（拡張子付き・パスなし）
        // 例：「260221_日報入力.xlsm」
        public string file_name { get; set; } = string.Empty;

        // エラーが発生したシート名
        // ・現行形式：「日報入力」
        // ・旧形式 ：日付シート名（例：「23」「1」など）
        public string sheet_name { get; set; } = string.Empty;

        // Excel上の行番号（1始まり）
        // ・行番号 0 はシート全体に対するエラー（行特定不可）の意
        public int row_number { get; set; }

        // エラーが発生した列名（日本語表記）
        // 例：「区分」「氏名」「時間」「距離」など
        // ・列特定不能のシート全体エラーは空文字
        public string column_name { get; set; } = string.Empty;

        // エラー種別（短い分類タグ）
        // 例：「型エラー」「必須項目欠落」「範囲外」「論理エラー」
        public string error_type { get; set; } = string.Empty;

        // エラー詳細メッセージ（ユーザー向け文章）
        // 例：「区分列に日付形式が入力されています」
        //     「時間が24時間を超えています（25.5h）」
        public string detail { get; set; } = string.Empty;

        // エラー対象セルの実際の値（文字列化）
        // ・調査時にユーザーが「実際に何が入っていたか」を確認できるよう保持
        // ・空セル等で値がない場合は空文字
        public string raw_value { get; set; } = string.Empty;

        // ── 表示用フォーマット ──────────────────────
        // MessageBox 1行表示用の整形済みテキスト
        // 例：「[260221_日報入力.xlsm / 日報入力 / 行15 / 区分] 型エラー：区分列に日付形式（値：2026/4/27）」
        public string display_line =>
            row_number > 0
                ? $"[{file_name} / {sheet_name} / 行{row_number} / {column_name}] {error_type}：{detail}"
                  + (string.IsNullOrEmpty(raw_value) ? "" : $"（値：{raw_value}）")
                : $"[{file_name} / {sheet_name}] {error_type}：{detail}";
    }
}
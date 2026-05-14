using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using EA_CostManager.Models;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Services
{
    /// <summary>
    /// 出力する列を選択するオプションクラス
    /// ExcelExportDialog と ExcelExportService で共通使用
    /// </summary>
    public class ExcelColumnOptions
    {
        public bool eng_name { get; set; } = true;
        public bool eng_count { get; set; } = true;  // ▼▼▼ 追加：技師人数
        public bool eng_day { get; set; } = true;
        public bool eng_hour { get; set; } = true;
        public bool eng_cost { get; set; } = true;
        public bool ast_name { get; set; } = true;
        public bool ast_count { get; set; } = true;  // ▼▼▼ 追加：助手人数
        public bool ast_day { get; set; } = true;
        public bool ast_hour { get; set; } = true;
        public bool ast_cost { get; set; } = true;
        public bool total_man { get; set; } = true;
        public bool person { get; set; } = true;
        public bool dist { get; set; } = true;
        public bool vehicle { get; set; } = true;
        public bool trans { get; set; } = true;
        public bool use_count { get; set; } = true;
        public bool equip { get; set; } = true;
        public bool equip_name { get; set; } = true;
        public bool total { get; set; } = true;

        // ★v0.9.7追加：ヘッダー表示オプション
        // チェックON → タイトル下に絞り込み条件・カスタム単価情報を出力する
        // 顧客提出時はOFFでシンプルな表のみ、社内確認用はONで詳細情報付きを使い分けられる
        public bool show_filter_info { get; set; } = false;
        public bool show_custom_rate_info { get; set; } = false;
    }

    /// <summary>
    /// 原価集計データをExcelファイルに書き出すサービス
    /// 1現場1ブック・1タブ1シートの構成
    /// 出力列はExcelColumnOptionsで選択可能
    /// </summary>
    public static class ExcelExportService
    {
        // ---- カラー定義 ----
        private static readonly XLColor CLR_TITLE = XLColor.FromHtml("#EEF4FB");
        private static readonly XLColor CLR_COL_HDR = XLColor.FromHtml("#2E75B6");
        private static readonly XLColor CLR_MONTH_HDR = XLColor.FromHtml("#2E75B6");
        private static readonly XLColor CLR_SUBTOTAL = XLColor.FromHtml("#B8D4EE");
        private static readonly XLColor CLR_TOTAL = XLColor.FromHtml("#2E75B6");
        private static readonly XLColor CLR_ALT = XLColor.FromHtml("#F7FAFD");
        private static readonly XLColor CLR_WHITE = XLColor.White;
        private static readonly XLColor CLR_BORDER = XLColor.FromHtml("#CCDDEE");

        /// <summary>
        /// 指定した現場・タブ一覧をExcelブックに書き出してファイルパスを返す
        /// </summary>
        public static string export(
            project_cost_view_model project_vm,
            IEnumerable<filter_tab_view_model> export_tabs,
            string output_dir,
            ExcelColumnOptions? cols = null,
            string? date_from = null,
            string? date_to = null)
        {
            cols ??= new ExcelColumnOptions(); // デフォルトは全列出力

            // ファイル名：現場コード_現場名_出力日時.xlsx
            string safe_name = make_safe_filename(
                $"{project_vm.category_code}_{project_vm.site_name}");
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
            string file_path = Path.Combine(output_dir, $"{safe_name}_{timestamp}.xlsx");

            using var wb = new XLWorkbook();

            foreach (var tab in export_tabs)
            {
                string sheet_name = make_safe_sheet_name(tab.tab_name);
                var ws = wb.Worksheets.Add(sheet_name);
                write_sheet(ws, project_vm, tab, cols, date_from, date_to);
            }

            Directory.CreateDirectory(output_dir);
            wb.SaveAs(file_path);
            return file_path;
        }

        // ---- 1シート書き出し ----
        private static void write_sheet(
            IXLWorksheet ws,
            project_cost_view_model project_vm,
            filter_tab_view_model tab,
            ExcelColumnOptions cols,
            string? date_from,
            string? date_to)
        {
            // 列構成を決定（固定列 + 選択列）
            var col_defs = build_col_defs(cols);
            set_col_widths(ws, col_defs);

            int row = 1;
            // ▼▼▼ 削除：タイトル行（EA_CostManager 原価集計...）を削除 ▼▼▼
            var records = filter_records(tab.raw_records, date_from, date_to);

            if (records.Count == 0)
            {
                ws.Cell(row, 1).Value = "対象データがありません";
                ws.Cell(row, 1).Style.Font.Italic = true;
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.Gray;
                return;
            }

            // ★v0.9.7追加：絞り込み条件・カスタム単価情報をヘッダーに出力
            // - 顧客提出時はOFFを推奨（数字の出所だけ見せる）
            // - 社内確認用はONで詳細を残しておく
            // tab には絞り込み条件・use_custom_ratesの情報が入っているのでそれを参照する
            // 戻り値：書き込み終了後の次の行番号（オプションOFFなら 1 が返る）
            row = write_filter_info(ws, project_vm, tab, cols, date_from, date_to);

            row = write_col_header(ws, row, col_defs);

            var groups = records
                .GroupBy(r => r.fiscal_month ?? "")
                .OrderBy(g => g.Key);

            foreach (var g in groups)
            {
                int month_header_row = row;
                row = write_month_header(ws, row, g.Key, col_defs.Count);
                var list = g.OrderBy(r => r.record_date).ToList();
                int data_start_row = row;
                for (int i = 0; i < list.Count; i++)
                    row = write_data_row(ws, row, list[i], i, col_defs);
                int subtotal_row = row;
                row = write_subtotal(ws, row, list, g.Key, col_defs);

                // ▼▼▼ 削除：月度グループ折り畳みは無効化 ▼▼▼
            }

            row = write_total(ws, row, records, col_defs);
            setup_print(ws);
        }

        // ---- 列定義ビルド ----
        // 列名・幅・データアクセサーのリストを生成
        private record ColDef(string header, double width, Func<cost_record, string> get_val, bool is_num = false);

        private static List<ColDef> build_col_defs(ExcelColumnOptions c)
        {
            var list = new List<ColDef>
            {
                new("作業日",   10, r => fmt_date(r.record_date)),
                new("作業内容", 30, r => r.work_content ?? ""),
            };
            if (c.eng_name) list.Add(new("技師氏名", 10, r => r.engineer_names ?? ""));
            if (c.eng_count) list.Add(new("技師\n人数", 7, r => r.engineer_count == 0 ? "" : r.engineer_count.ToString(), true));
            if (c.eng_day) list.Add(new("技師人工(日)", 9, r => fmt_num(r.engineer_days), true));
            if (c.eng_hour) list.Add(new("技師時間(h)", 8, r => fmt_num(r.engineer_hours), true));
            if (c.eng_cost) list.Add(new("技師人件費", 10, r => fmt_yen(r.engineer_cost), true));
            if (c.ast_name) list.Add(new("助手氏名", 10, r => r.assistant_names ?? ""));
            if (c.ast_count) list.Add(new("助手\n人数", 7, r => r.assistant_count == 0 ? "" : r.assistant_count.ToString(), true));
            if (c.ast_day) list.Add(new("助手人工(日)", 9, r => fmt_num(r.assistant_days), true));
            if (c.ast_hour) list.Add(new("助手時間(h)", 8, r => fmt_num(r.assistant_hours), true));
            if (c.ast_cost) list.Add(new("助手人件費", 10, r => fmt_yen(r.assistant_cost), true));
            if (c.total_man) list.Add(new("合計人工(日)", 9, r => fmt_num(r.engineer_days + r.assistant_days), true));
            if (c.person) list.Add(new("人件費合計", 10, r => fmt_yen(r.personnel_cost), true));
            if (c.dist) list.Add(new("距離(km往復)", 9, r => fmt_num(r.distance_total), true));
            if (c.vehicle) list.Add(new("台数", 6, r => r.vehicle_count == 0 ? "" : r.vehicle_count.ToString(), true));
            if (c.trans) list.Add(new("交通費", 9, r => fmt_yen(r.transport_cost), true));
            if (c.use_count) list.Add(new("使用数", 6, r => r.equipment_quantity == 0 ? "" : r.equipment_quantity.ToString(), true));
            if (c.equip) list.Add(new("損料", 9, r => fmt_yen(r.equipment_cost), true));
            if (c.equip_name) list.Add(new("機材・使用者", 14, r => r.equipment_names ?? ""));
            if (c.total) list.Add(new("合計金額", 11, r => fmt_yen(r.total_cost), true));
            return list;
        }

        private static void set_col_widths(IXLWorksheet ws, List<ColDef> defs)
        {
            for (int i = 0; i < defs.Count; i++)
                ws.Column(i + 1).Width = defs[i].width;
        }

        // ---- タイトル行 ----
        private static int write_title(
            IXLWorksheet ws, int row,
            project_cost_view_model project_vm,
            filter_tab_view_model tab,
            int col_count)
        {
            var range = ws.Range(row, 1, row, col_count);
            range.Style.Fill.BackgroundColor = CLR_TITLE;
            ws.Cell(row, 1).Value =
                $"EA_CostManager 原価集計　{project_vm.category_code}_{project_vm.site_name}　/ {tab.tab_name}";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 12;

            ws.Cell(row, col_count).Value = $"出力：{DateTime.Now:yyyy/MM/dd HH:mm}";
            ws.Cell(row, col_count).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(row, col_count).Style.Font.FontSize = 9;
            ws.Cell(row, col_count).Style.Font.FontColor = XLColor.Gray;
            ws.Row(row).Height = 18;
            // タイトル行外枠罫線
            ws.Range(row, 1, row, col_count).Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            ws.Range(row, 1, row, col_count).Style.Border.OutsideBorderColor = CLR_COL_HDR;
            return row + 1;
        }

        // ★v0.9.7追加：絞り込み条件・カスタム単価情報をヘッダーに出力
        // 戻り値：書き込み終了後の次の行番号
        // 出力されるレイアウト例（show_filter_info=true, show_custom_rate_info=true 時）：
        //   行1: 「現場：EA45_北陸高架橋（4-1工事）」（タイトル）
        //   行2: 「絞り込み条件：「3D」「モデル」を含む / 千原・ズイ」
        //   行3: 「カスタム単価：技師 9,000円/h、助手 6,500円/h（標準より変更）」
        //   行4: 「期間：2026/3/20 〜 2026/4/21」（期間指定がある場合のみ）
        //   行5: 空行（ヘッダーとデータの区切り）
        // どのオプションもOFFなら何も出力せず row をそのまま返す
        private static int write_filter_info(
            IXLWorksheet ws,
            project_cost_view_model project_vm,
            filter_tab_view_model tab,
            ExcelColumnOptions cols,
            string? date_from,
            string? date_to)
        {
            // 両オプションOFFの場合は出力しない（行を消費しない）
            if (!cols.show_filter_info && !cols.show_custom_rate_info)
                return 1;

            int row = 1;

            // タイトル行（現場名・タブ名）：オプションONなら必ず出力
            string title = $"現場：{project_vm.category_code}_{project_vm.site_name}";
            if (!string.IsNullOrEmpty(tab.tab_name) && tab.tab_name != "全件")
                title += $"／タブ：{tab.tab_name}";
            ws.Cell(row, 1).Value = title;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 11;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = CLR_TITLE;
            row++;

            // 絞り込み条件
            if (cols.show_filter_info)
            {
                // 絞り込み条件文字列を組み立てる
                // tab.filter_content / filter_names などから生成
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(tab.filter_month))
                    parts.Add($"月度：{tab.filter_month}");

                if (!string.IsNullOrWhiteSpace(tab.filter_content))
                {
                    // カンマ区切りのキーワード→「○○」「△△」と整形
                    var kw_list = tab.filter_content
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(k => $"「{k.Trim()}」");
                    string match_label = (tab.filter_match == "exact") ? "完全一致" : "部分一致";
                    parts.Add($"内容：{string.Join("・", kw_list)}（{match_label}）");
                }

                if (!string.IsNullOrWhiteSpace(tab.filter_names))
                {
                    var name_list = tab.filter_names
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(n => n.Trim());
                    parts.Add($"氏名：{string.Join("・", name_list)}");
                }

                if (!string.IsNullOrWhiteSpace(tab.filter_date_from)
                    && !string.IsNullOrWhiteSpace(tab.filter_date_to))
                {
                    parts.Add($"期間：{tab.filter_date_from} 〜 {tab.filter_date_to}");
                }

                // 絞り込み条件が1つも無い場合は「条件なし（全件）」と表示
                string filter_text = parts.Count == 0
                    ? "絞り込み条件：なし（全件表示）"
                    : "絞り込み条件：" + string.Join(" ／ ", parts);

                ws.Cell(row, 1).Value = filter_text;
                ws.Cell(row, 1).Style.Font.FontSize = 10;
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#444444");
                row++;
            }

            // カスタム単価情報
            if (cols.show_custom_rate_info && tab.use_custom_rates)
            {
                // カスタム単価が適用されているタブの場合のみ出力
                // app_settings から標準単価を取得して比較表示するのが理想だが、
                // ここでは「カスタム単価が適用されています」のみシンプルに表示する
                // （詳細単価は filter_tab_rates テーブルにあるが現場ごとに取得が重いため省略）
                ws.Cell(row, 1).Value = "カスタム単価：このタブには現場独自の人件費単価が適用されています";
                ws.Cell(row, 1).Style.Font.FontSize = 10;
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#0066CC");
                ws.Cell(row, 1).Style.Font.Italic = true;
                row++;
            }

            // ダイアログ側で期間指定がある場合（タブ側の filter_date と重複しない場合のみ）
            if (cols.show_filter_info
                && !string.IsNullOrWhiteSpace(date_from)
                && !string.IsNullOrWhiteSpace(date_to)
                && string.IsNullOrWhiteSpace(tab.filter_date_from))
            {
                ws.Cell(row, 1).Value = $"出力期間：{date_from} 〜 {date_to}";
                ws.Cell(row, 1).Style.Font.FontSize = 10;
                ws.Cell(row, 1).Style.Font.FontColor = XLColor.FromHtml("#444444");
                row++;
            }

            // 空行を1行入れて見やすくする
            row++;
            return row;
        }

        // ---- 列ヘッダー行 ----
        private static int write_col_header(IXLWorksheet ws, int row, List<ColDef> defs)
        {
            ws.Row(row).Height = 26;
            for (int i = 0; i < defs.Count; i++)
            {
                var cell = ws.Cell(row, i + 1);
                cell.Value = defs[i].header;
                cell.Style.Fill.BackgroundColor = CLR_COL_HDR;
                cell.Style.Font.FontColor = CLR_WHITE;
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 9;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Alignment.WrapText = true;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = CLR_BORDER;
            }
            return row + 1;
        }

        // ---- 月度ヘッダー ----
        private static int write_month_header(
            IXLWorksheet ws, int row, string month, int col_count)
        {
            var range = ws.Range(row, 1, row, col_count);
            range.Merge();
            range.FirstCell().Value = $"【{month}】";
            range.Style.Fill.BackgroundColor = CLR_MONTH_HDR;
            range.Style.Font.FontColor = CLR_WHITE;
            range.Style.Font.Bold = true;
            range.Style.Font.FontSize = 10;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.OutsideBorderColor = CLR_BORDER;
            ws.Row(row).Height = 16;
            return row + 1;
        }

        // ---- データ行 ----
        private static int write_data_row(
            IXLWorksheet ws, int row, cost_record r, int index, List<ColDef> defs)
        {
            var bg = index % 2 == 1 ? CLR_ALT : CLR_WHITE;
            for (int i = 0; i < defs.Count; i++)
            {
                var cell = ws.Cell(row, i + 1);
                cell.Value = defs[i].get_val(r);
                cell.Style.Fill.BackgroundColor = bg;
                cell.Style.Font.FontSize = 9;
                cell.Style.Alignment.Horizontal = defs[i].is_num
                    ? XLAlignmentHorizontalValues.Right
                    : XLAlignmentHorizontalValues.Left;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Alignment.WrapText = !defs[i].is_num;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = CLR_BORDER;
            }
            // 行高さはExcel自動調整に委ねる（WrapText有効なのでExcel展開時に自動フィット）
            return row + 1;
        }

        // ---- 月度小計 ----
        private static int write_subtotal(
            IXLWorksheet ws, int row, List<cost_record> recs,
            string month, List<ColDef> defs)
        {
            ws.Cell(row, 1).Value = $"【{month} 小計】";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 9;

            // 数値列に小計を計算して記入
            var sums = new Dictionary<string, string>
            {
                ["技師人工(日)"] = fmt_num(recs.Sum(r => r.engineer_days)),
                ["技師時間(h)"] = fmt_num(recs.Sum(r => r.engineer_hours)),
                ["技師人件費"] = fmt_yen(recs.Sum(r => r.engineer_cost)),
                ["助手人工(日)"] = fmt_num(recs.Sum(r => r.assistant_days)),
                ["助手時間(h)"] = fmt_num(recs.Sum(r => r.assistant_hours)),
                ["助手人件費"] = fmt_yen(recs.Sum(r => r.assistant_cost)),
                ["合計人工(日)"] = fmt_num(recs.Sum(r => r.engineer_days + r.assistant_days)),
                ["人件費合計"] = fmt_yen(recs.Sum(r => r.personnel_cost)),
                ["交通費"] = fmt_yen(recs.Sum(r => r.transport_cost)),
                ["損料"] = fmt_yen(recs.Sum(r => r.equipment_cost)),
                ["合計金額"] = fmt_yen(recs.Sum(r => r.total_cost)),
            };

            for (int i = 0; i < defs.Count; i++)
            {
                var cell = ws.Cell(row, i + 1);
                if (sums.TryGetValue(defs[i].header, out var v)) cell.Value = v;
                cell.Style.Fill.BackgroundColor = CLR_SUBTOTAL;
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 9;
                cell.Style.Alignment.Horizontal = defs[i].is_num
                    ? XLAlignmentHorizontalValues.Right
                    : XLAlignmentHorizontalValues.Left;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = CLR_BORDER;
            }
            ws.Row(row).Height = 16;
            return row + 1;
        }

        // ---- 合計バー ----
        private static int write_total(
            IXLWorksheet ws, int row,
            List<cost_record> recs, List<ColDef> defs)
        {
            row++; // 空白行
            string label =
                $"【合計】　技師人件費：{fmt_yen(recs.Sum(r => r.engineer_cost))}円　" +
                $"助手人件費：{fmt_yen(recs.Sum(r => r.assistant_cost))}円　" +
                $"人件費計：{fmt_yen(recs.Sum(r => r.personnel_cost))}円　" +
                $"交通費：{fmt_yen(recs.Sum(r => r.transport_cost))}円　" +
                $"損料：{fmt_yen(recs.Sum(r => r.equipment_cost))}円　" +
                $"合計：{fmt_yen(recs.Sum(r => r.total_cost))}円";

            var range = ws.Range(row, 1, row, defs.Count);
            range.Merge();
            range.FirstCell().Value = label;
            range.Style.Fill.BackgroundColor = CLR_TOTAL;
            range.Style.Font.FontColor = CLR_WHITE;
            range.Style.Font.Bold = true;
            range.Style.Font.FontSize = 10;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            range.Style.Border.OutsideBorderColor = CLR_COL_HDR;
            ws.Row(row).Height = 18;
            return row + 1;
        }

        // ---- 印刷設定 ----
        private static void setup_print(IXLWorksheet ws)
        {
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
            ws.PageSetup.FitToPages(1, 0);
            ws.PageSetup.SetRowsToRepeatAtTop(1, 1); // ▼▼▼ 修正：タイトル行削除により1行に変更
            ws.SheetView.FreezeRows(1);
        }

        // ---- ヘルパー ----
        private static List<cost_record> filter_records(
            IEnumerable<cost_record> records,
            string? date_from, string? date_to)
        {
            var r = records.AsEnumerable();
            if (!string.IsNullOrEmpty(date_from))
                r = r.Where(x => string.Compare(x.record_date, date_from) >= 0);
            if (!string.IsNullOrEmpty(date_to))
                r = r.Where(x => string.Compare(x.record_date, date_to) <= 0);
            return r.ToList();
        }

        // ★v0.9.7修正：日付形式を「M/d」→「M月d日」に変更
        private static string fmt_date(string? d) =>
            string.IsNullOrEmpty(d) ? "" :
            DateTime.TryParse(d, out var dt) ? dt.ToString("M月d日") : d;

        private static string fmt_num(double v) =>
            v == 0 ? "" : v.ToString("0.##");

        private static string fmt_yen(decimal v) =>
            v == 0 ? "" : v.ToString("#,##0");

        private static string make_safe_filename(string name) =>
            string.Concat(name.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        private static string make_safe_sheet_name(string name)
        {
            var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
            string safe = string.Concat(name.Select(c =>
                invalid.Contains(c) ? '_' : c));
            return safe.Length > 31 ? safe[..31] : safe;
        }
    }
}
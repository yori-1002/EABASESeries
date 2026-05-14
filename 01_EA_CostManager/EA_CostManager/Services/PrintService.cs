using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using EA_CostManager.Models;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Services
{
    // ▼▼▼ [Sprint 5G] 追加：印刷列選択オプション ▼▼▼
    /// <summary>印刷時に出力する列を制御するオプション（Excel出力と同様の仕組み）</summary>
    public class PrintColumnOptions
    {
        public bool eng_name { get; set; } = true;  // 技師氏名
        public bool eng_day { get; set; } = true;  // 技師人工(日)
        public bool eng_cost { get; set; } = true;  // 技師人件費
        public bool ast_name { get; set; } = true;  // 助手氏名
        public bool ast_day { get; set; } = true;  // 助手人工(日)
        public bool ast_cost { get; set; } = true;  // 助手人件費
        public bool personnel { get; set; } = true;  // 人件費合計
        public bool transport { get; set; } = true;  // 交通費
        public bool equipment { get; set; } = true;  // 損料
        public bool equip_name { get; set; } = true;  // 機材・使用者

        // ★v0.9.7追加：ヘッダー表示オプション（Excel出力と同様）
        // ON → タブ名直下に絞り込み条件・カスタム単価情報を出力
        // 顧客提出時はOFF推奨、社内用ならONにすると条件が一目で分かる
        public bool show_filter_info { get; set; } = false;
        public bool show_custom_rate_info { get; set; } = false;
    }

    /// <summary>
    /// 印刷用 FlowDocument を生成するサービス
    /// A4横（1122×794 DIU）・1カラム・月度グループ構造
    /// </summary>
    public static class PrintService
    {
        // ---- ページ設定（外部から参照可能にしておく） ----
        public const double PAGE_WIDTH = 1122;  // A4横 幅（96dpi換算）
        public const double PAGE_HEIGHT = 794;   // A4横 高さ
        private const double MARGIN = 48;        // 余白
        private const double CONTENT_W = PAGE_WIDTH - MARGIN * 2;  // 1026（コンテンツ幅）
        // ▼▼▼ 修正：static field ではなく static property にして初期化順序問題を回避 ▼▼▼
        // static field の場合、COL_WIDTHS より前に TABLE_W が初期化されると null 参照例外になる
        private static double TABLE_W => COL_WIDTHS.Sum();

        // ---- 列幅定義（合計 = コンテンツ幅 = PAGE_W - MARGIN*2 = 1026） ----
        // 13列で合計 1018 ≒ 1026（FlowDocumentは若干伸縮させる）
        private static readonly double[] COL_WIDTHS =
        {
            70,  // 作業日
            162, // 作業内容
            66,  // 技師氏名
            52,  // 技師人工(日)
            66,  // 技師人件費
            66,  // 助手氏名
            52,  // 助手人工(日)
            66,  // 助手人件費
            66,  // 人件費合計
            58,  // 交通費
            56,  // 損料
            92,  // 機材・使用者
            66,  // 合計金額
        };

        private static readonly string[] COL_HEADERS =
        {
            "作業日", "作業内容", "技師氏名", "技師\n人工(日)", "技師\n人件費",
            "助手氏名", "助手\n人工(日)", "助手\n人件費",
            "人件費\n合計", "交通費", "損料", "機材・使用者", "合計金額"
        };

        // ---- カラーブラシ ----
        private static readonly Brush BRUSH_HEADER = new SolidColorBrush(Color.FromRgb(46, 117, 182));   // #2E75B6
        private static readonly Brush BRUSH_MONTH_HDR = new SolidColorBrush(Color.FromRgb(46, 117, 182));   // #2E75B6
        private static readonly Brush BRUSH_SUBTOTAL = new SolidColorBrush(Color.FromRgb(184, 212, 238));  // #B8D4EE
        private static readonly Brush BRUSH_ALT = new SolidColorBrush(Color.FromRgb(247, 250, 253));  // #F7FAFD
        private static readonly Brush BRUSH_TOTAL_BAR = new SolidColorBrush(Color.FromRgb(46, 117, 182));   // #2E75B6
        private static readonly Brush BRUSH_WHITE = Brushes.White;
        private static readonly Brush BRUSH_BLACK = Brushes.Black;
        private static readonly Brush BRUSH_BORDER = new SolidColorBrush(Color.FromRgb(204, 221, 238));  // #CCDDEE

        // ---- 公開メソッド ----

        /// <summary>
        /// 印刷対象データから FlowDocument を生成する
        /// </summary>
        /// <param name="project_vm">現場ViewModel（現場名・区分コード取得用）</param>
        /// <param name="print_tabs">印刷するタブのリスト（タブ名・データ）</param>
        /// <param name="date_from">期間フィルター開始日（yyyy-MM-dd。null=無制限）</param>
        /// <param name="date_to">期間フィルター終了日（yyyy-MM-dd。null=無制限）</param>
        /// <param name="col_opts">列選択オプション（null=全列）</param>
        public static FlowDocument build_document(
            project_cost_view_model project_vm,
            IEnumerable<filter_tab_view_model> print_tabs,
            string? date_from = null,
            string? date_to = null,
            PrintColumnOptions? col_opts = null)
        {
            // デフォルト（null）は全列表示
            col_opts ??= new PrintColumnOptions();
            var doc = new FlowDocument
            {
                PageWidth = PAGE_WIDTH,
                PageHeight = PAGE_HEIGHT,
                PagePadding = new Thickness(MARGIN),
                ColumnWidth = double.MaxValue,  // 単一カラム
                FontFamily = new FontFamily("メイリオ, Arial"),
                FontSize = 9,
            };

            bool first_tab = true;

            foreach (var tab in print_tabs)
            {
                // 日付範囲フィルター適用
                var records = tab.raw_records.AsEnumerable();
                if (!string.IsNullOrEmpty(date_from))
                    records = records.Where(r => string.Compare(r.record_date, date_from) >= 0);
                if (!string.IsNullOrEmpty(date_to))
                    records = records.Where(r => string.Compare(r.record_date, date_to) <= 0);
                var recs = records.ToList();

                if (recs.Count == 0) continue;

                // ---- タブセクション ----
                // 2タブ目以降は改ページ
                var section = new Section { BreakPageBefore = !first_tab };
                first_tab = false;

                // ページヘッダー（現場名＋タブ名＋出力日時）
                section.Blocks.Add(build_page_header(project_vm, tab.tab_name, date_from, date_to));

                // ★v0.9.7追加：絞り込み条件・カスタム単価情報を表示するブロックを追加
                // ヘッダー直下に薄い背景色のParagraphで出力する（Excel出力と同様の挙動）
                // どちらのオプションもOFFなら何も追加しない
                if (col_opts.show_filter_info || col_opts.show_custom_rate_info)
                {
                    var info_block = build_filter_info_block(tab, col_opts);
                    if (info_block != null)
                        section.Blocks.Add(info_block);
                }

                // 月度グループ別にテーブルを追加
                var by_month = recs
                    .GroupBy(r => r.fiscal_month ?? "")
                    .OrderBy(g => g.Min(r => r.record_date));

                foreach (var month_group in by_month)
                    section.Blocks.Add(build_month_table(month_group.Key, month_group.ToList(), col_opts));

                // 合計バー
                section.Blocks.Add(build_total_bar(recs));

                doc.Blocks.Add(section);
            }

            if (doc.Blocks.Count == 0)
            {
                var empty = new Section();
                empty.Blocks.Add(new Paragraph(new Run("印刷対象のデータがありません。")));
                doc.Blocks.Add(empty);
            }

            return doc;
        }

        // ---- ページヘッダー ----
        private static Block build_page_header(
            project_cost_view_model project_vm,
            string tab_name,
            string? date_from, string? date_to)
        {
            // ▼▼▼ 修正：データテーブルと同じTABLE_W幅に揃える ▼▼▼
            const double RIGHT_COL = 200;
            double left_col = TABLE_W - RIGHT_COL;

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 6) };
            table.Columns.Add(new TableColumn { Width = new GridLength(left_col) });
            table.Columns.Add(new TableColumn { Width = new GridLength(RIGHT_COL) });

            var rg = new TableRowGroup();
            var row = new TableRow { Background = BRUSH_HEADER };

            // 左：現場名＋タブ名
            string period = "";
            if (!string.IsNullOrEmpty(date_from) && !string.IsNullOrEmpty(date_to))
                period = $"  【期間：{date_from} ～ {date_to}】";

            row.Cells.Add(make_cell(
                $"EA_CostManager  原価集計　{project_vm.category_code}_{project_vm.site_name}　/ {tab_name}{period}",
                BRUSH_WHITE, BRUSH_HEADER, bold: true, font_size: 10, padding: new Thickness(8, 4, 4, 4)));

            // 右：出力日時
            row.Cells.Add(make_cell(
                $"出力：{DateTime.Now:yyyy/MM/dd HH:mm}",
                BRUSH_WHITE, BRUSH_HEADER, align: TextAlignment.Right, padding: new Thickness(4, 4, 8, 4)));

            rg.Rows.Add(row);
            table.RowGroups.Add(rg);
            return table;
        }

        // ★v0.9.7追加：絞り込み条件・カスタム単価情報を表示するブロック
        // FlowDocumentの段落として薄いグレー背景で出力する
        // 戻り値：表示する内容がない場合は null（呼び出し側でスキップ）
        private static Block? build_filter_info_block(
            filter_tab_view_model tab,
            PrintColumnOptions opts)
        {
            // 表示する文字列を組み立て（複数行にする場合は \n で改行）
            var lines = new List<string>();

            // 絞り込み条件
            if (opts.show_filter_info)
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(tab.filter_month))
                    parts.Add($"月度：{tab.filter_month}");

                if (!string.IsNullOrWhiteSpace(tab.filter_content))
                {
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

                // 絞り込み条件が1つもない場合は「全件表示」と明示
                lines.Add(parts.Count == 0
                    ? "絞り込み条件：なし（全件表示）"
                    : "絞り込み条件：" + string.Join(" ／ ", parts));
            }

            // カスタム単価情報
            if (opts.show_custom_rate_info && tab.use_custom_rates)
            {
                lines.Add("カスタム単価：このタブには現場独自の人件費単価が適用されています");
            }

            // 表示する内容がない場合は null を返す（呼び出し側でブロック追加をスキップ）
            if (lines.Count == 0) return null;

            // FlowDocumentのParagraphとして組み立て
            // 薄いグレー背景＋黒文字で印刷時の視認性を確保
            var para = new Paragraph
            {
                Background = new SolidColorBrush(Color.FromRgb(244, 247, 252)), // #F4F7FC
                Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 60)),    // #3C3C3C
                FontSize = 9,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 6),
            };

            // 各行を追加（行間は LineBreak で）
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) para.Inlines.Add(new LineBreak());
                para.Inlines.Add(new Run(lines[i]));
            }

            return para;
        }

        // ---- 月度テーブル（ヘッダー行 + データ行 + 小計行） ----
        private static Block build_month_table(
            string fiscal_month, List<cost_record> recs,
            PrintColumnOptions opts)
        {
            // ▼▼▼ [Sprint 5G] 列オプションに基づいて動的に列定義を構築 ▼▼▼
            // 各列：(ヘッダー文字列, 幅, 固定幅かどうか)
            var cols = new System.Collections.Generic.List<(string hdr, double w)>();
            cols.Add(("作業日", 70));   // 常に表示
            cols.Add(("作業内容", 162));   // 常に表示
            if (opts.eng_name) cols.Add(("技師氏名", 66));
            if (opts.eng_day) cols.Add(("技師\n人工(日)", 52));
            if (opts.eng_cost) cols.Add(("技師\n人件費", 66));
            if (opts.ast_name) cols.Add(("助手氏名", 66));
            if (opts.ast_day) cols.Add(("助手\n人工(日)", 52));
            if (opts.ast_cost) cols.Add(("助手\n人件費", 66));
            if (opts.personnel) cols.Add(("人件費\n合計", 66));
            if (opts.transport) cols.Add(("交通費", 58));
            if (opts.equipment) cols.Add(("損料", 56));
            if (opts.equip_name) cols.Add(("機材・使用者", 92));
            cols.Add(("合計金額", 66));    // 常に表示

            // 列幅をコンテンツ幅に収まるようスケール調整
            double raw_total = cols.Sum(c => c.w);
            double scale = raw_total > 0 ? CONTENT_W / raw_total : 1.0;
            var col_widths = cols.Select(c => c.w * scale).ToArray();
            var col_headers = cols.Select(c => c.hdr).ToArray();

            var table = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 0, 0, 0),
                BorderBrush = BRUSH_BORDER,
                BorderThickness = new Thickness(0.5),
            };
            foreach (var w in col_widths)
                table.Columns.Add(new TableColumn { Width = new GridLength(w) });

            var rg = new TableRowGroup();

            // 月度ヘッダー行（全列スパン）
            var month_row = new TableRow { Background = BRUSH_MONTH_HDR };
            var month_cell = make_cell($"【{fiscal_month}】", BRUSH_WHITE, BRUSH_MONTH_HDR, bold: true);
            month_cell.ColumnSpan = col_widths.Length;
            month_row.Cells.Add(month_cell);
            rg.Rows.Add(month_row);

            // 列ヘッダー行
            var header_row = new TableRow { Background = BRUSH_HEADER };
            for (int i = 0; i < col_headers.Length; i++)
            {
                header_row.Cells.Add(make_cell(
                    col_headers[i], BRUSH_WHITE, BRUSH_HEADER,
                    bold: true, align: TextAlignment.Center, font_size: 8));
            }
            rg.Rows.Add(header_row);

            // データ行
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                var bg = i % 2 == 1 ? BRUSH_ALT : BRUSH_WHITE;
                var data_row = new TableRow { Background = bg };

                data_row.Cells.Add(make_cell(format_date(r.record_date), BRUSH_BLACK, bg));
                data_row.Cells.Add(make_cell(r.work_content ?? "", BRUSH_BLACK, bg));
                if (opts.eng_name) data_row.Cells.Add(make_cell(r.engineer_names ?? "", BRUSH_BLACK, bg));
                if (opts.eng_day) data_row.Cells.Add(make_cell(format_days(r.engineer_days), BRUSH_BLACK, bg, TextAlignment.Right));
                if (opts.eng_cost) data_row.Cells.Add(make_cell(format_yen(r.engineer_cost), BRUSH_BLACK, bg, TextAlignment.Right));
                if (opts.ast_name) data_row.Cells.Add(make_cell(r.assistant_names ?? "", BRUSH_BLACK, bg));
                if (opts.ast_day) data_row.Cells.Add(make_cell(format_days(r.assistant_days), BRUSH_BLACK, bg, TextAlignment.Right));
                if (opts.ast_cost) data_row.Cells.Add(make_cell(format_yen(r.assistant_cost), BRUSH_BLACK, bg, TextAlignment.Right));
                if (opts.personnel) data_row.Cells.Add(make_cell(format_yen(r.personnel_cost), BRUSH_BLACK, bg, TextAlignment.Right));
                if (opts.transport) data_row.Cells.Add(make_cell(format_yen(r.transport_cost), BRUSH_BLACK, bg, TextAlignment.Right));
                if (opts.equipment) data_row.Cells.Add(make_cell(format_yen(r.equipment_cost), BRUSH_BLACK, bg, TextAlignment.Right));
                if (opts.equip_name) data_row.Cells.Add(make_cell(r.equipment_names ?? "", BRUSH_BLACK, bg));
                data_row.Cells.Add(make_cell(format_yen(r.total_cost), BRUSH_BLACK, bg, TextAlignment.Right, bold: true));

                rg.Rows.Add(data_row);
            }

            // 小計行（作業日+作業内容の2列をスパン、残りは各列に数値を出す）
            var sub_row = new TableRow { Background = BRUSH_SUBTOTAL };
            var sub_label = make_cell($"【{fiscal_month}　小計】", BRUSH_BLACK, BRUSH_SUBTOTAL, bold: true, align: TextAlignment.Center);
            sub_label.ColumnSpan = 2; // 作業日・作業内容
            sub_row.Cells.Add(sub_label);

            if (opts.eng_name) sub_row.Cells.Add(make_cell("", BRUSH_BLACK, BRUSH_SUBTOTAL));
            if (opts.eng_day) sub_row.Cells.Add(make_cell(format_days(recs.Sum(r => r.engineer_days)), BRUSH_BLACK, BRUSH_SUBTOTAL, TextAlignment.Right, bold: true));
            if (opts.eng_cost) sub_row.Cells.Add(make_cell(format_yen(recs.Sum(r => r.engineer_cost)), BRUSH_BLACK, BRUSH_SUBTOTAL, TextAlignment.Right, bold: true));
            if (opts.ast_name) sub_row.Cells.Add(make_cell("", BRUSH_BLACK, BRUSH_SUBTOTAL));
            if (opts.ast_day) sub_row.Cells.Add(make_cell(format_days(recs.Sum(r => r.assistant_days)), BRUSH_BLACK, BRUSH_SUBTOTAL, TextAlignment.Right, bold: true));
            if (opts.ast_cost) sub_row.Cells.Add(make_cell(format_yen(recs.Sum(r => r.assistant_cost)), BRUSH_BLACK, BRUSH_SUBTOTAL, TextAlignment.Right, bold: true));
            if (opts.personnel) sub_row.Cells.Add(make_cell(format_yen(recs.Sum(r => r.personnel_cost)), BRUSH_BLACK, BRUSH_SUBTOTAL, TextAlignment.Right, bold: true));
            if (opts.transport) sub_row.Cells.Add(make_cell(format_yen(recs.Sum(r => r.transport_cost)), BRUSH_BLACK, BRUSH_SUBTOTAL, TextAlignment.Right, bold: true));
            if (opts.equipment) sub_row.Cells.Add(make_cell(format_yen(recs.Sum(r => r.equipment_cost)), BRUSH_BLACK, BRUSH_SUBTOTAL, TextAlignment.Right, bold: true));
            if (opts.equip_name) sub_row.Cells.Add(make_cell("", BRUSH_BLACK, BRUSH_SUBTOTAL));
            sub_row.Cells.Add(make_cell(format_yen(recs.Sum(r => r.total_cost)), BRUSH_BLACK, BRUSH_SUBTOTAL, TextAlignment.Right, bold: true));

            rg.Rows.Add(sub_row);
            table.RowGroups.Add(rg);
            return table;
        }

        // ---- 合計バー ----
        private static Block build_total_bar(List<cost_record> recs)
        {
            // ▼▼▼ 修正：データテーブルと同じTABLE_W幅に揃えるためTableを使用 ▼▼▼
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 0) };
            table.Columns.Add(new TableColumn { Width = new GridLength(TABLE_W) });

            string text = $"【合計】　" +
                $"技師人件費：{format_yen(recs.Sum(r => r.engineer_cost))}円　" +
                $"助手人件費：{format_yen(recs.Sum(r => r.assistant_cost))}円　" +
                $"人件費計：{format_yen(recs.Sum(r => r.personnel_cost))}円　" +
                $"交通費：{format_yen(recs.Sum(r => r.transport_cost))}円　" +
                $"損料：{format_yen(recs.Sum(r => r.equipment_cost))}円　" +
                $"合計：{format_yen(recs.Sum(r => r.total_cost))}円";

            var rg = new TableRowGroup();
            var row = new TableRow();
            row.Cells.Add(new TableCell(
                new Paragraph(new Run(text)
                {
                    FontWeight = FontWeights.Bold,
                    FontSize = 9.5,
                })
                {
                    TextAlignment = TextAlignment.Left,
                    Foreground = BRUSH_WHITE,
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0),
                })
            {
                Background = BRUSH_TOTAL_BAR,
                BorderThickness = new Thickness(0),
            });
            rg.Rows.Add(row);
            table.RowGroups.Add(rg);
            return table;
        }

        // ---- TableCell ファクトリ ----
        private static TableCell make_cell(
            string text,
            Brush fg, Brush bg,
            TextAlignment align = TextAlignment.Left,
            bool bold = false,
            double font_size = 8.5,
            Thickness? padding = null)
        {
            var run = new Run(text)
            {
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontSize = font_size,
            };
            var para = new Paragraph(run)
            {
                TextAlignment = align,
                Foreground = fg,
                Padding = padding ?? new Thickness(3, 2, 3, 2),
                Margin = new Thickness(0),
            };
            return new TableCell(para)
            {
                Background = bg,
                BorderBrush = BRUSH_BORDER,
                BorderThickness = new Thickness(0.5),
            };
        }

        // ---- フォーマットヘルパー ----
        private static string format_date(string? d) =>
            string.IsNullOrEmpty(d) ? "" :
            DateTime.TryParse(d, out var dt) ? dt.ToString("M/d") : d;

        private static string format_days(double days) =>
            days == 0 ? "" : days.ToString("0.##");

        private static string format_yen(decimal amount) =>
            amount == 0 ? "" : amount.ToString("#,##0");

        /// <summary>
        /// FlowDocument の DocumentPaginator にページフッターを追加するラッパー
        /// 印刷・プレビュー時に各ページ下部へ「現場名 / ページ X / 全Yページ」を描画する
        /// </summary>
        public static DocumentPaginator create_paginator(
            FlowDocument doc,
            string footer_left_text)
        {
            // ページサイズをセット
            var source = (IDocumentPaginatorSource)doc;
            source.DocumentPaginator.PageSize =
                new Size(PAGE_WIDTH, PAGE_HEIGHT);
            return new footer_paginator(source.DocumentPaginator, footer_left_text);
        }
    }

    /// <summary>
    /// ページフッター（現場名・ページ番号）を各ページに描画する DocumentPaginator ラッパー
    /// PrintService.create_paginator() 経由で使用する
    /// </summary>
    internal class footer_paginator : DocumentPaginator
    {
        private readonly DocumentPaginator _inner;
        private readonly string _footer_left;   // 左フッター（現場名等）
        private const double FOOTER_H = 20;     // フッター高さ（DIU）
        private const double FONT_SIZE = 8.5;

        public footer_paginator(DocumentPaginator inner, string footer_left)
        {
            _inner = inner;
            _footer_left = footer_left;
        }

        public override bool IsPageCountValid => _inner.IsPageCountValid;
        public override int PageCount => _inner.PageCount;
        public override Size PageSize
        {
            get => _inner.PageSize;
            set => _inner.PageSize = value;
        }
        public override IDocumentPaginatorSource Source => _inner.Source;

        public override DocumentPage GetPage(int page_number)
        {
            var page = _inner.GetPage(page_number);
            var visual = new System.Windows.Media.DrawingVisual();

            using (var dc = visual.RenderOpen())
            {
                // 元ページを描画
                dc.DrawRectangle(
                    new VisualBrush(page.Visual),
                    null,
                    new Rect(page.ContentBox.Location, page.ContentBox.Size));

                // フッターのY座標（ページ下部）
                double footer_y = page.Size.Height - FOOTER_H - 4;
                double page_w = page.Size.Width;

                // 区切り線
                dc.DrawLine(
                    new System.Windows.Media.Pen(
                        new SolidColorBrush(Color.FromRgb(180, 200, 220)), 0.5),
                    new Point(32, footer_y),
                    new Point(page_w - 32, footer_y));

                // 左フッター：現場名
                var left_text = make_text(_footer_left, FONT_SIZE, TextAlignment.Left);
                dc.DrawText(left_text, new Point(36, footer_y + 3));

                // 右フッター：ページ番号
                string page_str = $"{page_number + 1} / {PageCount}";
                var right_text = make_text(page_str, FONT_SIZE, TextAlignment.Right);
                dc.DrawText(right_text, new Point(page_w - right_text.Width - 36, footer_y + 3));
            }

            return new DocumentPage(
                visual,
                page.Size,
                page.BleedBox,
                page.ContentBox);
        }

        public override void ComputePageCount() => _inner.ComputePageCount();

        private static FormattedText make_text(
            string text, double font_size, TextAlignment align)
        {
            return new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("メイリオ"),
                font_size,
                Brushes.Gray,
                VisualTreeHelper.GetDpi(new System.Windows.Media.DrawingVisual()).PixelsPerDip)
            {
                TextAlignment = align,
                MaxTextWidth = 300,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
        }
    }
}
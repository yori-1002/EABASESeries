using System.Windows;
using System.Windows.Documents;

namespace EA_CostManager.Views
{
    /// <summary>
    /// 印刷プレビューウィンドウ
    /// FlowDocumentReader を使用してページ送り・ズーム・印刷ができる
    /// </summary>
    public partial class PrintPreviewWindow : Window
    {
        private readonly FlowDocument _doc;

        public PrintPreviewWindow(FlowDocument doc)
        {
            InitializeComponent();
            _doc = doc;
            // FlowDocumentReader にドキュメントをセット
            doc_reader.Document = doc;
        }

        // ---- 印刷ボタン ----
        private void btn_print_Click(object sender, RoutedEventArgs e)
        {
            var pd = new System.Windows.Controls.PrintDialog();
            if (pd.ShowDialog() != true) return;

            // A4横レイアウト
            pd.PrintTicket.PageOrientation = System.Printing.PageOrientation.Landscape;

            IDocumentPaginatorSource source = _doc;
            pd.PrintDocument(source.DocumentPaginator, "EA_CostManager 原価集計");
        }

        // ---- 閉じるボタン ----
        private void btn_close_Click(object sender, RoutedEventArgs e) => Close();
    }
}

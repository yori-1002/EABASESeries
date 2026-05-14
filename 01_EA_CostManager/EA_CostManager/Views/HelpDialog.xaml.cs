using System.Windows;

namespace EA_CostManager.Views
{
    /// <summary>
    /// Sprint 5G: ヘルプダイアログ（F1で表示）
    /// ショートカットキー一覧と操作ボタンの説明を表示する
    /// </summary>
    public partial class HelpDialog : Window
    {
        public HelpDialog()
        {
            InitializeComponent();
        }

        private void btn_close_Click(object sender, RoutedEventArgs e) => Close();
    }
}

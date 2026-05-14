using System.Windows;

namespace EA_CostManager.Views
{
    // ▼▼▼ 追加：RatesDialogAction 列挙型（このファイルで一元定義） ▼▼▼
    // CostPage.xaml.cs・ProjectRatesDialog.xaml.cs・rates_action_dialog.xaml.cs から共通参照
    public enum RatesDialogAction
    {
        None,                    // 後で反映（何もしない）
        ReAggregate,             // すべてのタブで再集計
        ReAggregateCurrentTab,   // ▼▼▼ 追加：このタブのみ再集計 ▼▼▼
        CreateNewTab,            // 単価変更で新規タブ作成
        ResetAll,                // 全タブリセット（project_rates削除→デフォルト単価で再集計）
        ResetCurrentTab,         // このタブのみリセット（project_ratesは保持）
    }

    /// <summary>
    /// 現場別単価保存後の操作選択ダイアログ（4択）
    /// </summary>
    public partial class rates_action_dialog : Window
    {
        public RatesDialogAction selected_action { get; private set; } = RatesDialogAction.None;

        public rates_action_dialog()
        {
            InitializeComponent();
        }

        private void btn_reaggregate_Click(object sender, RoutedEventArgs e)
        {
            selected_action = RatesDialogAction.ReAggregate;
            DialogResult = true;
            Close();
        }

        // ▼▼▼ 追加：このタブのみ再集計 ▼▼▼
        private void btn_reaggregate_current_Click(object sender, RoutedEventArgs e)
        {
            selected_action = RatesDialogAction.ReAggregateCurrentTab;
            DialogResult = true;
            Close();
        }

        private void btn_new_tab_Click(object sender, RoutedEventArgs e)
        {
            selected_action = RatesDialogAction.CreateNewTab;
            DialogResult = true;
            Close();
        }

        private void btn_later_Click(object sender, RoutedEventArgs e)
        {
            selected_action = RatesDialogAction.None;
            DialogResult = false;
            Close();
        }
    }
}
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EA_CostManager.Models;

namespace EA_CostManager.Views
{
    /// <summary>
    /// アーカイブ済み絞り込みタブの選択復元ダイアログ
    /// チェックボックス一覧から復元するタブを選択できる
    /// </summary>
    public partial class ArchivedTabsDialog : Window
    {
        // 表示対象のアーカイブ済みタブ一覧
        private readonly List<cost_filter_tab> _archived_tabs;

        // ユーザーが復元を選んだタブ（ダイアログ終了後に参照）
        public List<cost_filter_tab> selected_tabs { get; private set; } = new();

        public ArchivedTabsDialog(List<cost_filter_tab> archived_tabs)
        {
            InitializeComponent();
            _archived_tabs = archived_tabs;
            build_checkbox_list();
        }

        /// <summary>チェックボックス一覧を動的に生成</summary>
        private void build_checkbox_list()
        {
            panel_tabs.Children.Clear();
            foreach (var tab in _archived_tabs)
            {
                var cb = new CheckBox
                {
                    Content = tab.tab_name,
                    Tag = tab,
                    IsChecked = false,
                    Style = (Style)Resources["ItemStyle"]
                };

                // フィルター条件を補足テキストとして表示
                string hint = build_hint(tab);
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock
                    {
                        Text = tab.tab_name,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold
                    });
                    sp.Children.Add(new TextBlock
                    {
                        Text = hint,
                        FontSize = 10,
                        Foreground = System.Windows.Media.Brushes.Gray,
                        Margin = new Thickness(0, 1, 0, 0)
                    });
                    cb.Content = sp;
                }

                panel_tabs.Children.Add(cb);
            }
        }

        /// <summary>タブの絞り込み条件を補足テキストに変換</summary>
        private static string build_hint(cost_filter_tab tab)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(tab.filter_month)) parts.Add($"月度:{tab.filter_month}");
            if (!string.IsNullOrWhiteSpace(tab.filter_content)) parts.Add($"作業:{tab.filter_content}");
            if (!string.IsNullOrWhiteSpace(tab.filter_names)) parts.Add($"氏名:{tab.filter_names}");
            if (tab.is_single_mode == 1) parts.Add("個人別集計");
            return string.Join(" / ", parts);
        }

        // ---- ボタンイベント ----

        private void btn_select_all_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox cb in panel_tabs.Children)
                cb.IsChecked = true;
        }

        private void btn_deselect_all_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox cb in panel_tabs.Children)
                cb.IsChecked = false;
        }

        private void btn_restore_Click(object sender, RoutedEventArgs e)
        {
            selected_tabs = panel_tabs.Children
                .OfType<CheckBox>()
                .Where(cb => cb.IsChecked == true && cb.Tag is cost_filter_tab)
                .Select(cb => (cost_filter_tab)cb.Tag)
                .ToList();

            if (selected_tabs.Count == 0)
            {
                MessageBox.Show(
                    "復元するタブを1つ以上選択してください。",
                    "選択なし",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
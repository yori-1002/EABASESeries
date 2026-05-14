using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Views
{
    /// <summary>
    /// ★v0.9.7追加：複数選択された現場タブに対して共通属性を一括変更するダイアログ
    /// CostPage の Ctrl+クリック / Shift+クリックで複数選択 → 右クリックメニュー
    /// → 「選択中のN件の属性を変更」から呼び出される
    /// </summary>
    public partial class BulkAttributeDialog : Window
    {
        // ▼ 一括変更対象のタブリスト（コンストラクタで渡される）
        // CostPage 側では _multi_selected_tabs の中身がそのまま渡される
        private readonly List<project_cost_view_model> _target_tabs;

        // ▼ 選択された属性名（OK時にCostPage側がここから取得する）
        public string selected_attribute { get; private set; } = "";

        public BulkAttributeDialog(List<project_cost_view_model> target_tabs)
        {
            InitializeComponent();

            // ▼ 引数のタブリストを保持（後でOKボタンで使用するためフィールドに格納）
            _target_tabs = target_tabs ?? new List<project_cost_view_model>();

            // ▼ ヘッダーテキストに件数を表示
            txt_header.Text = $"{_target_tabs.Count} 件のタブの属性を変更します";

            // ▼ 対象タブ一覧テキストを構築
            // 4件以下：すべて表示
            // 5件以上：先頭4件 + 「他 N 件」と省略表示する（ダイアログ高さの肥大化を防ぐ）
            const int MAX_DISPLAY = 4;
            if (_target_tabs.Count <= MAX_DISPLAY)
            {
                txt_target_list.Text = string.Join("\n",
                    _target_tabs.Select(t => $"・{t.tab_name}"));
            }
            else
            {
                var first_part = _target_tabs.Take(MAX_DISPLAY)
                    .Select(t => $"・{t.tab_name}");
                int remaining = _target_tabs.Count - MAX_DISPLAY;
                txt_target_list.Text = string.Join("\n", first_part)
                    + $"\n他 {remaining} 件";
            }

            // ▼ デフォルトで「契約済」を選択状態にする（最も使用頻度が高い属性）
            cmb_attribute.SelectedIndex = 1;
        }

        // ▼ 一括適用ボタン：選択された属性を保持してダイアログを閉じる
        // 実際のDB更新は呼び出し元の CostPage 側で行う（Undo履歴・操作ログとセットで処理する必要があるため）
        private void btn_apply_Click(object sender, RoutedEventArgs e)
        {
            // ▼ 属性が選択されていない場合は警告を出して閉じない
            if (cmb_attribute.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
            {
                MessageBox.Show("属性を選択してください。",
                    "確認",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            selected_attribute = item.Content?.ToString() ?? "";
            DialogResult = true;
            Close();
        }

        // ▼ キャンセルボタン：選択属性を空のまま閉じる（呼び出し元はDialogResult==falseで判定する）
        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

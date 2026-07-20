using EA_CostManager.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EA_CostManager.Views
{
    /// <summary>
    /// ▼ 追加 [Sprint 7C-1]：工数表の分類設定ダイアログ
    ///
    /// 【保存反映方式】
    /// 編集内容はすべてViewModel上（メモリ）で保持し、「保存」を押したときだけDBへ反映する。
    /// キャンセルすれば編集はすべて破棄されるため、区分やキーワードを試行錯誤しやすい。
    ///
    /// 【呼び出し側】
    /// WorkloadPage が ShowDialog() で開き、true が返った場合のみ再集計を行う。
    /// </summary>
    public partial class WorkloadClassifyDialog : Window
    {
        private readonly workload_classify_view_model _vm;

        public WorkloadClassifyDialog(int project_id, string project_title)
        {
            InitializeComponent();
            _vm = new workload_classify_view_model(project_id, project_title);
            DataContext = _vm;

            // 読み込みは表示後に実行する（コンストラクタでawaitできないため）
            Loaded += async (_, _) => await _vm.load_async();
        }

        /// <summary>
        /// 「保存」ボタン。
        ///
        /// ▼ 修正 [7C-fix4]：保存前にDataGridのセル編集を強制コミットする。
        ///   WPFのDataGridは、セルが編集モードのままだと入力値が
        ///   バインディングソース（edit_subgroup.name / keywords_text）へ
        ///   書き戻されない（既定の UpdateSourceTrigger は LostFocus のため）。
        ///   「保存」ボタンのクリックではセルからフォーカスが外れないケースがあり、
        ///   その場合「画面には入力した文字が見えているのに保存されない」という
        ///   不具合になっていた。CommitEdit() を明示的に呼んで確定させる。
        ///
        /// 保存に失敗した場合（区分名が空・重複など）はダイアログを閉じず、修正できるようにする。
        /// </summary>
        private async void save_click(object sender, RoutedEventArgs e)
        {
            if (ReadOnlyGuard.block_if_read_only(this)) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            commit_pending_edits();

            bool ok = await _vm.save_async();
            if (!ok) return;   // 入力エラー等 → 閉じない
            DialogResult = true;
        }

        /// <summary>
        /// 編集途中のセルを確定させる。
        /// CommitEdit は「セル単位」と「行単位」の2段階があるため、両方を順に呼ぶ必要がある
        /// （セルだけコミットしても、行が編集中のままだと値が確定しない）。
        /// </summary>
        private void commit_pending_edits()
        {
            // フォーカス中のコントロール（TextBox等）のバインディングも確定させる
            // ※ サブ分類グリッド以外の入力欄で編集中だった場合の保険
            var focused = Keyboard.FocusedElement as FrameworkElement;
            focused?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            if (subgroup_grid == null) return;

            // セル → 行 の順にコミットする（順序が逆だと確定しない）
            subgroup_grid.CommitEdit(DataGridEditingUnit.Cell, true);
            subgroup_grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }
}
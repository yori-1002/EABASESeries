using System.Windows;

namespace EA_CostManager.Views
{
    /// <summary>
    /// タブ名入力ダイアログ
    /// 「単価変更で新規タブ作成」時およびタブ名変更（右クリック）時に使用する
    /// </summary>
    public partial class TabNameInputDialog : Window
    {
        /// <summary>ユーザーが入力・確定したタブ名（OKボタン押下時のみセットされる）</summary>
        public string input_name { get; private set; } = "";

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="initial_name">初期表示するタブ名（空文字=空欄で開く）</param>
        public TabNameInputDialog(string initial_name = "")
        {
            InitializeComponent();

            // 既存のタブ名を初期値としてセット・全選択状態にする
            txt_name.Text = initial_name;
            Loaded += (_, _) =>
            {
                txt_name.SelectAll();
                txt_name.Focus();
            };
        }

        // ---- OK ボタン ----
        private void btn_ok_Click(object sender, RoutedEventArgs e)
        {
            string name = txt_name.Text.Trim();

            // 空欄チェック
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(
                    "タブ名を入力してください。",
                    "入力エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txt_name.Focus();
                return;
            }

            input_name = name;
            DialogResult = true;
            Close();
        }

        // ---- キャンセル ボタン ----
        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
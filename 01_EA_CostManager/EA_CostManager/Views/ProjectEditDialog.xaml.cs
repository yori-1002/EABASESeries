using System.Windows;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.Views
{
    /// <summary>
    /// 現場名・詳細・区分コードの編集ダイアログ
    /// 右クリックメニュー「現場名・詳細を編集」から呼び出す
    /// ▼▼▼ 修正⑤：区分コード（category_code）編集フィールドを追加
    /// </summary>
    public partial class ProjectEditDialog : Window
    {
        private readonly int _project_id;

        // ▼▼▼ 追加⑤：変更後の区分コードを呼び出し元から参照できるように公開
        public string new_category_code { get; private set; } = "";

        // ▼▼▼ 修正⑤：コンストラクタに category_code 引数を追加
        // 呼び出し側（CostPage.xaml.cs の edit_project ケース）も引数追加が必要
        public ProjectEditDialog(int project_id, string category_code, string site_name, string detail)
        {
            InitializeComponent();
            _project_id = project_id;
            txt_category_code.Text = category_code;
            txt_site_name.Text = site_name;
            txt_detail.Text = detail;
        }

        private async void btn_save_Click(object sender, RoutedEventArgs e)
        {
            if (ReadOnlyGuard.block_if_read_only(this)) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            // ▼▼▼ 区分コードのバリデーション
            string new_cat = txt_category_code.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(new_cat))
            {
                txt_error.Text = "区分コードは必須です";
                txt_error.Visibility = Visibility.Visible;
                return;
            }

            string site_name = txt_site_name.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(site_name))
            {
                txt_error.Text = "現場名は必須です";
                txt_error.Visibility = Visibility.Visible;
                return;
            }

            txt_error.Visibility = Visibility.Collapsed;

            try
            {
                using var conn = database_manager.create_connection();

                // ▼▼▼ 修正⑤：projects 更新前に現在の category_code を取得しておく
                // UPDATE 後に SELECT すると新しい値が返るため、先に取得する必要がある
                string old_cat = await conn.ExecuteScalarAsync<string>(
                    "SELECT category_code FROM projects WHERE id = @id",
                    new { id = _project_id }) ?? "";

                // projects テーブルを更新（区分コード・現場名・詳細）
                await conn.ExecuteAsync(@"
                    UPDATE projects
                    SET category_code = @cat,
                        site_name     = @site,
                        detail        = @det,
                        updated_at    = datetime('now','localtime')
                    WHERE id = @id",
                    new
                    {
                        cat = new_cat,
                        site = site_name,
                        det = txt_detail.Text.Trim(),
                        id = _project_id
                    });

                // ▼▼▼ 追加⑤：区分コードが変わった場合のみ cost_records も連動更新
                // old_cat で WHERE して new_cat にセットする（UPDATE前の値を使うのが正しい）
                if (old_cat != new_cat && !string.IsNullOrEmpty(old_cat))
                {
                    await conn.ExecuteAsync(@"
                        UPDATE cost_records
                        SET category_code = @new_cat
                        WHERE category_code = @old_cat",
                        new { new_cat, old_cat });
                }

                new_category_code = new_cat;

                DialogResult = true;
                Close();
            }
            catch (System.Exception ex)
            {
                txt_error.Text = $"保存エラー：{ex.Message}";
                txt_error.Visibility = Visibility.Visible;
            }
        }

        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
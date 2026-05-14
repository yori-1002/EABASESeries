using System.Windows;
using System.Windows.Controls;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.Views
{
    /// <summary>
    /// 初回起動時のユーザー登録ダイアログ
    /// employeesテーブルから氏名を選択するか手動入力してPCに紐付ける
    /// </summary>
    public partial class FirstLoginDialog : Window
    {
        // 登録結果
        public string registered_name { get; private set; } = "";
        public int registered_employee_id { get; private set; } = 0;

        // employeesのリスト（コンボボックス用）
        private List<(int id, string name)> _employee_list = new();

        public FirstLoginDialog()
        {
            InitializeComponent();
            _ = load_employees_async();
        }

        // employeesテーブルから社員リストを読み込む
        private async Task load_employees_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var employees = await conn.QueryAsync<(int id, string name)>(
                    "SELECT id, employee_name FROM employees WHERE is_active = 1 ORDER BY employee_name");

                _employee_list = employees.ToList();

                // コンボボックスに表示
                cmb_employees.Items.Clear();
                cmb_employees.Items.Add(new ComboBoxItem
                {
                    Content = "--- 選択してください ---",
                    Tag = 0
                });

                foreach (var (id, name) in _employee_list)
                {
                    cmb_employees.Items.Add(new ComboBoxItem
                    {
                        Content = name,
                        Tag = id
                    });
                }
                cmb_employees.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                lbl_error.Text = $"社員リストの読み込みに失敗しました：{ex.Message}";
            }
        }

        // コンボボックス選択変更
        private void cmb_employees_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmb_employees.SelectedItem is ComboBoxItem item && item.Tag is int id && id > 0)
            {
                // 社員選択したらテキストボックスに氏名をセット
                txt_name.Text = item.Content?.ToString() ?? "";
                txt_name.IsEnabled = false; // 選択中は手動入力を無効化
            }
            else
            {
                txt_name.IsEnabled = true;
            }
            update_register_button();
        }

        // テキストボックス変更
        private void txt_name_TextChanged(object sender, TextChangedEventArgs e)
        {
            update_register_button();
        }

        // 登録ボタンの有効/無効を更新
        private void update_register_button()
        {
            bool has_name = !string.IsNullOrWhiteSpace(txt_name.Text);
            btn_register.IsEnabled = has_name;
        }

        // 登録ボタンクリック
        private async void btn_register_Click(object sender, RoutedEventArgs e)
        {
            string name = txt_name.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                lbl_error.Text = "氏名を入力または選択してください。";
                return;
            }

            btn_register.IsEnabled = false;
            lbl_error.Text = "";

            try
            {
                string mac = UserSession.fetch_mac_address();
                string pc = UserSession.fetch_pc_name();

                // 選択した社員IDを取得
                int emp_id = 0;
                if (cmb_employees.SelectedItem is ComboBoxItem item && item.Tag is int id && id > 0)
                    emp_id = id;

                using var conn = database_manager.create_connection();

                // pc_usersに登録済みのユーザー数を確認（最初のユーザーは管理者に）
                int existing_count = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM pc_users");
                int is_admin = existing_count == 0 ? 2 : 1; // 0=閲覧/1=一般/2=管理者。最初のユーザーは管理者、以降は一般

                // pc_usersに登録
                await conn.ExecuteAsync(@"
                    INSERT OR REPLACE INTO pc_users
                        (mac_address, pc_name, employee_id, user_name, is_admin, created_at, updated_at)
                    VALUES
                        (@mac, @pc, @emp_id, @name, @is_admin,
                         datetime('now','localtime'), datetime('now','localtime'))",
                    new { mac, pc, emp_id, name, is_admin });

                int new_id = await conn.ExecuteScalarAsync<int>(
                    "SELECT id FROM pc_users WHERE mac_address = @mac", new { mac });

                registered_name = name;
                registered_employee_id = emp_id;

                // UserSessionに設定
                UserSession.set(new_id, name, emp_id, is_admin == 2);

                // ▼▼▼ 追加：ユーザー登録の操作ログを書き込む ▼▼▼
                // 権限ラベルを文字列に変換して詳細に含める
                string role_label = is_admin switch { 2 => "管理者", 1 => "一般", _ => "閲覧" };
                try
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO operation_logs
                            (log_datetime, pc_user_id, operator_name, operation_type, detail)
                        VALUES
                            (datetime('now','localtime'), @uid, @name, 'ユーザー登録', @detail)",
                        new
                        {
                            uid = new_id,
                            name = name,
                            detail = $"{name}（{role_label}）が {pc} で登録"
                        });
                }
                catch { /* ログ失敗は無視 */ }

                if (is_admin == 2)
                {
                    MessageBox.Show(
                        $"ようこそ、{name} さん！\n\n最初のユーザーのため、管理者権限が付与されました。",
                        "登録完了",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                lbl_error.Text = $"登録に失敗しました：{ex.Message}";
                btn_register.IsEnabled = true;
            }
        }
    }
}
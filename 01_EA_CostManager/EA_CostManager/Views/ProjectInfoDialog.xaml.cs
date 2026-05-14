using System.Linq;
using System.Windows;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.Views
{
    /// <summary>
    /// 新規現場情報入力ダイアログ
    /// 取込データに未登録の区分コードが現れたときに表示
    /// daily_reports.project_name を候補として ComboBox に表示
    /// </summary>
    public partial class ProjectInfoDialog : Window
    {
        public string category_code { get; }
        public bool was_registered { get; private set; }

        // ▼▼▼ 修正：_color_map を廃止し project_cost_view_model.get_tab_color に一元化 ▼▼▼
        // 新属性（第一土木・セミナー・その他・契約中オレンジ変更）もここで自動反映される

        public ProjectInfoDialog(string cat_code)
        {
            InitializeComponent();
            category_code = cat_code;
            txt_category_code.Text = cat_code;
            Loaded += async (_, _) => await load_site_name_candidates_async();
        }

        /// <summary>
        /// daily_reports から当該区分コードの project_name を取得して候補にセット
        /// </summary>
        private async System.Threading.Tasks.Task load_site_name_candidates_async()
        {
            try
            {
                using var conn = database_manager.create_connection();

                // 区分コードに紐づく現場名を出現頻度順で取得
                var candidates = (await conn.QueryAsync<string>(@"
                    SELECT project_name
                    FROM daily_reports
                    WHERE category_code = @cat
                      AND project_name IS NOT NULL
                      AND project_name != ''
                    GROUP BY project_name
                    ORDER BY COUNT(*) DESC",
                    new { cat = category_code })).ToList();

                txt_site_name.Items.Clear();
                foreach (var name in candidates)
                    txt_site_name.Items.Add(name);

                // 最頻出の現場名をデフォルト表示
                if (candidates.Count > 0)
                    txt_site_name.Text = candidates[0];
            }
            catch
            {
                // 候補取得失敗はサイレントに無視（手入力で対応可能）
            }
        }

        private async void btn_register_Click(object sender, RoutedEventArgs e)
        {
            string site_name = txt_site_name.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(site_name))
            {
                txt_error.Text = "現場名は必須です";
                txt_error.Visibility = Visibility.Visible;
                return;
            }

            txt_error.Visibility = Visibility.Collapsed;

            string attribute = (cmb_attribute.SelectedItem as System.Windows.Controls.ComboBoxItem)
                               ?.Content?.ToString() ?? "契約中";
            // get_tab_color に一元化（project_cost_view_model.cs で管理）
            string tab_color = EA_CostManager.ViewModels.project_cost_view_model.get_tab_color(attribute);

            try
            {
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(@"
                    INSERT OR IGNORE INTO projects
                        (category_code, site_name, detail, attribute, tab_color, is_active)
                    VALUES
                        (@cat, @site, @detail, @attr, @color, 1)",
                    new
                    {
                        cat = category_code,
                        site = site_name,
                        detail = txt_detail.Text.Trim(),
                        attr = attribute,
                        color = tab_color
                    });

                was_registered = true;
                // ▼▼▼ 修正：ShowDialog以外から呼ばれた場合のエラー回避 ▼▼▼
                try { DialogResult = true; } catch { }
                Close();
            }
            catch (System.Exception ex)
            {
                txt_error.Text = $"登録エラー：{ex.Message}";
                txt_error.Visibility = Visibility.Visible;
            }
        }

        private void btn_skip_Click(object sender, RoutedEventArgs e)
        {
            was_registered = false;
            // ▼▼▼ 修正：ShowDialog以外から呼ばれた場合のエラー回避 ▼▼▼
            try { DialogResult = false; } catch { }
            Close();
        }
    }
}
using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace EA_CostManager.Views
{
    /// <summary>
    /// ▼ 追加 [Sprint 7C-1]：コピー対象として表示する区分1件
    /// </summary>
    public class copy_category_item : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void notify([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public int id { get; set; }
        public string name { get; set; } = "";

        /// <summary>この区分に登録されている全キーワード（区分名キーワードを含む）</summary>
        public List<string> all_keywords { get; set; } = new();

        /// <summary>区分名がキーワードとして登録されているか（コピー先でも同じ設定にする）</summary>
        public bool use_name_as_keyword { get; set; }

        /// <summary>コピー先に持っていく追加キーワード（区分名キーワードを除いたもの）</summary>
        public List<string> keywords =>
            all_keywords.Where(k => !string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>一覧に表示するキーワード文字列</summary>
        public string keywords_display => string.Join(" / ", all_keywords);

        private bool _is_checked;
        public bool is_checked
        {
            get => _is_checked;
            set { if (_is_checked == value) return; _is_checked = value; notify(); }
        }
    }

    /// <summary>コピー元として選べる案件1件</summary>
    public class copy_source_project
    {
        public int id { get; set; }
        public string category_code { get; set; } = "";
        public string site_name { get; set; } = "";
        public int category_count { get; set; }
        public string display_name => $"{category_code}　{site_name}　（区分 {category_count} 件）";
    }

    /// <summary>
    /// ▼ 追加 [Sprint 7C-1]：他案件から業務区分をコピーするダイアログ
    ///
    /// ・コピー元の案件は「業務区分が1件以上登録されている案件」のみを候補に出す
    ///   （区分が無い案件を選んでも意味がないため）。自分自身は候補から除外する。
    /// ・選択された区分は selected_categories に格納され、呼び出し元
    ///  （workload_classify_view_model）が編集中リストへ「新規」として追加する。
    ///   この時点ではDBに書き込まれず、分類設定ダイアログで「保存」を押して初めて確定する。
    /// </summary>
    public partial class WorkloadCopyDialog : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void notify([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        private readonly int _exclude_project_id;

        /// <summary>取り込む区分（「取り込む」を押したときに確定する）</summary>
        public List<copy_category_item> selected_categories { get; private set; } = new();

        public ObservableCollection<copy_source_project> source_projects { get; } = new();
        public ObservableCollection<copy_category_item> source_categories { get; } = new();

        private copy_source_project? _selected_source;
        public copy_source_project? selected_source
        {
            get => _selected_source;
            set
            {
                if (_selected_source == value) return;
                _selected_source = value;
                notify();
                _ = load_categories_async();
            }
        }

        private string _status_message = "";
        public string status_message
        {
            get => _status_message;
            set { _status_message = value; notify(); }
        }

        public ICommand check_all_command { get; }
        public ICommand uncheck_all_command { get; }

        public WorkloadCopyDialog(int exclude_project_id)
        {
            InitializeComponent();
            _exclude_project_id = exclude_project_id;
            DataContext = this;

            check_all_command = new RelayCommand(() =>
            {
                foreach (var c in source_categories) c.is_checked = true;
            });
            uncheck_all_command = new RelayCommand(() =>
            {
                foreach (var c in source_categories) c.is_checked = false;
            });

            Loaded += async (_, _) => await load_projects_async();
        }

        /// <summary>区分が登録されている案件だけをコピー元の候補として読み込む</summary>
        private async System.Threading.Tasks.Task load_projects_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var rows = (await conn.QueryAsync<copy_source_project>(@"
                    SELECT p.id, p.category_code, p.site_name,
                           COUNT(c.id) AS category_count
                    FROM projects p
                    JOIN workload_categories c
                      ON c.project_id = p.id AND c.is_active = 1
                    WHERE p.id <> @exclude
                    GROUP BY p.id, p.category_code, p.site_name
                    HAVING COUNT(c.id) > 0
                    ORDER BY p.sort_order, p.id",
                    new { exclude = _exclude_project_id })).ToList();

                source_projects.Clear();
                foreach (var r in rows) source_projects.Add(r);

                if (source_projects.Count == 0)
                {
                    status_message = "コピー元として使用できる案件がございません。"
                                   + "（業務区分が登録されている案件が他にありません）";
                    return;
                }
                selected_source = source_projects[0];
            }
            catch (Exception ex)
            {
                status_message = $"案件一覧の読み込みに失敗いたしました：{ex.Message}";
            }
        }

        /// <summary>選択されたコピー元案件の区分・キーワードを読み込む</summary>
        private async System.Threading.Tasks.Task load_categories_async()
        {
            source_categories.Clear();
            if (_selected_source == null) return;

            try
            {
                using var conn = database_manager.create_connection();
                var cats = (await conn.QueryAsync<dynamic>(@"
                    SELECT id, name FROM workload_categories
                    WHERE project_id = @p AND is_active = 1
                    ORDER BY sort_order, id",
                    new { p = _selected_source.id })).ToList();

                var kws = (await conn.QueryAsync<dynamic>(@"
                    SELECT k.category_id, k.keyword
                    FROM workload_category_keywords k
                    JOIN workload_categories c ON c.id = k.category_id
                    WHERE c.project_id = @p AND c.is_active = 1
                    ORDER BY k.priority, k.id",
                    new { p = _selected_source.id })).ToList();

                foreach (var c in cats)
                {
                    int cid = Convert.ToInt32(c.id);
                    string cname = (string)(c.name ?? "");
                    var words = kws.Where(k => Convert.ToInt32(k.category_id) == cid)
                                   .Select(k => (string)(k.keyword ?? ""))
                                   .Where(k => k.Length > 0)
                                   .ToList();

                    source_categories.Add(new copy_category_item
                    {
                        id = cid,
                        name = cname,
                        all_keywords = words,
                        // 区分名と同じキーワードがあれば「区分名をキーワードとして使う」設定だったとみなす
                        use_name_as_keyword = words.Any(
                            w => string.Equals(w, cname, StringComparison.OrdinalIgnoreCase)),
                        is_checked = true,   // 既定で全選択（不要なものだけ外す運用を想定）
                    });
                }
                status_message = $"{source_categories.Count} 件の区分が見つかりました。";
            }
            catch (Exception ex)
            {
                status_message = $"区分の読み込みに失敗いたしました：{ex.Message}";
            }
        }

        private void import_click(object sender, RoutedEventArgs e)
        {
            selected_categories = source_categories.Where(c => c.is_checked).ToList();
            if (selected_categories.Count == 0)
            {
                MessageBox.Show("取り込む区分が選択されておりません。",
                    "確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        }
    }
}

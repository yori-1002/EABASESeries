using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dapper;
using EA_DailyReport.Data;

namespace EA_DailyReport.Views.Dialogs
{
    /// <summary>
    /// 区分リストダイアログ（v0.1.12 Phase 2 新規実装）
    ///
    /// ▼ 概要：
    /// InputDialog の「区分リスト」ボタンから呼び出されるモーダルサブダイアログ。
    /// projects テーブル(is_active=1)の一覧を表示し、検索フィルター + ダブルクリックで
    /// 区分を選択して InputDialog に戻す。
    ///
    /// ▼ レイアウト：
    /// ・上部：検索 TextBox（区分コード・業務名・区分詳細を全文検索・リアルタイム絞り込み）
    /// ・中央：ListView（区分コード / 業務名 / 区分詳細 の3列）
    /// ・下部：キャンセルボタン + ヒント
    ///
    /// ▼ 呼び出し側のサンプル（InputDialog 側）：
    ///     var dlg = new CategoryListDialog { Owner = this };
    ///     if (dlg.ShowDialog() == true
    ///         &amp;&amp; !string.IsNullOrWhiteSpace(dlg.selected_category_code))
    ///     {
    ///         txt_category.Text = dlg.selected_category_code;
    ///         await load_category_detail_async();  // 業務名・区分詳細を自動取得
    ///     }
    ///
    /// ▼ 戻り値：
    /// ・ShowDialog() == true → ユーザーが区分を選択した
    ///   → selected_category_code に値がセットされている
    /// ・ShowDialog() == false → ユーザーがキャンセル / 閉じるボタン
    ///   → selected_category_code は null
    /// </summary>
    public partial class CategoryListDialog : Window
    {
        // ════════════════════════════════════════════════
        // ===== フィールド =================================
        // ════════════════════════════════════════════════

        /// <summary>
        /// 全件キャッシュ。
        /// DB から取得した全件をここに保持し、検索フィルターで _all_projects → 表示の絞り込みを行う。
        /// 都度 DB クエリを発行しないことでレスポンスを高速化する。
        /// </summary>
        private List<project_list_item> _all_projects = new();

        // ════════════════════════════════════════════════
        // ===== 公開プロパティ ============================
        // ════════════════════════════════════════════════

        /// <summary>
        /// 選択された区分コード。
        /// ShowDialog() == true で返ったときのみ値がセットされる。
        /// キャンセル時は null のまま。
        /// </summary>
        public string? selected_category_code { get; private set; }

        // ════════════════════════════════════════════════
        // ===== コンストラクタ・初期化 ====================
        // ════════════════════════════════════════════════

        public CategoryListDialog()
        {
            InitializeComponent();
            Loaded += on_loaded;
        }

        /// <summary>
        /// Window Loaded：DB から区分一覧をロード + 検索欄に初期フォーカス
        /// </summary>
        private async void on_loaded(object sender, RoutedEventArgs e)
        {
            await load_projects_async();

            // 起動時に検索欄へフォーカス（即タイピングで絞り込みできる UX）
            txt_search.Focus();
        }

        // ════════════════════════════════════════════════
        // ===== DB ロード ================================
        // ════════════════════════════════════════════════

        /// <summary>
        /// projects テーブルから is_active=1 のレコードを全件取得して _all_projects に格納
        /// SELECT 列：category_code / company_name（区分詳細として表示）
        /// ORDER：category_code 昇順
        ///
        /// ▼ 注意：projects テーブルの実カラム構成（2026/05/19 DB 直接確認）：
        ///   id / category_code / site_name / company_name / detail / attribute /
        ///   tab_color / is_active / sort_order / created_at / updated_at / updated_by
        ///   業務上の「区分詳細」として CategoryListDialog で表示するのは company_name（正式工事名）。
        ///   ・detail カラムは実データほぼ空のため使用しない
        ///   ・site_name は InputDialog の「業務名(自動)」フィールドで使う想定だが、
        ///     CategoryListDialog の表示には含めない（yori 指定で 2 列シンプル）
        ///
        /// dynamic ベースで取得することで Dapper のバージョン依存（ValueTuple マッピング等）を回避。
        /// </summary>
        private async Task load_projects_async()
        {
            try
            {
                using var conn = database_manager.create_connection();

                var rows = await conn.QueryAsync(@"
                    SELECT category_code, company_name
                    FROM projects
                    WHERE is_active = 1
                    ORDER BY category_code");

                // dynamic → 内部 DTO にマッピング
                _all_projects = rows.Select(r => new project_list_item
                {
                    category_code = (r.category_code as string) ?? "",
                    company_name = (r.company_name as string) ?? ""
                }).ToList();

                // 初期表示は全件
                lst_categories.ItemsSource = _all_projects;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CategoryListDialog] 区分一覧取得失敗：{ex.Message}");

                MessageBox.Show(
                    $"区分一覧の取得に失敗しました。\n\n{ex.Message}",
                    "DB エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ════════════════════════════════════════════════
        // ===== 検索フィルター ============================
        // ════════════════════════════════════════════════

        /// <summary>
        /// 検索 TextBox の TextChanged：
        /// 区分コード（category_code）/ 区分詳細（company_name）を
        /// 大文字小文字区別なしで全文検索（OR 条件）。
        /// 空欄に戻したら全件表示に戻る。
        /// リアルタイムで lst_categories.ItemsSource を切り替えるので即反映される。
        /// </summary>
        private void txt_search_changed(object sender, TextChangedEventArgs e)
        {
            string keyword = txt_search.Text?.Trim().ToLowerInvariant() ?? "";

            // 空欄なら全件
            if (string.IsNullOrWhiteSpace(keyword))
            {
                lst_categories.ItemsSource = _all_projects;
                return;
            }

            // 表示している 2 列いずれかに部分一致するレコードを抽出
            var filtered = _all_projects.Where(p =>
                   p.category_code.ToLowerInvariant().Contains(keyword)
                || p.company_name.ToLowerInvariant().Contains(keyword))
                .ToList();

            lst_categories.ItemsSource = filtered;
        }

        // ════════════════════════════════════════════════
        // ===== 選択・キャンセル ==========================
        // ════════════════════════════════════════════════

        /// <summary>
        /// ListView ダブルクリック：
        /// 選択された行の category_code を selected_category_code に保存し、
        /// DialogResult=true で閉じる（呼び出し元の ShowDialog() が true を返す）。
        ///
        /// 注意：ヘッダーをダブルクリックした場合は SelectedItem が null になるので、
        /// is パターンマッチで型チェック + null チェックを兼ねる。
        /// </summary>
        private void lst_categories_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lst_categories.SelectedItem is project_list_item p
                && !string.IsNullOrWhiteSpace(p.category_code))
            {
                selected_category_code = p.category_code;
                DialogResult = true;
                Close();
            }
            // 選択が無効な場合は何もしない（ダイアログは開いたまま）
        }

        /// <summary>
        /// 「キャンセル」ボタン：
        /// DialogResult=false で閉じる。selected_category_code は null のまま。
        /// </summary>
        private void btn_cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ════════════════════════════════════════════════
        // ===== 内部 DTO ==================================
        // ════════════════════════════════════════════════

        /// <summary>
        /// 区分リスト表示用の内部 DTO。
        /// 既存の EA_DailyReport.Models.ProjectMaster とはスコープを分けて、
        /// この CategoryListDialog 内でのみ使用する。
        ///
        /// ▼ プロパティ名は DB の実カラム名（category_code / company_name）に揃える。
        ///   xaml の Binding（{Binding company_name} など）もこの名前を参照する。
        /// </summary>
        private class project_list_item
        {
            public string category_code { get; set; } = "";
            public string company_name { get; set; } = "";
        }
    }
}
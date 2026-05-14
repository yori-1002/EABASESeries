using Dapper;
using EA_DailyReport.Data;
using EA_DailyReport.Views.Pages;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace EA_DailyReport.Views
{
    /// <summary>
    /// メインウィンドウ（v0.1.7 大幅改修）
    /// ▼ C方針レイアウト：サイドバーがウィンドウ全高に伸びる
    /// ▼ サイドバー：ロゴエリア / クイック「今月の日報」 / メニュー + 月度ツリー / 同期ボタン
    /// ▼ ヘッダーバー削除（同期/最新取得はサイドバー下部に移動）
    /// ▼ UserSession から直接ユーザー名・権限を取得（リフレクション廃止）
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // 起動時のデフォルトページ＝日報一覧（当月度・全員）
            content_frame.Navigate(new DailyReportPage());

            // ヘッダー右側のユーザー名・バージョンを更新
            update_user_display();
            update_version_display();

            // 月度ツリー構築は Loaded で行う（DB アクセスのため）
            Loaded += async (_, _) => await build_month_tree_async();
        }

        // ────────────────────────────────────────────────
        // 既存メソッド（変更なし）
        // ────────────────────────────────────────────────

        /// <summary>
        /// アップデートバナー表示メソッド（既存）
        /// App.xaml.cs の check_update_async() から呼ばれる
        /// </summary>
        public void show_update_banner(string latest_version)
        {
            MessageBox.Show(
                $"新しいバージョン v{latest_version} が利用可能です。",
                "アップデート通知",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ────────────────────────────────────────────────
        // ▼ 追加：v0.1.7 クイックエリア「今月の日報」
        // ────────────────────────────────────────────────

        /// <summary>
        /// 「📅 今月の日報」ボタン
        /// 当月度（前月21日〜当月20日）+ 自分のフィルターを適用した日報一覧を表示
        /// </summary>
        private void btn_quick_this_month_Click(object sender, RoutedEventArgs e)
        {
            var (year, month) = get_current_month_period();
            string my_name = UserSession.user_name ?? "";

            // 自分の名前が取れない場合は全員表示にフォールバック
            // （UserSession 未初期化時など）
            content_frame.Navigate(new DailyReportPage(year, month, my_name));
        }

        /// <summary>
        /// 当月度の (year, month) を取得する
        /// 月度サイクル：前月21日〜当月20日
        /// 例：4/21 〜 5/20 は「5月度」（year=2026, month=5）
        ///     5/21 〜 6/20 は「6月度」
        /// </summary>
        private static (int year, int month) get_current_month_period()
        {
            var today = DateTime.Today;
            int year = today.Year;
            int month = today.Month;
            // 21日以降は翌月度に繰り上げ
            if (today.Day >= 21)
            {
                month++;
                if (month > 12) { month = 1; year++; }
            }
            return (year, month);
        }

        // ────────────────────────────────────────────────
        // ▼ 追加：v0.1.7 月度ツリー構築・選択イベント
        // ────────────────────────────────────────────────

        /// <summary>
        /// 月度ツリーを DB から構築する
        /// daily_reports に存在するデータの (年度, 月度) を集計してツリー化
        /// 当年度は展開・当月度はハイライト
        /// </summary>
        private async Task build_month_tree_async()
        {
            try
            {
                // ローカル DB から月度集計
                using var conn = database_manager.create_connection();
                var raw = (await conn.QueryAsync<(string report_date, int cnt)>(@"
                    SELECT report_date, COUNT(*) AS cnt
                    FROM daily_reports
                    WHERE report_date IS NOT NULL AND report_date <> ''
                    GROUP BY report_date
                ")).ToList();

                // 月度サイクル（前月21日〜当月20日）でグルーピング
                // (year, month) → 件数集計
                var month_counts = new Dictionary<(int year, int month), int>();
                foreach (var (date_str, cnt) in raw)
                {
                    if (!DateTime.TryParse(date_str, out var dt)) continue;
                    var (y, m) = date_to_month_period(dt);
                    var key = (y, m);
                    if (month_counts.ContainsKey(key))
                        month_counts[key] += cnt;
                    else
                        month_counts[key] = cnt;
                }

                if (month_counts.Count == 0)
                {
                    // データ無し時は当月度だけ表示
                    var (cy, cm) = get_current_month_period();
                    month_counts[(cy, cm)] = 0;
                }

                // 年度ごとにグルーピング → ツリー構築
                tree_months.Items.Clear();
                var (current_year, current_month) = get_current_month_period();

                var grouped = month_counts
                    .GroupBy(kv => kv.Key.year)
                    .OrderByDescending(g => g.Key);

                foreach (var year_group in grouped)
                {
                    var year_item = new TreeViewItem
                    {
                        Header = $"📁 {year_group.Key}年度",
                        IsExpanded = year_group.Key == current_year,
                        Style = (Style)FindResource("style_month_tree_item"),
                    };

                    foreach (var month_kv in year_group.OrderByDescending(g => g.Key.month))
                    {
                        int m = month_kv.Key.month;
                        int cnt = month_kv.Value;

                        var month_item = new TreeViewItem
                        {
                            Header = $"📄 {m}月度（{cnt}件）",
                            Tag = (year_group.Key, m),  // 後でクリック時に取り出す
                            Style = (Style)FindResource("style_month_tree_item"),
                        };

                        // 当月度はハイライト（IsSelected=True）
                        if (year_group.Key == current_year && m == current_month)
                            month_item.IsSelected = true;

                        year_item.Items.Add(month_item);
                    }

                    tree_months.Items.Add(year_item);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MainWindow] 月度ツリー構築失敗（無視）: {ex.Message}");
            }
        }

        /// <summary>
        /// 日付から月度サイクルの (year, month) を計算する
        /// 月度サイクル：前月21日〜当月20日
        /// </summary>
        private static (int year, int month) date_to_month_period(DateTime dt)
        {
            int year = dt.Year;
            int month = dt.Month;
            if (dt.Day >= 21)
            {
                month++;
                if (month > 12) { month = 1; year++; }
            }
            return (year, month);
        }

        /// <summary>
        /// 月度ツリーで月度がクリックされたら、その月度を表示する
        /// 全員フィルター（自分指定なし）で表示
        /// </summary>
        private void tree_months_SelectedItemChanged(
            object sender,
            RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not TreeViewItem item) return;
            if (item.Tag is not ValueTuple<int, int> ym) return;

            var (year, month) = ym;
            // 月度ツリーから選択時は「全員」表示（クイックボタンとの差別化）
            content_frame.Navigate(new DailyReportPage(year, month, null));
        }

        // ────────────────────────────────────────────────
        // サイドバーナビゲーション
        // ────────────────────────────────────────────────

        /// <summary>ダッシュボード画面に遷移する</summary>
        private void btn_nav_dashboard_Click(object sender, RoutedEventArgs e)
        {
            content_frame.Navigate(new DashboardPage());
        }

        /// <summary>日報一覧画面に遷移する（当月度・全員）</summary>
        private void btn_nav_daily_report_Click(object sender, RoutedEventArgs e)
        {
            var (year, month) = get_current_month_period();
            content_frame.Navigate(new DailyReportPage(year, month, null));
        }

        /// <summary>出面表画面に遷移する（Sprint 2 で実装予定）</summary>
        private void btn_nav_attendance_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "出面表は Sprint 2 で実装予定です。",
                "未実装機能のお知らせ",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// 設定画面に遷移する（v0.1.7 実装）
        /// DB モード切替・本番/テスト DB パス指定が可能
        /// </summary>
        private void btn_nav_settings_Click(object sender, RoutedEventArgs e)
        {
            content_frame.Navigate(new SettingsPage());
        }

        /// <summary>ヘルプダイアログを開く（Sprint 2 以降で実装予定）</summary>
        private void btn_nav_help_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "ヘルプは Sprint 2 以降で実装予定です。",
                "未実装機能のお知らせ",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ────────────────────────────────────────────────
        // 同期・最新取得ボタン
        // ────────────────────────────────────────────────

        /// <summary>手動同期ボタン</summary>
        private void btn_sync_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "手動同期は Sprint 1 後半で実装予定です。\n" +
                "現在はバックグラウンドで2分ごとに自動同期しています。",
                "手動同期",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// 「最新を取得」ボタン（v0.1.5 修正）
        /// NAS から最新の日報データを取得し、ローカルDBに反映する
        /// その後、現在表示中の日報一覧ページを再読込する
        /// </summary>
        private async void btn_refresh_Click(object sender, RoutedEventArgs e)
        {
            // ボタン連打防止
            btn_refresh.IsEnabled = false;
            string original_label = (btn_refresh.Content as string) ?? "🔄 最新を取得";
            btn_refresh.Content = "🔄 取得中...";

            try
            {
                // ─── ① NAS パスをローカル設定から取得 ───
                string nas_path = "";
                try
                {
                    using var conn = EA_DailyReport.Data.database_manager.create_connection();
                    var v = await Dapper.SqlMapper.ExecuteScalarAsync<string>(
                        conn,
                        "SELECT value FROM app_settings WHERE key = 'nas_path'");
                    nas_path = v ?? "";
                }
                catch
                {
                    // 設定取得失敗 → 後段で nas_path 空判定
                }

                if (string.IsNullOrWhiteSpace(nas_path))
                {
                    MessageBox.Show(
                        "NAS パスが未設定のため、最新データを取得できません。\n設定画面から NAS パスを指定してください。",
                        "NAS未設定",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                // ─── ② NAS → ローカル取得実行 ───
                int affected = await EA_DailyReport.Services.NasDataFetchService
                    .fetch_latest_daily_reports_async(nas_path);

                // ─── ③ 結果通知 ───
                if (affected < 0)
                {
                    MessageBox.Show(
                        "最新データの取得に失敗しました。\nNAS への接続を確認してください。",
                        "取得エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else if (affected == 0)
                {
                    txt_status.Text = "最新データを取得しました（更新なし）";
                }
                else
                {
                    txt_status.Text = $"最新データを取得しました（{affected}件更新）";
                }

                // ─── ④ 月度ツリーを再構築（新月度のデータが増えてるかも）───
                await build_month_tree_async();

                // ─── ⑤ 日報一覧ページなら再読込 ───
                if (content_frame.Content is DailyReportPage page)
                {
                    page.reload_data();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"最新データの取得中に予期しないエラーが発生しました。\n\n{ex.Message}",
                    "取得エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                btn_refresh.IsEnabled = true;
                btn_refresh.Content = original_label;
            }
        }

        // ────────────────────────────────────────────────
        // ▼ 修正：v0.1.7 ヘッダー表示の更新（UserSession 直接呼出）
        // ────────────────────────────────────────────────

        /// <summary>
        /// サイドバー上部のユーザー名表示を更新する
        /// ▼ 修正：v0.1.7 リフレクション廃止、UserSession 直接呼出
        /// 権限は管理者かどうかも表示する
        /// </summary>
        private void update_user_display()
        {
            try
            {
                if (UserSession.is_logged_in
                 && !string.IsNullOrWhiteSpace(UserSession.user_name))
                {
                    string admin_label = UserSession.is_admin ? "（管理者）" : "";
                    txt_user.Text = $"👤 {UserSession.user_name}{admin_label}";
                }
                else
                {
                    txt_user.Text = "👤 ゲスト";
                }
            }
            catch
            {
                // UserSession 未初期化時はデフォルト表示
                txt_user.Text = "👤 ゲスト";
            }
        }

        /// <summary>
        /// サイドバー上部のバージョン表示を更新する
        /// AppVersionInfo.CURRENT_VERSION（動的取得方式）から取得
        /// </summary>
        private void update_version_display()
        {
            try
            {
                txt_version.Text = $"EABASE Series v{AppVersionInfo.CURRENT_VERSION}";
            }
            catch
            {
                // 失敗時はデフォルト表示のまま
            }
        }
    }
}
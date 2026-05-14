using System.Windows;
using System.Windows.Controls;
using Dapper;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Views
{
    public partial class MainWindow : Window
    {
        // ▼ v1.0.0 追加：サイドバー左下のバージョン表示用プロパティ
        //   AssemblyVersion から動的取得することで、csproj の <Version> を上げるだけで
        //   サイドバーの表示も自動連動する（表示更新忘れを防止）。
        //
        //   GetName().Version は AssemblyVersion の値を返す（例：1.0.0.0）
        //   ToString(3) で前から3桁表示（例：1.0.0）→ 末尾の .0 を非表示にして見やすく
        //
        //   表示例：「v1.0.0」（Beta卒業のため「- Beta」サフィックスは廃止）
        public string app_version_display =>
            "v" + System.Reflection.Assembly.GetExecutingAssembly()
                  .GetName().Version!.ToString(3);

        public MainWindow()
        {
            InitializeComponent();

            // ▼▼▼ Sprint 5A：Loadedイベントでユーザー確認 ▼▼▼
            Loaded += async (_, _) =>
            {
                string mac = EA_CostManager.UserSession.fetch_mac_address();

                using var conn = EA_CostManager.Data.database_manager.create_connection();
                var user = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT id, user_name, employee_id, is_admin FROM pc_users WHERE mac_address = @mac",
                    new { mac });

                if (user != null)
                {
                    // 登録済み → UserSessionに設定してそのまま表示
                    EA_CostManager.UserSession.set(
                        (int)user.id,
                        (string)user.user_name,
                        (int)user.employee_id,
                        (int)user.is_admin >= 2);

                    // PC名を最新に更新
                    string pc = EA_CostManager.UserSession.fetch_pc_name();
                    await conn.ExecuteAsync(
                        "UPDATE pc_users SET pc_name = @pc, updated_at = datetime('now','localtime') WHERE mac_address = @mac",
                        new { pc, mac });
                }
                else
                {
                    // 未登録 → MainWindowを一時非表示にしてダイアログを表示
                    this.Hide();

                    var dlg = new FirstLoginDialog
                    {
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };

                    dlg.ShowDialog();

                    // 登録完了後にMainWindowを前面に表示
                    this.Show();
                    this.Activate();
                    this.WindowState = WindowState.Maximized;
                }

                // ▼▼▼ 追加：セッション登録（古いセッションを削除して新規登録）▼▼▼
                // 5分以内に更新されたものをオンラインと見なすため、ここで登録しておく
                try
                {
                    // ★v0.9.7追加：起動時に古いセッションを自動クリーンアップ
                    // 前回クラッシュや強制終了でinactiveに更新されなかったセッションを掃除する。
                    // ハートビートが5分以上更新されていないセッションは確実に切断済みと判断。
                    await conn.ExecuteAsync(@"
                        UPDATE user_sessions 
                        SET status = 'inactive' 
                        WHERE status = 'active' 
                          AND updated_at < datetime('now', 'localtime', '-5 minutes')");

                    await conn.ExecuteAsync(
                        "DELETE FROM user_sessions WHERE mac_address = @mac",
                        new { mac });
                    // ▼▼▼ [現場タブ追跡] 起動時は current_tab を空でリセット ▼▼▼
                    // DELETE + INSERT で古いセッションを完全にリセット
                    // current_tab は App.xaml.cs の NAS マイグレーションで追加済みのため省略しない
                    await conn.ExecuteAsync(@"
                        INSERT INTO user_sessions (mac_address, user_name, software, status)
                        VALUES (@mac, @name, 'CostManager', 'active')",
                        new
                        {
                            mac,
                            name = EA_CostManager.UserSession.user_name
                        });
                    // current_tab を明示的に空にリセット（前回セッションの値を消す）
                    await conn.ExecuteAsync(@"
                        UPDATE user_sessions SET current_tab = ''
                        WHERE mac_address = @mac",
                        new { mac });
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"セッション登録エラー: {ex.Message}");
                }

                // ▼▼▼ 追加：オンラインユーザー取得＋ハートビート開始 ▼▼▼
                if (DataContext is main_view_model main_vm)
                {
                    await main_vm.refresh_online_users_async();
                    main_vm.start_heartbeat();

                    // ▼ 追加 [v1.0.3] 起動時表示ページ設定の反映
                    //   v1.0.2 までは UserSettings.startup_page を読んで起動時の
                    //   表示ページを切り替える実装が存在しなかった（実装漏れ）。
                    //   このため、ユーザーが詳細設定で「原価集計」を選択して保存・再起動しても
                    //   毎回ダッシュボードで起動していた。バグ②の根本原因。
                    //
                    //   navigate_to_cost_command を Execute すれば、サイドバーの
                    //   「原価集計」クリックと完全に同じ挙動（current_page="cost" +
                    //   is_cost_expanded=true で大区分ツリー展開）になる。
                    //   既存ロジックを再利用することで挙動の一貫性も担保される。
                    //
                    //   "dashboard" の場合は何もしない（current_page の既定値が "dashboard" のため）。
                    try
                    {
                        var startup_settings = EA_CostManager.UserSettingsManager.current;
                        if (startup_settings.startup_page == "cost")
                        {
                            main_vm.navigate_to_cost_command.Execute(null);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MainWindow] 起動時ページ反映失敗: {ex.GetType().Name} : {ex.Message}");
                    }
                }
            };

            // ▼▼▼ 追加：ウィンドウ終了時にセッションを非アクティブ化 ▼▼▼
            // ▼▼▼ 修正：async void の Closing は await が完了しないまま終了してNASへの
            //           書き込みがぶら下がる問題を修正。CancelEventArgs.Cancel で一旦キャンセル
            //           → 終了処理完了後に Application.Shutdown() で正常終了する方式に変更 ▼▼▼
            Closing += (_, e) =>
            {
                // 既に終了処理中なら何もしない（2重発火防止）
                if (_is_closing) return;
                _is_closing = true;

                // ▼ 追加 [v1.0.1] 終了時にウィンドウ状態を保存する
                //   普通のWindowsアプリと同じ挙動：
                //   ・最大化で終了 → 次回も最大化で開く
                //   ・通常サイズで終了 → 次回も同じ位置・同じサイズで開く
                //   ・最小化中の場合は「Normal」として保存し、次回は通常サイズで開く
                //     （最小化のまま起動するとユーザーが見失うため意図的に通常へ戻す）
                //   保存失敗は致命的ではない（次回はデフォルト値で起動するだけ）。
                try
                {
                    var settings = UserSettingsManager.current;

                    if (this.WindowState == WindowState.Maximized)
                    {
                        settings.window_state = "Maximized";
                        // Maximized 時の Width/Height は画面サイズが入るためそのまま保存しない
                        // （Normal 復帰時のサイズは RestoreBounds で取得する）
                        if (this.RestoreBounds.Width > 0)
                        {
                            settings.window_width = this.RestoreBounds.Width;
                            settings.window_height = this.RestoreBounds.Height;
                            settings.window_top = this.RestoreBounds.Top;
                            settings.window_left = this.RestoreBounds.Left;
                        }
                    }
                    else
                    {
                        // Normal / Minimized どちらも次回は Normal で開く
                        settings.window_state = "Normal";
                        settings.window_width = this.Width;
                        settings.window_height = this.Height;
                        settings.window_top = this.Top;
                        settings.window_left = this.Left;
                    }

                    UserSettingsManager.save(settings);
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MainWindow] ウィンドウ状態保存失敗: {ex.GetType().Name} : {ex.Message}");
                }

                // 一旦終了をキャンセルして非同期処理を完走させる
                e.Cancel = true;

                // fire-and-forget で終了処理を実行 → 完了後にShutdown
                _ = shutdown_async();
            };
        }

        /// <summary>
        /// ▼ 追加 [v1.0.1] 起動時にウィンドウ状態を復元する
        ///
        /// 動作仕様：
        ///   user_settings.json に保存された前回のウィンドウ状態を読み取り、
        ///   起動時のウィンドウ表示モード・サイズ・位置を復元する。
        ///   ・初回起動時（保存値なし） → デフォルトで最大化
        ///   ・前回最大化で終了        → 最大化で開く
        ///   ・前回通常サイズで終了    → 同じ位置・同じサイズで開く
        ///
        /// 呼び出しタイミング：
        ///   OnSourceInitialized は Window のハンドル作成直後に呼ばれる。
        ///   ここで WindowState を設定すると、起動時の最大化処理が他の初期化より
        ///   前に走るため、画面のチラつきを最小化できる。
        ///
        /// 画面外復元の防止：
        ///   外部モニター抜き差しで前回位置が画面外になる可能性があるため、
        ///   現在の仮想画面範囲内に収まるかチェックして、外なら中央に補正する。
        ///
        /// 失敗時の挙動：
        ///   どこかで例外が出ても、デフォルトの最大化で起動できるよう
        ///   try-catch で握りつぶす。
        /// </summary>
        protected override void OnSourceInitialized(System.EventArgs e)
        {
            base.OnSourceInitialized(e);

            try
            {
                var settings = UserSettingsManager.current;

                // 通常サイズで開く場合は、保存サイズと位置を先に適用する
                if (settings.window_state == "Normal"
                    && settings.window_width > 0
                    && settings.window_height > 0)
                {
                    this.Width = settings.window_width;
                    this.Height = settings.window_height;

                    // 位置情報がある場合は適用（top/leftは-1=未設定扱い）
                    if (settings.window_top >= 0 && settings.window_left >= 0)
                    {
                        // 仮想画面範囲チェック：外部モニター抜き差し対策
                        double v_left = SystemParameters.VirtualScreenLeft;
                        double v_top = SystemParameters.VirtualScreenTop;
                        double v_width = SystemParameters.VirtualScreenWidth;
                        double v_height = SystemParameters.VirtualScreenHeight;

                        // ウィンドウの右下端が画面外に出ていないか
                        bool in_screen =
                            settings.window_left >= v_left
                            && settings.window_top >= v_top
                            && (settings.window_left + this.Width) <= (v_left + v_width)
                            && (settings.window_top + this.Height) <= (v_top + v_height);

                        if (in_screen)
                        {
                            this.Top = settings.window_top;
                            this.Left = settings.window_left;
                        }
                        else
                        {
                            // 画面外 → 中央配置（XAML の WindowStartupLocation="CenterScreen" に任せる）
                            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                        }
                    }

                    this.WindowState = WindowState.Normal;
                }
                else
                {
                    // 保存値が "Maximized" または未設定 → 最大化で開く（デフォルト挙動）
                    this.WindowState = WindowState.Maximized;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MainWindow] ウィンドウ状態復元失敗: {ex.GetType().Name} : {ex.Message}");
                // 例外時はデフォルトの最大化で起動
                this.WindowState = WindowState.Maximized;
            }
        }

        // ▼▼▼ 大区分グループのクリックハンドラー ▼▼▼
        private void group_item_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not project_group_item group) return;
            if (DataContext is not main_view_model main_vm) return;

            main_vm.current_page = "cost";
            main_vm.cost_vm.selected_group = group;
        }

        // ▼▼▼ [Sprint 5G] キーボードショートカット（ウィンドウ全体で受け取る） ▼▼▼
        // CostPage.KeyDown ではフォーカスがないと発火しないため、Window.OnKeyDown に集約する
        protected override async void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // F1：ヘルプダイアログ
            if (e.Key == System.Windows.Input.Key.F1)
            {
                e.Handled = true;
                var dlg = new Views.HelpDialog { Owner = this };
                dlg.ShowDialog();
                return;
            }

            // 以下は原価集計ページが表示中のみ有効
            var main_vm = DataContext as EA_CostManager.ViewModels.main_view_model;
            if (main_vm?.current_page != "cost") return;

            // CostPage の VM を取得
            var cost_vm = main_vm.cost_vm;
            var selected_tab = cost_vm?.selected_tab;

            bool ctrl = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl)
                      || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl);
            bool shift = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift)
                      || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift);

            // ★v0.9.7追加：F2 現場編集ダイアログ（Ctrlなし）
            // if (!ctrl) return の前に配置しないと到達できないため、ここに配置
            if (e.Key == System.Windows.Input.Key.F2 && !ctrl)
            {
                e.Handled = true;
                if (selected_tab != null)
                {
                    var cost_page = this.FindName("cost_page_control") as Views.CostPage
                        ?? find_visual_child<Views.CostPage>(this);
                    if (cost_page != null)
                    {
                        cost_page.trigger_project_edit(selected_tab);
                    }
                }
                return;
            }

            if (!ctrl) return;

            switch (e.Key)
            {
                // Ctrl+Z：Undo
                case System.Windows.Input.Key.Z:
                    e.Handled = true;
                    var undo_id = await EA_CostManager.Services.EditHistoryService.undo_async();
                    if (undo_id >= 0 && cost_vm != null)
                        await cost_vm.initialize_async();
                    else if (!EA_CostManager.Services.EditHistoryService.can_undo)
                        System.Windows.MessageBox.Show("これ以上元に戻せません。",
                            "Undo", System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Information);
                    break;

                // Ctrl+Y：Redo
                case System.Windows.Input.Key.Y:
                    e.Handled = true;
                    var redo_id = await EA_CostManager.Services.EditHistoryService.redo_async();
                    if (redo_id >= 0 && cost_vm != null)
                        await cost_vm.initialize_async();
                    else if (!EA_CostManager.Services.EditHistoryService.can_redo)
                        System.Windows.MessageBox.Show("これ以上やり直せません。",
                            "Redo", System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Information);
                    break;

                // Ctrl+R：🔄更新
                case System.Windows.Input.Key.R:
                    e.Handled = true;
                    if (selected_tab != null)
                    {
                        await selected_tab.load_records_async();
                        await selected_tab.load_filter_tabs_async();
                    }
                    break;

                // Ctrl+P：印刷ダイアログ
                case System.Windows.Input.Key.P:
                    e.Handled = true;
                    if (selected_tab != null)
                    {
                        var print_dlg = new Views.PrintOptionsDialog(selected_tab) { Owner = this };
                        print_dlg.ShowDialog();
                    }
                    break;

                // Ctrl+E：Excel出力ダイアログ
                case System.Windows.Input.Key.E:
                    e.Handled = true;
                    if (selected_tab != null)
                    {
                        var excel_dlg = new Views.ExcelExportDialog(selected_tab) { Owner = this };
                        excel_dlg.ShowDialog();
                    }
                    break;

                // Ctrl+N：絞り込みタブ追加
                case System.Windows.Input.Key.N:
                    e.Handled = true;
                    if (selected_tab != null)
                    {
                        // CostPage の btn_add_filter_Click 相当の処理
                        var cost_page = this.FindName("cost_page_control") as Views.CostPage
                            ?? find_visual_child<Views.CostPage>(this);
                        if (cost_page != null)
                        {
                            var fake_btn = new Button { Tag = selected_tab };
                            var args = new RoutedEventArgs(Button.ClickEvent, fake_btn);
                            cost_page.trigger_add_filter(selected_tab);
                        }
                    }
                    break;

                // Ctrl+U：人員単価変更
                case System.Windows.Input.Key.U:
                    e.Handled = true;
                    if (selected_tab != null)
                    {
                        bool load_existing = selected_tab.selected_display_tab?.use_custom_rates == true;
                        var rates_dlg = new Views.ProjectRatesDialog(
                            selected_tab.project_id, selected_tab.tab_name, load_existing)
                        { Owner = this };
                        rates_dlg.ShowDialog();
                    }
                    break;

                // Ctrl+M：月度/年度折りたたみ
                case System.Windows.Input.Key.M:
                    e.Handled = true;
                    if (!shift)
                        selected_tab?.selected_display_tab?.toggle_all_months();
                    else
                        selected_tab?.selected_display_tab?.toggle_all_years();
                    break;

                // ★v0.9.7追加：Ctrl+F → CostPageのタブ検索ボックスにフォーカス
                case System.Windows.Input.Key.F:
                    e.Handled = true;
                    {
                        var cost_page = this.FindName("cost_page_control") as Views.CostPage
                            ?? find_visual_child<Views.CostPage>(this);
                        if (cost_page != null)
                        {
                            var search_box = cost_page.FindName("tab_search_box") as System.Windows.Controls.ComboBox;
                            search_box?.Focus();
                        }
                    }
                    break;
            }
        }

        // ▼▼▼ 追加：終了処理中フラグ（Closing 2重発火防止） ▼▼▼
        private bool _is_closing = false;

        // ▼▼▼ 追加（v0.9.6-fix2）：強制終了タイマーをクラスフィールドに保持 ▼▼▼
        // System.Threading.Timer はローカル変数だと GC に回収されてコールバックが
        // 発火しない .NET の仕様がある。クラスフィールドに昇格して強参照を維持する。
        private System.Threading.Timer? _force_exit_timer;

        /// <summary>
        /// ▼▼▼ 追加：非同期終了処理 ▼▼▼
        /// Closing イベントの async void 問題を回避するため、
        /// 終了処理を別メソッドに切り出して完走後に Application.Shutdown() を呼ぶ
        ///
        /// ▼▼▼ 修正（v0.9.6）：プロセス残留バグ対応 ▼▼▼
        /// 旧実装の問題：
        ///   ① finally で force_exit_timer.Dispose() を呼び、保険タイマーを自分で消していた
        ///      → Application.Shutdown() がハングした場合に救済手段がなくなっていた
        ///   ② Dispatcher.Invoke() で Shutdown() を呼ぶと、Dispatcher がブロック中だと詰まる
        ///   ③ main_vm.stop_heartbeat() を呼んでも _cts.Token が実際のDB処理に
        ///      渡されていなかったため、NAS DB の busy_timeout=30秒待ちでプロセスが残留
        ///
        /// 今回の修正：
        ///   ① force_exit_timer は Dispose しない（3秒後に必ず Environment.Exit が発火）
        ///   ② Dispatcher.BeginInvoke（非同期投げっぱなし）に変更
        ///   ③ App.request_shutdown() で NAS 通信を事前キャンセル
        ///      main_vm.Dispose() で進行中DB処理を即中断
        /// </summary>
        private async System.Threading.Tasks.Task shutdown_async()
        {
            // ▼▼▼ 追加（v0.9.6-fix）：終了処理の診断ログ ▼▼▼
            EA_CostManager.App.write_log("=== shutdown_async開始 ===");

            // ▼▼▼ 修正（v0.9.6）：アプリ全体のキャンセルを真っ先に発行 ▼▼▼
            // これで UpdateCheckerService.get_latest_version_async 等の fire-and-forget
            // NAS 通信が即中断され、SMB タイムアウト待ちでプロセスが残る問題を防ぐ。
            EA_CostManager.App.request_shutdown();

            // ▼▼▼ 修正（v0.9.6-fix）：確実に終了するためタイマーで強制終了を予約してから処理を開始 ▼▼▼
            // どこでハングしても1秒後に必ず Environment.Exit(0) が呼ばれる。
            // 重要：このタイマーは finally で Dispose しない（保険として最後まで残す）。
            //
            // 1秒に短縮した理由：
            //   未修正のVM（cost_view_model / filter_tab_view_model 等）が NAS DB への
            //   クエリを実行中の場合、busy_timeout=30秒で待機してプロセスが残留する。
            //   「保存ボタンがないインライン即保存方式」のため、ユーザー操作の書き込みは
            //   数百ms で完了している想定 → 1秒待てば十分。
            //   1秒で終わらない書き込みは切断されるが、v0.9.7 のペンディング保存機構で
            //   次回起動時に復元可能にする設計。
            // ▼▼▼ 修正（v0.9.6-fix2）：ローカル変数→クラスフィールドに変更 ▼▼▼
            // ローカル変数だと GC 回収でタイマーが消えて Environment.Exit が発火しなかった
            _force_exit_timer = new System.Threading.Timer(
                _ => System.Environment.Exit(0),
                null,
                1000,  // 1秒後に強制終了（v0.9.6-fix で 3000→1000 に短縮）
                System.Threading.Timeout.Infinite);

            try
            {
                // ① ハートビート停止・キャンセルトークン発火・VM の IDisposable 解放
                // ▼▼▼ 修正（v0.9.6）：main_vm.Dispose() を追加 ▼▼▼
                // main_view_model が IDisposable になったため、
                // Dispose() 内で _session_heartbeat.Stop() / _cts.Cancel() / _cts.Dispose() を実行。
                // これで refresh_online_users_async 等の await 中DB処理が
                // OperationCanceledException で即時中断される。
                if (DataContext is main_view_model main_vm)
                {
                    main_vm.stop_heartbeat();
                    main_vm.Dispose();
                }

                // ★v0.9.7修正：セッションをinactiveに更新
                // 【変更前】ローカルDBにのみ書いていたため、NAS上のセッションが永久にactive残留
                // 【変更後】現在のDB（NAS接続中ならNAS）に書く → 失敗時はローカルDBにフォールバック
                try
                {
                    string mac = EA_CostManager.UserSession.fetch_mac_address();

                    // まず現在のDB（NAS or ローカル）に書く
                    var current_conn_str =
                        $"Data Source={EA_CostManager.Data.database_manager.get_db_path()};Timeout=1";
                    using var current_conn = new Microsoft.Data.Sqlite.SqliteConnection(current_conn_str);

                    using var cts = new System.Threading.CancellationTokenSource(1000);
                    await current_conn.OpenAsync(cts.Token);

                    using var cmd = current_conn.CreateCommand();
                    cmd.CommandText = "PRAGMA busy_timeout=500;";
                    cmd.ExecuteNonQuery();

                    using var cts2 = new System.Threading.CancellationTokenSource(1000);
                    await current_conn.ExecuteAsync(new CommandDefinition(@"
                        UPDATE user_sessions
                        SET status = 'inactive', updated_at = datetime('now','localtime')
                        WHERE mac_address = @mac",
                        new { mac },
                        cancellationToken: cts2.Token));
                }
                catch
                {
                    // NAS書き込み失敗時はローカルDBにフォールバック
                    try
                    {
                        string mac = EA_CostManager.UserSession.fetch_mac_address();
                        var local_conn_str =
                            $"Data Source={EA_CostManager.Data.database_manager.get_local_db_path()};Timeout=1";
                        using var local_conn = new Microsoft.Data.Sqlite.SqliteConnection(local_conn_str);

                        using var cts_fb = new System.Threading.CancellationTokenSource(500);
                        await local_conn.OpenAsync(cts_fb.Token);

                        using var cmd_fb = local_conn.CreateCommand();
                        cmd_fb.CommandText = "PRAGMA busy_timeout=500;";
                        cmd_fb.ExecuteNonQuery();

                        await local_conn.ExecuteAsync(@"
                            UPDATE user_sessions
                            SET status = 'inactive', updated_at = datetime('now','localtime')
                            WHERE mac_address = @mac",
                            new { mac });
                    }
                    catch { /* フォールバックも失敗 → 次回起動時のクリーンアップに任せる */ }
                }
            }
            catch { /* 全例外を無視して終了へ */ }
            finally
            {
                // ▼▼▼ 修正（v0.9.6）：force_exit_timer は Dispose しない ▼▼▼
                // 旧実装：force_exit_timer.Dispose()  ← これが保険を消す原因だった
                // 新実装：3秒後の Environment.Exit(0) を保険として最後まで有効に保つ。
                // 正常パスでタイマーインスタンスが残るが、System.Threading.Timer は
                // ThreadPool のバックグラウンドスレッド扱いなのでプロセス終了を妨げない。

                // ▼▼▼ 修正（v0.9.6）：Dispatcher.Invoke → BeginInvoke ▼▼▼
                // Invoke は同期でブロックするため、Dispatcher が何かで詰まっていると
                // ここで無限待機になる。BeginInvoke で投げっぱなしにすれば、
                // うまく動けば Shutdown が走り、失敗しても force_exit_timer が
                // 3秒後に確実にプロセスを終了させる。
                try
                {
                    Dispatcher.BeginInvoke(new System.Action(() =>
                    {
                        try { System.Windows.Application.Current.Shutdown(); }
                        catch { System.Environment.Exit(0); }
                    }));
                }
                catch
                {
                    System.Environment.Exit(0);
                }
            }
        }

        // VisualTree検索ヘルパー
        private static T? find_visual_child<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var result = find_visual_child<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        // ▼▼▼ [Sprint 6] アップデート通知バナー ▼▼▼

        private string _latest_version = "";

        /// <summary>
        /// App.xaml.cs から呼ばれる。アップデートバナーを表示する。
        /// </summary>
        public void show_update_banner(string latest_version)
        {
            _latest_version = latest_version;

            // バナーテキスト更新
            if (txt_update_message != null)
                txt_update_message.Text =
                    $"🎉 新しいバージョン v{latest_version} が利用できます  " +
                    $"（現在: v{AppVersionInfo.CURRENT_VERSION}）";

            // バナー表示・高さを有効化
            if (border_update_banner != null)
                border_update_banner.Visibility = Visibility.Visible;

            if (row_update_banner != null)
                row_update_banner.Height = new System.Windows.GridLength(40);
        }

        // 「今すぐ更新」ボタン
        private async void btn_update_now_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                $"v{_latest_version} のインストーラーをダウンロードして実行します。\n\n" +
                "アプリが終了してインストーラーが起動します。\n続行しますか？",
                "アップデートの確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.OK) return;

            // ダウンロード中はボタンを無効化
            if (btn_update_now != null) btn_update_now.IsEnabled = false;

            await EA_CostManager.Services.UpdateCheckerService
                .download_and_run_async(_latest_version);

            if (btn_update_now != null) btn_update_now.IsEnabled = true;
        }

        // 「後で」ボタン → バナーを非表示
        private void btn_update_later_Click(object sender, RoutedEventArgs e)
        {
            if (border_update_banner != null)
                border_update_banner.Visibility = Visibility.Collapsed;
            if (row_update_banner != null)
                row_update_banner.Height = new System.Windows.GridLength(0);
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {

        }
    }
}
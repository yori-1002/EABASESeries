using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.ViewModels
{
    /// <summary>オンラインユーザー1件分（ユーザー名＋現在の現場タブ）</summary>
    public class online_user_item
    {
        public string user_name { get; set; } = "";
        public string current_tab { get; set; } = "";
        /// <summary>Tooltip表示テキスト：「ユーザー名 → 現場名」形式</summary>
        public string display_text =>
            string.IsNullOrWhiteSpace(current_tab)
                ? user_name
                : $"{user_name}：{current_tab}";
    }
    /// <summary>
    /// メインウィンドウのViewModel - 画面遷移・オンラインユーザー管理を担当
    ///
    /// ▼▼▼ 修正（v0.9.6）：IDisposable を実装 ▼▼▼
    /// アプリ終了時にタイマー停止・CancellationTokenSource の Cancel/Dispose を
    /// 確実に実行するため。MainWindow.shutdown_async() から Dispose() を呼ぶ。
    /// </summary>
    public class main_view_model : base_view_model, IDisposable
    {
        // ▼▼▼ 追加：CostPageと共有するVMインスタンス ▼▼▼
        public cost_view_model cost_vm { get; } = new cost_view_model();

        // ▼▼▼ 追加：原価集計サイドバーの展開フラグ ▼▼▼
        private bool _is_cost_expanded = false;
        public bool is_cost_expanded
        {
            get => _is_cost_expanded;
            set => SetProperty(ref _is_cost_expanded, value);
        }

        // ▼ 追加 [Sprint 9A]：工数表の大区分ツリーの開閉状態
        //   原価集計の is_cost_expanded と同じ挙動（工数表ボタンの再クリックでトグル）。
        //   ツリー自体は各ページのVMが持つ group_items を表示するため、
        //   原価集計と工数表で選択中の大区分は互いに影響しない。
        private bool _is_workload_expanded = false;
        public bool is_workload_expanded
        {
            get => _is_workload_expanded;
            set => SetProperty(ref _is_workload_expanded, value);
        }

        // ▼▼▼ 追加：オンラインユーザー一覧（ユーザー名＋現在の現場タブ）▼▼▼
        public ObservableCollection<online_user_item> online_users { get; } = new();
        // 後方互換用：online_user_names は online_users から生成
        public ObservableCollection<string> online_user_names { get; } = new();
        public int online_user_count => online_users.Count;

        // ▼▼▼ 追加：セッションハートビートタイマー ▼▼▼
        // 2分ごとに updated_at を更新してオンライン状態を維持する
        // オンラインユーザー一覧も同タイミングで再取得する
        private readonly DispatcherTimer _session_heartbeat;

        // ▼▼▼ 追加：終了時に非同期処理を中断するためのキャンセルトークン ▼▼▼
        // stop_heartbeat() / Dispose() でキャンセルして進行中のDB処理を即時中断する
        private CancellationTokenSource _cts = new();

        // ▼▼▼ 追加（v0.9.6）：二重 Dispose 防止フラグ ▼▼▼
        private bool _disposed = false;

        // コマンドを手動で定義
        public ICommand navigate_to_dashboard_command { get; }
        public ICommand navigate_to_cost_command { get; }
        public ICommand navigate_to_import_command { get; }
        public ICommand navigate_to_settings_command { get; }
        // ▼ 追加 [Sprint 7B]：工数表ページへのナビゲーション
        public ICommand navigate_to_workload_command { get; }

        public main_view_model()
        {
            navigate_to_dashboard_command = new RelayCommand(() =>
            {
                current_page = "dashboard";
                is_cost_expanded = false;
                is_workload_expanded = false; // ▼ 追加 [Sprint 9A]
            });
            navigate_to_cost_command = new RelayCommand(() =>
            {
                if (current_page == "cost")
                {
                    // ▼▼▼ 追加：原価集計ページが既に表示中のときはツリーをトグル ▼▼▼
                    is_cost_expanded = !is_cost_expanded;
                }
                else
                {
                    current_page = "cost";
                    is_cost_expanded = true;
                }
                is_workload_expanded = false; // ▼ 追加 [Sprint 9A]：工数表ツリーは閉じる
            });
            navigate_to_import_command = new RelayCommand(() =>
            {
                current_page = "import";
                is_cost_expanded = false;
                is_workload_expanded = false; // ▼ 追加 [Sprint 9A]
            });
            navigate_to_settings_command = new RelayCommand(() =>
            {
                current_page = "settings";
                is_cost_expanded = false;
                is_workload_expanded = false; // ▼ 追加 [Sprint 9A]
            });
            // ▼ 修正 [Sprint 9A]：工数表ページへ切替＋大区分ツリーを開く
            //   既に工数表を表示中に再クリックした場合はツリーをトグルする
            //   （原価集計ボタンと同じ操作感）。
            navigate_to_workload_command = new RelayCommand(() =>
            {
                if (current_page == "workload")
                {
                    is_workload_expanded = !is_workload_expanded;
                }
                else
                {
                    current_page = "workload";
                    is_workload_expanded = true;
                }
                is_cost_expanded = false; // 原価集計ツリーは閉じる
            });

            // ▼▼▼ 追加：ハートビートタイマー初期化（2分間隔）▼▼▼
            // MainWindow.xaml.cs の Loaded でセッション登録後に Start() を呼ぶ
            _session_heartbeat = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(2)
            };
            _session_heartbeat.Tick += async (_, _) => await refresh_online_users_async();
        }

        // ▼▼▼ 追加：ハートビートタイマーを開始する（MainWindow.Loaded後に呼ぶ）▼▼▼
        public void start_heartbeat()
        {
            _session_heartbeat.Stop();
            _session_heartbeat.Start();
        }

        // ▼▼▼ 追加：ハートビートタイマーを停止する（MainWindow.Closing時に呼ぶ）▼▼▼
        // ▼▼▼ 修正：タイマー停止に加えてCancellationTokenをキャンセルして
        //           進行中のrefresh_online_users_async等のDB処理を即時中断する ▼▼▼
        public void stop_heartbeat()
        {
            _session_heartbeat.Stop();
            try { _cts.Cancel(); } catch { /* Dispose 済みなら無視 */ }
        }

        /// <summary>
        /// セッションの updated_at を更新してオンラインユーザー一覧を再取得する
        /// ログイン直後・ハートビートTickの両方から呼ばれる
        ///
        /// ▼▼▼ 修正（v0.9.6）：全DB処理に CancellationToken を渡す ▼▼▼
        /// 理由：以前は IsCancellationRequested の初期チェックのみで、
        ///       DB処理（ExecuteAsync / QueryAsync）には token が渡されていなかった。
        ///       その結果、stop_heartbeat で Cancel() してもすでに NAS DB 書き込み中の
        ///       クエリは busy_timeout=30000（30秒）まで待機し続けてプロセスが
        ///       終了できなかった。
        ///       Dapper の CommandDefinition 経由で token を渡すことで、
        ///       await 中のDB処理が OperationCanceledException で即時中断されるようになる。
        /// </summary>
        public async Task refresh_online_users_async()
        {
            // ▼▼▼ 追加：終了処理中（キャンセル済み）はDB処理を実行しない ▼▼▼
            if (_cts.IsCancellationRequested) return;
            CancellationToken token = _cts.Token;
            try
            {
                // ▼ 追加 [Sprint 8 / Phase 0]：NAS 書込ロックの heartbeat を同じ2分周期で更新する。
                //   保持中のPCだけが更新される（他PCでは no-op）。取得時に未確定だったユーザー名も反映される。
                NasWriteLock.heartbeat();

                string mac = EA_CostManager.UserSession.fetch_mac_address();
                using var conn = database_manager.create_connection();

                // 自分のセッションの updated_at を更新（ハートビート）
                // ▼▼▼ 修正（v0.9.6）：CommandDefinition で token を渡す ▼▼▼
                await conn.ExecuteAsync(new CommandDefinition(@"
                    UPDATE user_sessions
                    SET updated_at = datetime('now','localtime')
                    WHERE mac_address = @mac AND status = 'active'",
                    new { mac },
                    cancellationToken: token));

                // 5分以内に更新されたユーザーをオンラインと判定
                // ▼▼▼ [現場タブ追跡] current_tab も取得（カラム未存在時はフォールバック） ▼▼▼
                System.Collections.Generic.List<dynamic> rows;
                try
                {
                    // ▼▼▼ 修正（v0.9.6）：CommandDefinition で token を渡す ▼▼▼
                    var q = await conn.QueryAsync<dynamic>(new CommandDefinition(@"
                        SELECT mac_address, user_name, current_tab FROM user_sessions
                        WHERE status   = 'active'
                          AND software = 'CostManager'
                          AND updated_at >= datetime('now', '-5 minutes', 'localtime')
                        ORDER BY user_name",
                        cancellationToken: token));
                    rows = q.ToList();
                }
                catch (OperationCanceledException)
                {
                    // ▼▼▼ 追加（v0.9.6）：キャンセル時はそのまま抜ける ▼▼▼
                    throw;
                }
                catch
                {
                    // current_tab カラムが存在しない場合（旧DB）はフォールバック
                    var q = await conn.QueryAsync<dynamic>(new CommandDefinition(@"
                        SELECT mac_address, user_name, '' AS current_tab FROM user_sessions
                        WHERE status   = 'active'
                          AND software = 'CostManager'
                          AND updated_at >= datetime('now', '-5 minutes', 'localtime')
                        ORDER BY user_name",
                        cancellationToken: token));
                    rows = q.ToList();
                }

                online_users.Clear();
                online_user_names.Clear();

                string my_mac = mac;
                foreach (var r in rows)
                {
                    string uname = (string)(r.user_name ?? "");
                    string ctab = (string)(r.current_tab ?? "");

                    // ▼▼▼ [現場タブ追跡] 自分自身は現在のページ状態で current_tab を判断 ▼▼▼
                    // DBの値に依存せず、cost ページ以外にいるときは強制的に空にする
                    // これにより起動直後や他ページ滞在中に古い現場名が残る問題を解消
                    if ((string)(r.mac_address ?? "") == my_mac && _current_page != "cost")
                        ctab = "";

                    var item = new online_user_item
                    {
                        user_name = uname,
                        current_tab = ctab,
                    };
                    online_users.Add(item);
                    online_user_names.Add(item.user_name);
                }

                OnPropertyChanged(nameof(online_user_count));
            }
            catch (OperationCanceledException)
            {
                // ▼▼▼ 追加（v0.9.6）：キャンセルは正常フロー。ログ不要 ▼▼▼
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"オンラインユーザー更新エラー: {ex.Message}");
            }
        }

        /// <summary>
        /// 現在選択中の現場タブをDBに記録する
        /// CostPage のタブ切替時に呼ばれる
        ///
        /// ▼▼▼ 修正（v0.9.6）：全DB処理に CancellationToken を渡す ▼▼▼
        /// </summary>
        public async Task update_current_tab_async(string tab_name)
        {
            // ▼▼▼ 追加：終了処理中はDB処理を実行しない ▼▼▼
            if (_cts.IsCancellationRequested) return;
            CancellationToken token = _cts.Token;
            try
            {
                string mac = EA_CostManager.UserSession.fetch_mac_address();
                using var conn = database_manager.create_connection();

                // ▼▼▼ カラムが存在しない場合は自動追加（NAS/ローカルDB両方対応） ▼▼▼
                try
                {
                    // ▼▼▼ 修正（v0.9.6）：CommandDefinition で token を渡す ▼▼▼
                    await conn.ExecuteAsync(new CommandDefinition(
                        "ALTER TABLE user_sessions ADD COLUMN current_tab TEXT DEFAULT ''",
                        cancellationToken: token));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 既に存在する場合は無視 */ }

                // ▼▼▼ 修正（v0.9.6）：CommandDefinition で token を渡す ▼▼▼
                await conn.ExecuteAsync(new CommandDefinition(@"
                    UPDATE user_sessions
                    SET current_tab = @tab,
                        updated_at  = datetime('now','localtime')
                    WHERE mac_address = @mac AND status = 'active'",
                    new { tab = tab_name, mac },
                    cancellationToken: token));
            }
            catch (OperationCanceledException)
            {
                // ▼▼▼ 追加（v0.9.6）：キャンセルは正常フロー ▼▼▼
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"current_tab更新エラー: {ex.Message}");
            }
        }

        private string _current_page = "dashboard";
        public string current_page
        {
            get => _current_page;
            set
            {
                if (SetProperty(ref _current_page, value))
                {
                    OnPropertyChanged(nameof(is_dashboard));
                    OnPropertyChanged(nameof(is_cost));
                    OnPropertyChanged(nameof(is_import));
                    OnPropertyChanged(nameof(is_settings));
                    OnPropertyChanged(nameof(is_workload)); // ▼ 追加 [Sprint 7B]

                    // ▼▼▼ [現場タブ追跡] 原価集計以外に移動したらcurrent_tabをクリアして即時更新 ▼▼▼
                    if (value != "cost")
                        _ = clear_current_tab_and_refresh_async();
                }
            }
        }

        /// <summary>原価集計ページを離れるときにcurrent_tabをクリアしてTooltipを即時更新</summary>
        private async Task clear_current_tab_and_refresh_async()
        {
            await update_current_tab_async("");
            await refresh_online_users_async();
        }

        public bool is_dashboard => current_page == "dashboard";
        public bool is_cost => current_page == "cost";
        public bool is_import => current_page == "import";
        public bool is_settings => current_page == "settings";
        // ▼ 追加 [Sprint 7B]：工数表ページ表示中か（WorkloadPage の Visibility 用）
        public bool is_workload => current_page == "workload";

        // ▼▼▼ 追加（v0.9.6）：IDisposable 実装 ▼▼▼
        /// <summary>
        /// アプリ終了時のリソース解放処理
        /// MainWindow.shutdown_async() から呼ばれる
        /// 呼び出し順：
        ///   1. DispatcherTimer.Stop()  → 次回Tickを防止
        ///   2. _cts.Cancel()           → await 中のDB処理を即中断（OperationCanceledException）
        ///   3. _cts.Dispose()          → 内部ウェイトハンドルを解放
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _session_heartbeat.Stop(); } catch { /* ignore */ }
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }
} 
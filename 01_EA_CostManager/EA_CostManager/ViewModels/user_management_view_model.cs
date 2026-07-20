using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.ViewModels
{
    /// <summary>
    /// ユーザー管理画面のViewModel
    /// pc_usersテーブルの登録済みユーザーを一覧表示・権限変更する
    /// 管理者のみアクセス可能
    /// </summary>

    // ユーザー行モデル
    public class pc_user_item : base_view_model
    {
        public int id { get; set; }
        public string mac_address { get; set; } = "";
        public string pc_name { get; set; } = "";
        public string user_name { get; set; } = "";
        public int employee_id { get; set; }

        // ▼▼▼ 修正：is_admin_raw をプロパティ化し、変更時に role_label も通知する ▼▼▼
        // 以前は { get; set; } だったため role_label の再描画が走らなかった
        private int _is_admin_raw;
        public int is_admin_raw
        {
            get => _is_admin_raw;
            set
            {
                if (SetProperty(ref _is_admin_raw, value))
                    OnPropertyChanged(nameof(role_label)); // role_labelの表示を連動更新
            }
        }

        // 権限の表示文字列（is_admin_raw が変わると自動更新される）
        public string role_label => is_admin_raw switch
        {
            2 => "管理者",
            1 => "一般",
            _ => "閲覧"
        };

        // 権限変更用プロパティ（ComboBoxバインド用）
        private int _selected_role;
        public int selected_role
        {
            get => _selected_role;
            set => SetProperty(ref _selected_role, value);
        }

        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";

        // 自分自身かどうか（自分の権限は変更不可）
        public bool is_self => id == EA_CostManager.UserSession.user_id;
    }

    /// <summary>
    /// ▼▼▼ 修正（v0.9.6）：IDisposable を実装 ▼▼▼
    /// 画面切替時・アプリ終了時に DispatcherTimer を停止し、
    /// CancellationTokenSource を Cancel/Dispose するため。
    /// </summary>
    public class user_management_view_model : base_view_model, IDisposable
    {
        public ObservableCollection<pc_user_item> users { get; } = new();

        private string _status = "";
        public string status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        // 管理者かどうか（非管理者は操作不可）
        public bool can_edit => EA_CostManager.UserSession.is_admin;

        public ICommand save_command { get; }

        // ▼▼▼ 追加：権限ポーリング用タイマー ▼▼▼
        // 他PCで自分の権限が変更された場合に UserSession へ反映するための60秒ポーリング
        private readonly DispatcherTimer _role_poll_timer;

        // ▼▼▼ 追加（v0.9.6）：DB処理キャンセル用トークン ▼▼▼
        // Dispose 時に Cancel() を呼ぶことで load_async / save_async / poll_my_role_async の
        // 進行中のDB処理を即時中断する（NAS DB の busy_timeout=30秒待ちを回避）
        private CancellationTokenSource _cts = new();

        // ▼▼▼ 追加（v0.9.6）：二重 Dispose 防止フラグ ▼▼▼
        private bool _disposed = false;

        public user_management_view_model()
        {
            save_command = new RelayCommand(async () => await save_async());
            _ = load_async();

            // ▼▼▼ 追加：60秒ごとに自分の権限をDBから再確認する ▼▼▼
            // UserSession.is_admin は起動時に設定された静的値のため、
            // 他PCで権限変更されてもアプリ再起動なしには反映されない問題を解消する
            _role_poll_timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _role_poll_timer.Tick += async (_, _) => await poll_my_role_async();
            _role_poll_timer.Start();
        }

        public async Task load_async()
        {
            // ▼▼▼ 追加（v0.9.6）：キャンセル済みなら処理スキップ ▼▼▼
            if (_cts.IsCancellationRequested) return;
            CancellationToken token = _cts.Token;
            try
            {
                users.Clear();
                using var conn = database_manager.create_connection();

                // ▼▼▼ 修正（v0.9.6）：CommandDefinition で token を渡す ▼▼▼
                var rows = await conn.QueryAsync<pc_user_item>(new CommandDefinition(@"
                    SELECT
                        id, mac_address, pc_name, user_name,
                        employee_id, is_admin AS is_admin_raw,
                        created_at, updated_at
                    FROM pc_users
                    ORDER BY id",
                    cancellationToken: token));

                foreach (var row in rows)
                {
                    row.selected_role = row.is_admin_raw;
                    users.Add(row);
                }

                status = $"{users.Count}件のユーザーを読み込みました。";
            }
            catch (OperationCanceledException)
            {
                // ▼▼▼ 追加（v0.9.6）：キャンセルは正常フロー ▼▼▼
            }
            catch (Exception ex)
            {
                status = $"読み込みエラー：{ex.Message}";
            }
        }

        private async Task save_async()
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (!can_edit)
            {
                status = "⚠️ 管理者権限が必要です。";
                return;
            }

            // ▼▼▼ 追加（v0.9.6）：キャンセル済みなら処理スキップ ▼▼▼
            if (_cts.IsCancellationRequested) return;
            CancellationToken token = _cts.Token;

            try
            {
                using var conn = database_manager.create_connection();

                foreach (var user in users)
                {
                    // 自分自身の権限は変更不可
                    if (user.is_self) continue;

                    // ▼▼▼ 修正（v0.9.6）：CommandDefinition で token を渡す ▼▼▼
                    await conn.ExecuteAsync(new CommandDefinition(@"
                        UPDATE pc_users
                        SET is_admin   = @role,
                            updated_at = datetime('now','localtime')
                        WHERE id = @id",
                        new { role = user.selected_role, id = user.id },
                        cancellationToken: token));
                }

                status = "✅ ユーザー権限を保存しました。";

                // ▼▼▼ 追加：保存後に一覧を再読み込みして role_label を最新化する ▼▼▼
                // save後にload_asyncを呼ぶことで「現在の権限」列を正しく更新する
                await load_async();
            }
            catch (OperationCanceledException)
            {
                // ▼▼▼ 追加（v0.9.6）：キャンセルは正常フロー ▼▼▼
            }
            catch (Exception ex)
            {
                status = $"❌ 保存エラー：{ex.Message}";
            }
        }

        // ▼▼▼ 追加：自分自身の権限をDBから再確認してUserSessionに反映する ▼▼▼
        // ポーリングタイマーから60秒ごとに呼ばれる
        // 権限が変わっていた場合は UserSession を更新し、UI上の can_edit も再通知する
        //
        // ▼▼▼ 修正（v0.9.6）：全DB処理に CancellationToken を渡す ▼▼▼
        private async Task poll_my_role_async()
        {
            // ▼▼▼ 追加（v0.9.6）：キャンセル済みなら処理スキップ ▼▼▼
            if (_cts.IsCancellationRequested) return;
            CancellationToken token = _cts.Token;
            try
            {
                // ゲストログイン（user_id = 0）はポーリング不要
                if (EA_CostManager.UserSession.user_id <= 0) return;

                using var conn = database_manager.create_connection();

                // ▼▼▼ 修正（v0.9.6）：CommandDefinition で token を渡す ▼▼▼
                var new_role = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                    SELECT is_admin FROM pc_users WHERE id = @id",
                    new { id = EA_CostManager.UserSession.user_id },
                    cancellationToken: token));

                if (new_role == null) return;

                // 権限に変化があった場合のみ UserSession を更新する
                bool new_is_admin = (new_role.Value == 2);
                if (new_is_admin != EA_CostManager.UserSession.is_admin)
                {
                    EA_CostManager.UserSession.update_role(new_is_admin);

                    // can_edit の再通知（UI上の保存ボタン活性状態を更新）
                    OnPropertyChanged(nameof(can_edit));

                    // 権限が降格された場合はユーザーに通知する
                    if (!new_is_admin)
                    {
                        status = "⚠️ あなたの権限が変更されました。一部の操作が制限されます。";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // ▼▼▼ 追加（v0.9.6）：キャンセルは正常フロー ▼▼▼
            }
            catch
            {
                // ポーリングエラーは無視（NAS切断等の一時的なエラーに対応）
            }
        }

        // ▼▼▼ 追加：画面を離れる際にタイマーを停止するためのクリーンアップ ▼▼▼
        // SettingsPage の IsVisibleChanged で DataContext が差し替わるため、
        // 古いインスタンスのタイマーが残り続けないよう明示的に停止する
        //
        // ▼▼▼ 修正（v0.9.6）：Dispose() のラッパーに変更 ▼▼▼
        // cleanup() を既存呼び出し箇所の後方互換のため残しつつ、
        // 実体は Dispose() に集約する。
        public void cleanup()
        {
            Dispose();
        }

        // ▼▼▼ 追加（v0.9.6）：IDisposable 実装 ▼▼▼
        /// <summary>
        /// アプリ終了・画面切替時のリソース解放処理
        /// 呼び出し順：
        ///   1. DispatcherTimer.Stop()  → 次回Tickを防止
        ///   2. _cts.Cancel()           → await 中のDB処理を即中断
        ///   3. _cts.Dispose()          → 内部ウェイトハンドルを解放
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _role_poll_timer.Stop(); } catch { /* ignore */ }
            try { _cts.Cancel(); } catch { /* ignore */ }
            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }
}
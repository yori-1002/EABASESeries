using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using EA_CostManager.Services;

namespace EA_CostManager.ViewModels
{
    // ─── ユーザーカード用ラッパーVM ─────────────────────────────────────────────

    /// <summary>
    /// ユーザーボックス表示用ラッパー
    /// is_selected を持つことで、XAML側でDataTriggerによる選択ハイライトが可能になる
    /// </summary>
    public class error_user_card_vm : INotifyPropertyChanged
    {
        private bool _is_selected;

        // ── DBから取得した値（変更なし） ──────────────────────────────────
        public string user_name        { get; init; } = "";
        public string mac_address      { get; init; } = "";
        public int    total_count      { get; init; }
        public int    unhandled_count  { get; init; }
        public string last_occurred_at { get; init; } = "";

        // ── UI表示用の計算プロパティ ────────────────────────────────────
        /// <summary>unhandled件数バッジ（0件のときは空文字 → Visibility=Collapsed）</summary>
        public string unhandled_badge
            => unhandled_count > 0 ? $"⚠ 未処理 {unhandled_count}件" : "";

        /// <summary>unhandledが1件以上あるか（BoolToVisibilityConverter用）</summary>
        public bool has_unhandled => unhandled_count > 0;

        // ── 選択状態（DataTriggerでボーダーハイライトに使用） ──────────
        public bool is_selected
        {
            get => _is_selected;
            set { _is_selected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ─── メインViewModel ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sprint 5D: エラーログ参照画面（ErrorLogPage）のViewModel
    /// </summary>
    public class ErrorLogViewModel : INotifyPropertyChanged
    {
        // ── プライベートフィールド ───────────────────────────────────────
        private bool                _is_loading;
        private error_user_card_vm? _selected_card;
        private string              _log_title = "← ユーザーを選択してください";

        // ── バインディングプロパティ ─────────────────────────────────────

        /// <summary>ユーザーカード一覧（UI左上のボックス群）</summary>
        public ObservableCollection<error_user_card_vm> user_cards { get; } = new();

        /// <summary>選択中ユーザーのログ一覧（DataGrid）</summary>
        public ObservableCollection<error_log_item> log_list { get; } = new();

        /// <summary>ローディング中フラグ（ProgressBar制御用）</summary>
        public bool is_loading
        {
            get => _is_loading;
            set { _is_loading = value; OnPropertyChanged(); }
        }

        /// <summary>ログセクションのタイトル（選択ユーザー名を表示）</summary>
        public string log_title
        {
            get => _log_title;
            set { _log_title = value; OnPropertyChanged(); }
        }

        /// <summary>ユーザーが1人以上いるか（ScrollViewer表示切替用）</summary>
        public bool has_users => user_cards.Count > 0;

        // ── コマンド ────────────────────────────────────────────────────

        /// <summary>ユーザーカードをクリックしたときのコマンド</summary>
        public ICommand select_user_command { get; }

        /// <summary>🔄 更新ボタン（ユーザー一覧 + 選択中ユーザーのログを再取得）</summary>
        public ICommand refresh_command { get; }

        // ── コンストラクタ ──────────────────────────────────────────────

        public ErrorLogViewModel()
        {
            // ※ relay_command はプロジェクト既存のものを使用すること
            //   存在しない場合は ViewModels/relay_command.cs を追加する（後述）
            select_user_command = new relay_command(async param =>
            {
                if (param is error_user_card_vm card)
                    await on_user_selected_async(card);
            });

            refresh_command = new relay_command(async _ =>
            {
                await load_users_async();
                // 選択中ユーザーがいれば再読み込み
                if (_selected_card != null)
                    await load_logs_async(_selected_card);
            });

            // 初回データロード（コンストラクタからのfire-and-forget）
            _ = load_users_async();
        }

        // ── ローカルメソッド ────────────────────────────────────────────

        /// <summary>ユーザー一覧を読み込む</summary>
        public async Task load_users_async()
        {
            is_loading = true;
            try
            {
                var summaries = await ErrorLogService.get_user_summary_async();
                user_cards.Clear();

                foreach (var s in summaries)
                {
                    user_cards.Add(new error_user_card_vm
                    {
                        user_name        = s.user_name,
                        mac_address      = s.mac_address,
                        total_count      = s.total_count,
                        unhandled_count  = s.unhandled_count,
                        last_occurred_at = s.last_occurred_at
                    });
                }

                // 選択状態の復元（更新後も選択を維持）
                if (_selected_card != null)
                {
                    foreach (var c in user_cards)
                        c.is_selected = c.mac_address == _selected_card.mac_address;
                }

                // ユーザー数の変化をUIに通知
                OnPropertyChanged(nameof(has_users));
            }
            finally { is_loading = false; }
        }

        /// <summary>ユーザーカード選択時の処理（ハイライト切替 + ログ読み込み）</summary>
        private async Task on_user_selected_async(error_user_card_vm card)
        {
            // 全カードの選択を解除してから選択
            foreach (var c in user_cards)
                c.is_selected = false;

            card.is_selected = true;
            _selected_card   = card;

            await load_logs_async(card);
        }

        /// <summary>指定ユーザーのログを読み込む</summary>
        private async Task load_logs_async(error_user_card_vm card)
        {
            is_loading = true;
            log_title  = $"{card.user_name} のエラーログ（直近100件）";
            try
            {
                var logs = await ErrorLogService.get_logs_by_user_async(card.mac_address);
                log_list.Clear();
                foreach (var l in logs)
                    log_list.Add(l);
            }
            finally { is_loading = false; }
        }

        // ── INotifyPropertyChanged ──────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

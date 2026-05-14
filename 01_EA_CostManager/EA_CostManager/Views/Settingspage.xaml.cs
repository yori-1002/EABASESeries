using System.Windows.Controls;
using System.Windows.Input;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Views
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
            // ▼▼▼ DataContextをLoadedで設定（UserSessionが確定してから初期化する）▼▼▼
            // コンストラクタ時点ではUserSession未設定のためLoadedで遅延初期化
            // IsVisibleChangedで毎回再初期化することで権限変更を即時反映する
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                    DataContext = new settings_view_model();
            };
        }

        // ▼▼▼ ナビゲーション項目クリック ▼▼▼
        private void nav_item_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.Border border) return;
            if (DataContext is not settings_view_model vm) return;

            string key = border.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(key)) return;

            var nav = System.Linq.Enumerable.FirstOrDefault(
                vm.nav_items, n => n.key == key);
            if (nav != null)
                vm.selected_nav = nav;
        }

        // ▼▼▼ 管理メニュークリック（展開・折りたたみ） ▼▼▼
        private void nav_admin_menu_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not settings_view_model vm) return;
            vm.toggle_admin_menu_expanded();
        }

        // ▼▼▼ マスタ管理ヘッダークリック（展開・折りたたみ） ▼▼▼
        private void nav_master_header_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not settings_view_model vm) return;
            vm.toggle_master_expanded();
        }

        // ▼▼▼ テスト用DBパス クイックセットボタン ▼▼▼
        private void btn_set_test_path_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is not settings_view_model vm) return;
            vm.set_test_nas_path();
            System.Windows.MessageBox.Show(
                $"テスト用DBパスをセットしました。\n\n{settings_view_model.TEST_NAS_PATH}\n\n「設定を保存して再起動」を押してください。",
                "テスト用パスをセット",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        // ▼▼▼ 標準DB（本番NAS）クイックセットボタン ▼▼▼
        private void btn_set_production_path_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is not settings_view_model vm) return;
            vm.set_production_nas_path();
            System.Windows.MessageBox.Show(
                $"標準DBパスをセットしました。\n\n{settings_view_model.PRODUCTION_NAS_PATH}\n\n「設定を保存して再起動」を押してください。",
                "標準DBパスをセット",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        // ▼▼▼ 詳細設定の保存ボタン（即時反映分のみ） ▼▼▼
        // カラー・フォント・色帯は即時反映
        private void btn_save_advanced_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is not settings_view_model vm) return;
            vm.save_advanced_command.Execute(null);
        }

        // ▼▼▼ 詳細設定：保存して再起動ボタン ▼▼▼
        // タブ表示モード・起動ページ等を反映するために再起動が必要な場合に使う
        private async void btn_save_and_restart_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is not settings_view_model vm) return;

            // ▼ 修正 [v1.0.3] 二重クリック防止
            if (sender is System.Windows.Controls.Button btn)
                btn.IsEnabled = false;

            // まず保存（即時反映も実行される）
            vm.save_advanced_command.Execute(null);

            // ▼ 修正 [v1.0.3] 400ms固定待ち → is_busy ループ待ち（最大5秒）に変更
            //   旧実装は Task.Delay(400) の固定待ちだったが、
            //   save_advanced_async() 内部の Task.Delay(300) と非常に近接しており、
            //   システムビジー時に逆転して保存未完了で再起動するケースがあった。
            //   これがバグ②「起動時表示ページが反映されない」の原因と推定される。
            //   btn_save_db_and_restart_Click と同じ is_busy ループ方式に統一する。
            int wait_count = 0;
            while (vm.is_busy && wait_count < 50)  // 100ms × 50 = 5秒
            {
                await System.Threading.Tasks.Task.Delay(100);
                wait_count++;
            }

            // ▼ 追加 [v1.0.3] 保存結果にエラーが含まれている場合は再起動を中断
            if (vm.status_message.StartsWith("❌"))
            {
                System.Windows.MessageBox.Show(
                    $"設定の保存に失敗したため再起動をキャンセルしました。\n\n{vm.status_message}",
                    "保存エラー",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);

                if (sender is System.Windows.Controls.Button btn2)
                    btn2.IsEnabled = true;
                return;
            }

            // 自動再起動
            app_restart_helper.restart();
        }

        // ▼▼▼ 追加：DB設定「設定を保存して再起動」ボタン ▼▼▼
        // save_settings_command で保存完了後、自動的にアプリを再起動する
        // 保存処理は非同期のため完了を待ってから再起動する
        private async void btn_save_db_and_restart_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is not settings_view_model vm) return;

            // 保存中は二重クリック防止
            if (sender is System.Windows.Controls.Button btn)
                btn.IsEnabled = false;

            // DB設定を保存（非同期・内部でNAS切替も実行）
            vm.save_settings_command.Execute(null);

            // save_settings_async の完了を待つ（is_busy が false になるまで最大5秒）
            // is_busy=true の間はループして待機し、完了したら再起動する
            int wait_count = 0;
            while (vm.is_busy && wait_count < 50)
            {
                await System.Threading.Tasks.Task.Delay(100);
                wait_count++;
            }

            // 保存結果にエラーが含まれている場合は再起動を中断してユーザーに通知
            if (vm.status_message.StartsWith("❌"))
            {
                System.Windows.MessageBox.Show(
                    $"設定の保存に失敗したため再起動をキャンセルしました。\n\n{vm.status_message}",
                    "保存エラー",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);

                if (sender is System.Windows.Controls.Button btn2)
                    btn2.IsEnabled = true;
                return;
            }

            // 自動再起動
            app_restart_helper.restart();
        }
    }
}
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EA_CostManager.ViewModels;

namespace EA_CostManager.Views
{
    public partial class DashboardPage : UserControl
    {
        // ▼▼▼ 自動更新タイマー ▼▼▼
        // ダッシュボード表示中に一定間隔でDBを再取得して最新状態を反映する
        // 非表示時（他ページ閲覧中）はタイマーを停止してDB負荷を抑える
        private readonly DispatcherTimer _auto_refresh_timer;

        // 自動更新間隔（60秒）
        private static readonly TimeSpan AUTO_REFRESH_INTERVAL = TimeSpan.FromSeconds(60);

        public DashboardPage()
        {
            InitializeComponent();

            // ▼▼▼ 自動更新タイマーを初期化 ▼▼▼
            // タイマーはUIスレッドで動作するため、Dispatcher経由での操作不要
            _auto_refresh_timer = new DispatcherTimer
            {
                Interval = AUTO_REFRESH_INTERVAL
            };
            _auto_refresh_timer.Tick += auto_refresh_timer_tick;

            // ▼▼▼ ページ表示・非表示でタイマーを制御 ▼▼▼
            // 表示時：VMを初期化（または再ロード）してタイマー開始
            // 非表示時：タイマー停止（無駄なDB問い合わせを防ぐ）
            IsVisibleChanged += async (_, e) =>
            {
                if ((bool)e.NewValue)
                {
                    // VM初期化またはデータ再ロード
                    if (DataContext is not dashboard_view_model)
                    {
                        // ★修正：new後に必ずload_asyncを呼ぶ（初回表示時のデータ未取得バグを修正）
                        var new_vm = new dashboard_view_model();
                        DataContext = new_vm;
                        await new_vm.load_async();
                    }
                    else if (DataContext is dashboard_view_model vm)
                        await vm.load_async();

                    // タイマー開始（ページを開くたびにリセットして60秒後から自動更新）
                    _auto_refresh_timer.Stop();
                    _auto_refresh_timer.Start();
                }
                else
                {
                    // 非表示時はタイマー停止
                    _auto_refresh_timer.Stop();
                }
            };
        }

        // ▼▼▼ タイマーTickハンドラー（自動更新）▼▼▼
        // is_busy=true（手動更新中）の場合はスキップして次のTickを待つ
        // スクロール位置を保存・復元して画面が先頭に戻らないようにする
        private async void auto_refresh_timer_tick(object? sender, EventArgs e)
        {
            if (DataContext is not dashboard_view_model vm) return;

            // 手動更新中またはロード中の場合はスキップ
            if (vm.is_busy) return;

            // スクロール位置を保存（Refreshによるリセット対策）
            double saved_offset = main_scroll.VerticalOffset;

            await vm.load_async();

            // 描画完了後にスクロール位置を復元
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => main_scroll.ScrollToVerticalOffset(saved_offset)));
        }

        // ▼▼▼ 手動更新ボタンのクリックハンドラー ▼▼▼
        private async void btn_refresh_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not dashboard_view_model vm) return;

            double saved_offset = main_scroll.VerticalOffset;

            await vm.load_async();

            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => main_scroll.ScrollToVerticalOffset(saved_offset)));
        }

        // ▼▼▼ [Sprint 6] アップデートインストールボタン ▼▼▼
        private async void btn_install_update_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not dashboard_view_model vm) return;
            if (!vm.has_pending_update) return;

            var result = MessageBox.Show(
                $"v{vm.latest_version} のインストーラーをNASからダウンロードして実行します。\n\n" +
                "アプリが終了してインストーラーが起動します。\n続行しますか？",
                "アップデートの確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.OK) return;

            if (btn_install_update != null) btn_install_update.IsEnabled = false;
            await vm.install_update_async();
            if (btn_install_update != null) btn_install_update.IsEnabled = true;
        }

        // ▼▼▼ 追加：お知らせの添付ファイルダウンロードハンドラー ▼▼▼
        // news.json の url フィールドに設定されたファイルを取得する
        // ・http/https → デフォルトブラウザで開く
        // ・UNCパス（\\NAS\...）またはローカルパス → SaveFileDialogで保存先選択 → コピー
        // ・ファイルが大きい場合でもUIがフリーズしないよう Task.Run で非同期コピー
        private async void btn_news_download_Click(object sender, RoutedEventArgs e)
        {
            // Tag に url 文字列をバインドしている
            if (sender is not Button btn) return;
            if (btn.Tag is not string url || string.IsNullOrWhiteSpace(url)) return;

            // HTTP / HTTPS の場合はブラウザで開く
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true   // デフォルトブラウザに委ねる
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"ブラウザでの表示に失敗しました。\n\n{ex.Message}",
                        "エラー",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                return;
            }

            // ファイルパス（NAS UNCパス含む）の場合：SaveFileDialog で保存先選択
            // ファイルの存在チェック（NASが切断されている場合の早期エラー検出）
            if (!File.Exists(url))
            {
                MessageBox.Show(
                    $"ファイルが見つかりませんでした。\n\nパス：{url}\n\nNASへの接続を確認してください。",
                    "ファイルが見つかりません",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // 元ファイル名をSaveFileDialogの初期値として使用
            string source_file_name = Path.GetFileName(url);
            string source_extension = Path.GetExtension(url);

            // 拡張子に応じたフィルター文字列を生成（例：PDFファイル|*.pdf|すべてのファイル|*.*）
            string filter = string.IsNullOrEmpty(source_extension)
                ? "すべてのファイル|*.*"
                : $"{source_extension.TrimStart('.').ToUpper()}ファイル|*{source_extension}|すべてのファイル|*.*";

            var save_dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = source_file_name,
                Filter = filter,
                Title = "ダウンロード先のフォルダーを選択してください"
            };

            // キャンセルされた場合は何もしない
            if (save_dlg.ShowDialog() != true) return;

            // ダウンロード中はボタンを無効化してダブルクリック防止
            btn.IsEnabled = false;
            btn.Content = "📎 ダウンロード中...";

            try
            {
                string dest_path = save_dlg.FileName;

                // NASからのコピーはUIスレッドをブロックしないよう Task.Run で実行
                await System.Threading.Tasks.Task.Run(() =>
                    File.Copy(url, dest_path, overwrite: true));

                MessageBox.Show(
                    $"「{source_file_name}」をダウンロードしました。\n\n保存先：{dest_path}",
                    "ダウンロード完了",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (IOException io_ex)
            {
                // ファイルI/Oエラー（NAS切断・ディスク満杯等）
                MessageBox.Show(
                    $"ダウンロードに失敗しました。\n\n{io_ex.Message}",
                    "ダウンロードエラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (UnauthorizedAccessException auth_ex)
            {
                // アクセス権限エラー
                MessageBox.Show(
                    $"ファイルへのアクセスが拒否されました。\n\n{auth_ex.Message}",
                    "アクセスエラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                // その他の予期しないエラー
                MessageBox.Show(
                    $"予期しないエラーが発生しました。\n\n{ex.Message}",
                    "エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                // 成功・失敗に関わらずボタンを元に戻す
                btn.IsEnabled = true;
                btn.Content = "📎 添付ファイルをダウンロード";
            }
        }
    }
}
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace EA_DailyReport
{
    /// <summary>
    /// アプリケーションの自動再起動ヘルパー
    /// 設定保存後に現在のプロセスを終了し、同じexeを再起動する
    /// </summary>
    public static class app_restart_helper
    {
        /// <summary>
        /// アプリを再起動する
        /// 現在のexeパスを取得して新プロセスを起動後、現プロセスを終了する
        /// </summary>
        public static void restart()
        {
            // 現在のexeパスを取得
            string exe_path = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "EA_DailyReport.exe");

            try
            {
                // 新しいプロセスとして同じexeを起動
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe_path,
                    UseShellExecute = true
                });

                // 現在のプロセスを終了
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                // 再起動に失敗した場合はメッセージを表示して手動再起動を促す
                MessageBox.Show(
                    $"自動再起動に失敗しました。\n手動でアプリを再起動してください。\n\nエラー：{ex.Message}",
                    "再起動エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
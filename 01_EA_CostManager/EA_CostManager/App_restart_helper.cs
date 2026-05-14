using System.Diagnostics;
using System.IO;
using System.Windows;

namespace EA_CostManager
{
    /// <summary>
    /// アプリケーションの自動再起動ヘルパー
    /// 設定保存後に現在のプロセスを終了し、同じexeを再起動する
    /// </summary>
    public static class app_restart_helper
    {
        /// <summary>
        /// アプリを再起動する
        /// ▼ 修正 [v1.0.3] 旧実装は Process.Start を呼んだ直後に Shutdown を呼んでいたため、
        ///   現プロセスの終了処理（DB クローズ・セッション inactive 化・JSON 書き込み等）が
        ///   完了する前に新プロセスが起動し、以下の競合を引き起こす可能性があった：
        ///   ・SQLite DB ファイルのロック競合（WAL/SHM ファイル残留）
        ///   ・ユーザー設定 JSON の書き込み中に新プロセスが読み込み → 破損検出
        ///   ・セッション残留（旧 inactive 化前に新 active 化）
        ///   これらが「保存して再起動で接続できなくなる」（バグ①）の原因と推定される。
        ///
        ///   v1.0.3 では Application.Exit イベント内で Process.Start を呼ぶ方式に変更。
        ///   これにより、現プロセスの終了処理が完全に完了してから新プロセスが起動するため、
        ///   ファイルロック競合やセッション競合が原理的に発生しなくなる。
        /// </summary>
        public static void restart()
        {
            // 現在のexeパスを取得
            string exe_path = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "EA_CostManager.exe");

            try
            {
                // ▼ 修正 [v1.0.3] Exit イベントで新プロセスを起動する方式に変更
                //   Application.Exit は Shutdown 処理が完了した直後（OnExit 後）に発火する。
                //   このタイミングでは現プロセスの全リソース（DB接続・ファイルハンドル等）が
                //   解放済みのため、新プロセスとの競合が発生しない。
                Application.Current.Exit += (s, e) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exe_path,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception startEx)
                    {
                        // Exit イベント内では MessageBox を出しても表示されないことがあるため
                        // Debug 出力のみ行う
                        System.Diagnostics.Debug.WriteLine(
                            $"[app_restart_helper] Exit内 Process.Start 失敗: {startEx.GetType().Name} : {startEx.Message}");
                    }
                };

                // 現在のプロセスを終了
                // → 終了処理が完了したあと、上記 Exit イベントで新プロセスが起動する
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                // ▼ 修正 [v1.0.3] Debug出力にもログ記録
                System.Diagnostics.Debug.WriteLine(
                    $"[app_restart_helper] restart 失敗: {ex.GetType().Name} : {ex.Message}");

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
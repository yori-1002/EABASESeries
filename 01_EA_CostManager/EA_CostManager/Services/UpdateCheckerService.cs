using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace EA_CostManager.Services
{
    /// <summary>
    /// [Sprint 6] アップデートチェック・ダウンロード・実行サービス
    ///
    /// 運用フロー：
    ///   1. NAS の 02_Updates\version.txt に新バージョン番号を書く
    ///   2. NAS の 02_Updates\ に新インストーラー（EA_CostManager_setup_vX.X.X.exe）を置く
    ///   3. 次回ユーザー起動時に自動検知 → 通知バナー表示
    ///   4. ユーザーが「今すぐ更新」を押すとインストーラーをダウンロードして実行
    /// </summary>
    public static class UpdateCheckerService
    {
        /// <summary>
        /// NASのversion.txtを読んで最新バージョンを取得する
        /// NAS未接続や読み込み失敗はサイレントに無視する
        ///
        /// ▼▼▼ 修正（v0.9.6）：CancellationToken を受け取れるように変更 ▼▼▼
        /// 理由：アプリ終了時に NAS の version.txt 読み込み中だと
        ///       File.ReadAllTextAsync が SMB タイムアウト（数十秒）まで居座り、
        ///       プロセスが終了できなくなる問題があった。
        ///       App.OnStartup から渡される _app_cts.Token でキャンセル可能にする。
        ///
        /// また、File.Exists(nasPath) は同期呼び出しでNAS切断時にブロックするため、
        /// Task.Run で包んで外部からキャンセル可能にする。
        /// </summary>
        /// <param name="ct">キャンセルトークン（アプリ終了時に発火）</param>
        /// <returns>最新バージョン文字列（例: "1.1.0"）。取得失敗時はnull</returns>
        public static async Task<string?> get_latest_version_async(CancellationToken ct = default)
        {
            try
            {
                string version_file = AppVersionInfo.NAS_VERSION_FILE;

                // ▼▼▼ 修正（v0.9.6）：File.Exists を Task.Run で非同期化 ▼▼▼
                // NAS 切断時の File.Exists は数秒〜数十秒ブロックするため、
                // 外部からキャンセルできるように Task.Run で包む。
                // 中の File.Exists 自体は中断できないが、呼び出し元（await）は
                // キャンセル時に即座に抜けるのでシャットダウンが進行できる。
                bool exists = await Task.Run(() => File.Exists(version_file), ct);
                if (!exists) return null;

                // 非同期でファイル読み込み（NAS遅延対策）
                // ▼▼▼ 修正（v0.9.6）：ReadAllTextAsync に CancellationToken を渡す ▼▼▼
                string content = await File.ReadAllTextAsync(version_file, ct);
                string version = content.Trim();

                // バージョン形式チェック（Major.Minor.Patch）
                if (System.Version.TryParse(version, out _))
                    return version;

                return null;
            }
            catch (OperationCanceledException)
            {
                // ▼▼▼ 修正（v0.9.6）：キャンセル例外は正常な終了フロー ▼▼▼
                // アプリ終了時のキャンセルなのでログを出さずに null を返す
                return null;
            }
            catch
            {
                // NAS未接続・アクセス権限なし等は無視
                return null;
            }
        }

        /// <summary>
        /// 最新バージョンのインストーラーをNASからローカルにコピーして実行する
        /// インストーラー実行後はアプリを終了する
        ///
        /// ▼▼▼ 修正（v0.9.6）：CancellationToken 受け取り対応 ▼▼▼
        /// ユーザーが更新開始後にアプリを × で閉じたケースに備える。
        /// 既定値 default のため既存呼び出し（引数なし）はそのまま動作する。
        /// </summary>
        /// <param name="latest_version">最新バージョン文字列</param>
        /// <param name="ct">キャンセルトークン（アプリ終了時に発火）</param>
        public static async Task<bool> download_and_run_async(
            string latest_version,
            CancellationToken ct = default)
        {
            try
            {
                // NAS上のインストーラーファイル名（例: EA_CostManager_setup_v1.1.0.exe）
                string installer_name = $"{AppVersionInfo.APP_NAME}_setup_v{latest_version}.exe";
                string nas_installer = Path.Combine(AppVersionInfo.NAS_UPDATE_DIR, installer_name);

                // ▼▼▼ 修正（v0.9.6）：File.Exists を Task.Run で非同期化 ▼▼▼
                bool nas_exists = await Task.Run(() => File.Exists(nas_installer), ct);
                if (!nas_exists)
                {
                    MessageBox.Show(
                        $"インストーラーが見つかりませんでした。\n\n{nas_installer}\n\n" +
                        "管理者にお問い合わせください。",
                        "ファイルが見つかりません",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                // ローカルの一時フォルダにコピー（NASから直接実行するとトラブルが起きやすい）
                string temp_dir = Path.Combine(Path.GetTempPath(), "EA_CostManager_update");
                Directory.CreateDirectory(temp_dir);
                string local_installer = Path.Combine(temp_dir, installer_name);

                // コピー（進捗表示は省略・NASなので数秒で完了するはず）
                // ▼▼▼ 修正（v0.9.6）：Task.Run にキャンセルトークンを渡す ▼▼▼
                await Task.Run(() => File.Copy(nas_installer, local_installer, overwrite: true), ct);

                // インストーラーを実行してアプリを終了
                Process.Start(new ProcessStartInfo
                {
                    FileName = local_installer,
                    UseShellExecute = true,
                });

                // 少し待ってからアプリ終了（インストーラーが起動するのを待つ）
                // ▼▼▼ 修正（v0.9.6）：Task.Delay にキャンセルトークンを渡す ▼▼▼
                await Task.Delay(1500, ct);
                Application.Current.Shutdown();
                return true;
            }
            catch (OperationCanceledException)
            {
                // ▼▼▼ 修正（v0.9.6）：キャンセル例外は正常な終了フロー ▼▼▼
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"アップデートの実行に失敗しました。\n\n{ex.Message}\n\n" +
                    "手動でNASからインストーラーをダウンロードしてください。",
                    "アップデートエラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }
    }
}
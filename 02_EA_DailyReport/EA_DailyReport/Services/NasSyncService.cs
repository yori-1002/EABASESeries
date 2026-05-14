// ============================================================
// Services/NasSyncService.cs
// EA_DailyReport専用のNAS同期サービス
// 役割：ローカルDB↔NAS DBの双方向自動同期（2分間隔・ランダム時間ずらし）
//
// ▼ Sprint 1：最小限のスタブ実装
//   - 起動・停止のみ実装（実際の同期処理は次Sprint以降で実装）
//   - App.xaml.cs から start() で起動、停止はCancellationTokenで制御
//
// ▼ 今後の拡張予定（Sprint 2以降）
//   - sync_local_to_nas_async()：ローカルDBの未同期レコードをNASに反映
//   - sync_nas_to_local_async()：NAS DBの差分をローカルに取り込み
//   - 同期失敗時のリトライキュー処理
//   - 通知プログラム経由のバックグラウンド同期
// ============================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace EA_DailyReport.Services;

public static class NasSyncService
{
    // 同期間隔の基本値（分）
    private const int SYNC_INTERVAL_MIN = 2;

    // ランダム時間ずらしの最大値（秒）
    // 全PCの同期タイミングを分散させてNASへの同時書き込みを防ぐ
    private const int RANDOM_OFFSET_MAX_SEC = 30;

    // 同期サービス起動済みかどうかのフラグ
    private static bool _is_running = false;

    // バックグラウンドタスクの参照（停止時に待機するため）
    private static Task? _sync_task = null;

    // ランダム時間ずらしの値（起動時に1回決定して固定）
    private static int _random_offset_sec = 0;

    /// <summary>
    /// NAS同期サービスを起動する
    /// App.xaml.cs の OnStartup から呼ばれる
    /// CancellationToken でアプリ終了時に同期処理を停止する
    /// </summary>
    public static void start(CancellationToken ct)
    {
        // 既に起動済みなら何もしない（多重起動防止）
        if (_is_running)
            return;

        _is_running = true;

        // 起動時にランダム値を1回決定（0〜30秒）
        // この値は次回起動まで固定される
        var rand = new Random();
        _random_offset_sec = rand.Next(0, RANDOM_OFFSET_MAX_SEC + 1);

        write_log($"NasSyncService起動：同期間隔 {SYNC_INTERVAL_MIN}分 + {_random_offset_sec}秒");

        // バックグラウンドタスクとして同期ループを起動
        _sync_task = Task.Run(() => sync_loop_async(ct), ct);
    }

    /// <summary>
    /// NAS同期サービスを停止する
    /// 通常は CancellationToken 経由で停止するが、明示的に停止したい場合に使用
    /// </summary>
    public static async Task stop_async()
    {
        if (!_is_running)
            return;

        _is_running = false;

        // バックグラウンドタスクの完了を待機（最大3秒）
        if (_sync_task != null)
        {
            try
            {
                await Task.WhenAny(_sync_task, Task.Delay(3000));
            }
            catch { /* 停止中の例外は無視 */ }
        }

        write_log("NasSyncService停止");
    }

    /// <summary>
    /// 同期ループ本体
    /// 2分 + ランダム秒の間隔で同期処理を繰り返す
    /// </summary>
    private static async Task sync_loop_async(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 同期間隔だけ待機（キャンセル可能）
                int wait_ms = (SYNC_INTERVAL_MIN * 60 + _random_offset_sec) * 1000;
                await Task.Delay(wait_ms, ct);

                if (ct.IsCancellationRequested)
                    break;

                // ─── 同期処理を実行 ───
                // Sprint 1ではスタブ実装。Sprint 2以降で実装する
                try
                {
                    await sync_local_to_nas_async(ct);
                    await sync_nas_to_local_async(ct);
                }
                catch (OperationCanceledException)
                {
                    // キャンセル時は静かに終了
                    break;
                }
                catch (Exception ex)
                {
                    // 同期失敗してもループは継続する（次回リトライ）
                    write_log($"同期エラー（次回リトライ）: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // アプリ終了時のキャンセルは正常終了
            write_log("NasSyncService：アプリ終了によりキャンセル");
        }
        catch (Exception ex)
        {
            // 想定外の例外
            write_log($"NasSyncService：致命的エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// ▼ スタブ：ローカルDB → NAS DB 同期
    /// Sprint 2以降で本実装する：
    /// ・sync_status='pending'のレコードを取得
    /// ・NAS DBにINSERT OR REPLACE
    /// ・成功時はsync_status='synced'に更新
    /// ・失敗時はsync_status='error'に更新（次回リトライ）
    /// </summary>
    private static async Task sync_local_to_nas_async(CancellationToken ct)
    {
        // Sprint 1ではスタブ。実装は次Sprintで
        await Task.CompletedTask;
    }

    /// <summary>
    /// ▼ スタブ：NAS DB → ローカルDB 取込
    /// Sprint 2以降で本実装する：
    /// ・NAS DBからupdated_at > last_sync_atのレコードを取得
    /// ・自分のPCが入力したレコード（source_pc一致）は除外
    /// ・ローカルDBにINSERT OR REPLACE
    /// ・last_sync_atを更新
    /// </summary>
    private static async Task sync_nas_to_local_async(CancellationToken ct)
    {
        // Sprint 1ではスタブ。実装は次Sprintで
        await Task.CompletedTask;
    }

    /// <summary>
    /// 手動同期（同期ボタンから呼ばれる）
    /// 即時に同期を実行する
    /// </summary>
    public static async Task manual_sync_async()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await sync_local_to_nas_async(cts.Token);
            await sync_nas_to_local_async(cts.Token);
            write_log("手動同期完了");
        }
        catch (Exception ex)
        {
            write_log($"手動同期エラー: {ex.Message}");
            MessageBox.Show(
                $"同期に失敗しました。\n\n{ex.Message}",
                "同期エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// ログ出力（App.xaml.cs の write_log を呼ぶ）
    /// 内部用ヘルパー
    /// </summary>
    private static void write_log(string message)
    {
        try
        {
            // App.write_log() は internal なので同じアセンブリから呼べる
            App.write_log($"[NasSyncService] {message}");
        }
        catch { /* ログ失敗は無視 */ }
    }
}

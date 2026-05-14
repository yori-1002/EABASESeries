using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using EA_DailyReport.Data;

namespace EA_DailyReport.Services
{
    // ─── モデル ─────────────────────────────────────────────────────────────────

    /// <summary>エラーログ1件（DB error_logs テーブルのレコード）</summary>
    public class error_log_item
    {
        public int    id          { get; set; }
        public string mac_address { get; set; } = "";
        public string user_name   { get; set; } = "";
        /// <summary>"unhandled" = 未処理例外 / "caught" = 明示catchで記録</summary>
        public string error_type  { get; set; } = "";
        public string source      { get; set; } = "";
        public string message     { get; set; } = "";
        public string stack_trace { get; set; } = "";
        public string occurred_at { get; set; } = "";
    }

    /// <summary>ユーザー別エラー集計（UI表示用サマリー）</summary>
    public class error_user_summary
    {
        public string user_name        { get; set; } = "";
        public string mac_address      { get; set; } = "";
        public int    total_count      { get; set; }
        public int    unhandled_count  { get; set; }
        public string last_occurred_at { get; set; } = "";
    }

    // ─── サービス ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sprint 5D: エラーログのDB保存・取得・クリーンアップを一元管理するサービス
    /// ※ 記録先は database_manager.create_connection() が返すDB
    ///    NAS有効時はNAS DB、無効時はローカルDB に書き込まれる
    /// </summary>
    public static class ErrorLogService
    {
        // ── 記録系 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 明示catchで捕捉したエラーを記録する（error_type = "caught"）
        /// 各所のcatchブロックから呼び出すこと
        /// </summary>
        public static async Task log_caught_async(string source, Exception ex)
            => await write_async("caught", source, ex);

        /// <summary>
        /// 未処理例外を記録する（error_type = "unhandled"）
        /// 同期版：AppDomain/DispatcherUnhandledException ハンドラーから使用
        /// </summary>
        public static void log_unhandled(string source, Exception ex)
            => write_async("unhandled", source, ex).GetAwaiter().GetResult();

        // ── 内部書き込みメソッド ──────────────────────────────────────────────

        private static async Task write_async(string error_type, string source, Exception? ex)
        {
            try
            {
                // null の場合はデフォルトメッセージを使用
                var msg   = ex?.Message    ?? "（メッセージなし）";
                var stack = ex?.StackTrace ?? "";

                // DBサイズ節約のため長すぎる場合は切り詰め
                if (msg.Length   > 2000) msg   = msg[..2000]   + "…（以下省略）";
                if (stack.Length > 5000) stack = stack[..5000] + "…（以下省略）";

                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(@"
                    INSERT INTO error_logs
                        (mac_address, user_name, error_type, source, message, stack_trace, occurred_at)
                    VALUES
                        (@mac, @name, @etype, @src, @msg, @stack, datetime('now','localtime'))",
                    new
                    {
                        mac   = UserSession.mac_address ?? "unknown",
                        name  = UserSession.user_name   ?? "unknown",
                        etype = error_type,
                        src   = source,
                        msg,
                        stack
                    });
            }
            catch
            {
                // ※ ログ記録自体の失敗は無視する
                //    理由：再帰的なログループ防止 + 起動直前のDB未初期化対策
            }
        }

        // ── メンテナンス系 ──────────────────────────────────────────────────────

        /// <summary>
        /// 7日以上前のエラーログを削除する
        /// App.OnStartup で呼び出し（起動時1回実行）
        /// </summary>
        public static async Task cleanup_old_logs_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(@"
                    DELETE FROM error_logs
                    WHERE occurred_at < datetime('now', '-7 days', 'localtime')");
            }
            catch { /* クリーンアップ失敗は無視して起動を続行 */ }
        }

        // ── 参照系 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// ユーザー別サマリーを取得する（エラーが1件以上のユーザーのみ）
        /// ErrorLogPage のユーザーカード一覧に使用
        /// </summary>
        public static async Task<IEnumerable<error_user_summary>> get_user_summary_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                return await conn.QueryAsync<error_user_summary>(@"
                    SELECT
                        user_name,
                        mac_address,
                        COUNT(*)                                                  AS total_count,
                        SUM(CASE WHEN error_type = 'unhandled' THEN 1 ELSE 0 END) AS unhandled_count,
                        MAX(occurred_at)                                          AS last_occurred_at
                    FROM  error_logs
                    GROUP BY mac_address, user_name
                    ORDER BY last_occurred_at DESC");
            }
            catch
            {
                return Array.Empty<error_user_summary>();
            }
        }

        /// <summary>
        /// 指定ユーザーのエラーログ一覧を取得する（最大100件・新しい順）
        /// ErrorLogPage のDataGrid表示に使用
        /// </summary>
        public static async Task<IEnumerable<error_log_item>> get_logs_by_user_async(string mac_address)
        {
            try
            {
                using var conn = database_manager.create_connection();
                return await conn.QueryAsync<error_log_item>(@"
                    SELECT id, mac_address, user_name, error_type,
                           source, message, stack_trace, occurred_at
                    FROM   error_logs
                    WHERE  mac_address = @mac
                    ORDER  BY occurred_at DESC
                    LIMIT  100",
                    new { mac = mac_address });
            }
            catch
            {
                return Array.Empty<error_log_item>();
            }
        }
    }
}

using System.Data;
using System.Threading.Tasks;
using Dapper;

namespace EA_DailyReport.Data
{
    /// <summary>
    /// Sprint 5D: error_logsテーブルのマイグレーション
    /// ・テーブルが存在しない場合 → 新規作成
    /// ・テーブルが存在するがカラムが足りない場合 → DROP して再作成
    ///   （テスト中の古いテーブル構造を自動修復するため）
    /// </summary>
    public static class ErrorLogMigration
    {
        public static async Task RunAsync(IDbConnection conn)
        {
            // ── 既存テーブルのカラム構成を確認 ──────────────────────────────────
            // PRAGMA table_info はテーブルが存在しない場合は空を返す
            var columns = await conn.QueryAsync<string>(
                "SELECT name FROM pragma_table_info('error_logs')");

            var col_list = new System.Collections.Generic.HashSet<string>(columns);

            // テーブルが存在するがカラムが欠けている場合は DROP して再作成
            // ※ テスト段階でのみ発生する。本番運用後はデータ損失になるため注意
            if (col_list.Count > 0 &&
                (!col_list.Contains("mac_address") ||
                 !col_list.Contains("user_name") ||
                 !col_list.Contains("error_type") ||
                 !col_list.Contains("source") ||
                 !col_list.Contains("stack_trace")))
            {
                // 古い不完全なテーブルを削除
                await conn.ExecuteAsync("DROP TABLE IF EXISTS error_logs");
                await conn.ExecuteAsync("DROP INDEX IF EXISTS idx_error_logs_mac");
                await conn.ExecuteAsync("DROP INDEX IF EXISTS idx_error_logs_occurred");

                // col_listをクリアして以下の CREATE TABLE に進む
                col_list.Clear();
            }

            // ── error_logsテーブル作成（存在しない場合のみ） ─────────────────────
            if (col_list.Count == 0)
            {
                await conn.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS error_logs (
                        id          INTEGER PRIMARY KEY AUTOINCREMENT,
                        mac_address TEXT    NOT NULL DEFAULT '',
                        user_name   TEXT    NOT NULL DEFAULT '',
                        error_type  TEXT    NOT NULL DEFAULT 'caught',
                        source      TEXT    NOT NULL DEFAULT '',
                        message     TEXT    NOT NULL DEFAULT '',
                        stack_trace TEXT    NOT NULL DEFAULT '',
                        occurred_at TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
                    )");

                // ── インデックス作成 ──────────────────────────────────────────
                await conn.ExecuteAsync(
                    "CREATE INDEX IF NOT EXISTS idx_error_logs_mac ON error_logs(mac_address)");
                await conn.ExecuteAsync(
                    "CREATE INDEX IF NOT EXISTS idx_error_logs_occurred ON error_logs(occurred_at)");
            }
        }
    }
}
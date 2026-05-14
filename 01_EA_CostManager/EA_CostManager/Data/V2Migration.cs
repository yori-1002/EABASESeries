// ============================================================
// Data/V2Migration.cs  ★完全修正版（Sprint 5A：ユーザー管理追加）
// ============================================================
using Dapper;
using Microsoft.Data.Sqlite;

namespace EA_CostManager.Data;

public static class V2Migration
{
    public static async Task RunAsync(SqliteConnection connection)
    {
        // ─── 車両単価マスタ（新規追加） ───
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS vehicle_rates (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                vehicle_name TEXT NOT NULL,
                road_type    TEXT NOT NULL,
                unit_price   INTEGER NOT NULL DEFAULT 0,
                created_at   TEXT DEFAULT (datetime('now','localtime')),
                updated_at   TEXT DEFAULT (datetime('now','localtime')),
                UNIQUE(vehicle_name, road_type)
            )");

        await connection.ExecuteAsync(@"
            INSERT OR IGNORE INTO vehicle_rates (vehicle_name, road_type, unit_price)
            VALUES
                ('ステップワゴン', '高速', 100),
                ('ステップワゴン', '下道',  50),
                ('ハイエース',     '高速', 100),
                ('ハイエース',     '下道',  50)");

        // ─── cost_records に列追加（Sprint 3） ───
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_records ADD COLUMN distance_total REAL DEFAULT 0");
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_records ADD COLUMN vehicle_count INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_records ADD COLUMN equipment_quantity INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_records ADD COLUMN fiscal_month TEXT DEFAULT ''");
        // ▼▼▼ 追加：技師・助手の人数カラム（Sprint 4） ▼▼▼
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_records ADD COLUMN engineer_count INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_records ADD COLUMN assistant_count INTEGER DEFAULT 0");

        // ─── DBバージョン記録 ───
        await connection.ExecuteAsync(@"
            INSERT OR REPLACE INTO app_settings (key, value)
            VALUES ('db_version', '3')");

        // ─── projects テーブルにカラム追加 ───
        await TryAddColumnAsync(connection,
            "ALTER TABLE projects ADD COLUMN tab_color TEXT DEFAULT ''");
        await TryAddColumnAsync(connection,
            "ALTER TABLE projects ADD COLUMN attribute TEXT DEFAULT ''");

        // ▼▼▼ 追加：is_active カラム（アーカイブ機能。0=非表示 / 1=表示） ▼▼▼
        await TryAddColumnAsync(connection,
            "ALTER TABLE projects ADD COLUMN is_active INTEGER DEFAULT 1");

        // ▼▼▼ 追加：sort_order カラム（ドラッグ並び替え用。0=自動並び順 / 1以上=手動並び順） ▼▼▼
        await TryAddColumnAsync(connection,
            "ALTER TABLE projects ADD COLUMN sort_order INTEGER DEFAULT 0");

        // ─── 絞り込みタブ管理テーブル（Sprint 3追加） ───
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS cost_filter_tabs (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id     INTEGER NOT NULL,
                tab_name       TEXT NOT NULL,
                filter_month   TEXT DEFAULT '',
                filter_content TEXT DEFAULT '',
                filter_match   TEXT DEFAULT 'partial',
                filter_names   TEXT DEFAULT '',
                is_single_mode INTEGER DEFAULT 0,
                created_at     TEXT DEFAULT (datetime('now','localtime'))
            )");

        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_filter_tabs ADD COLUMN is_single_mode INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_filter_tabs ADD COLUMN is_archived INTEGER DEFAULT 0");

        // ▼▼▼ 追加：project_rates テーブル（現場別単価設定 Sprint 3.7） ▼▼▼
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS project_rates (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id    INTEGER NOT NULL,
                rate_type     TEXT NOT NULL,
                employee_name TEXT DEFAULT NULL,
                daily_rate    REAL NOT NULL DEFAULT 0,
                created_at    TEXT DEFAULT (datetime('now','localtime')),
                updated_at    TEXT DEFAULT (datetime('now','localtime'))
            )");

        // ▼▼▼ 追加：区分コード正規化（Sprint 3.7） ▼▼▼
        await normalize_category_codes_async(connection);

        // ▼▼▼ 追加：日付範囲フィルター用カラム（Sprint 3.6） ▼▼▼
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_filter_tabs ADD COLUMN filter_date_from TEXT DEFAULT ''");
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_filter_tabs ADD COLUMN filter_date_to TEXT DEFAULT ''");

        // ▼▼▼ 追加：現場別単価適用フラグ（Sprint 3.7） ▼▼▼
        await TryAddColumnAsync(connection,
            "ALTER TABLE cost_filter_tabs ADD COLUMN use_custom_rates INTEGER DEFAULT 0");

        // ▼▼▼ 追加：既存の「単価変更版」タブで use_custom_rates=0 のものを修正（Sprint 3.7） ▼▼▼
        await connection.ExecuteAsync(@"
            UPDATE cost_filter_tabs
            SET use_custom_rates = 1
            WHERE use_custom_rates = 0
              AND EXISTS (
                  SELECT 1 FROM project_rates pr
                  WHERE pr.project_id = cost_filter_tabs.project_id
              )
              AND tab_name LIKE '%単価変更%'");

        // ▼▼▼ 追加：絞り込みタブ別単価スナップショット（Sprint 3.7+） ▼▼▼
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS filter_tab_rates (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                filter_tab_id   INTEGER NOT NULL,
                rate_type       TEXT NOT NULL,
                employee_name   TEXT DEFAULT NULL,
                daily_rate      REAL NOT NULL DEFAULT 0,
                created_at      TEXT DEFAULT (datetime('now','localtime'))
            )");

        // ─── equipment_rates テーブル ───
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS equipment_rates (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                equipment_name TEXT NOT NULL UNIQUE,
                daily_rate     REAL NOT NULL DEFAULT 0,
                is_active      INTEGER DEFAULT 1,
                note           TEXT DEFAULT '',
                created_at     TEXT DEFAULT (datetime('now','localtime'))
            )");

        await connection.ExecuteAsync(@"
            INSERT OR IGNORE INTO equipment_rates (equipment_name, daily_rate, note) VALUES
                ('武蔵',        480,  ''),
                ('Trend-Point', 600,  ''),
                ('Trend-CORE',  300,  ''),
                ('AUTODESK',    1750, ''),
                ('SCENE',       550,  ''),
                ('Metashape',   340,  ''),
                ('Inspire2',    240,  ''),
                ('ANA01',       2050, ''),
                ('ボート',      4920, ''),
                ('IMU',         6030, ''),
                ('FARO',        9340, ''),
                ('TerraceAR',   0,    '未設定'),
                ('mixpace',     0,    '未設定')");

        // ▼▼▼ 追加：legacy_name_map テーブル ▼▼▼
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS legacy_name_map (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                legacy_name  TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                created_at   TEXT DEFAULT (datetime('now','localtime'))
            )");

        await connection.ExecuteAsync(@"
            INSERT OR IGNORE INTO legacy_name_map (legacy_name, display_name) VALUES
                ('NGUYEN VAN PHUC',   'フック'),
                ('NGUYEN VAN THAI',   'タイ'),
                ('NGUYEN VAN DUY',    'ズイ'),
                ('NGUYEN BA TAI',     'バタイ')");

        // ▼▼▼ Sprint 5A：pc_users テーブルに列追加 ▼▼▼
        // 既存の pc_users（mac_address, user_name）に
        // employee_id・pc_name・is_admin を追加する
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS pc_users (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                mac_address  TEXT NOT NULL UNIQUE,
                pc_name      TEXT DEFAULT '',
                employee_id  INTEGER DEFAULT 0,
                user_name    TEXT NOT NULL,
                is_admin     INTEGER DEFAULT 0,
                created_at   TEXT DEFAULT (datetime('now','localtime')),
                updated_at   TEXT DEFAULT (datetime('now','localtime'))
            )");

        // 既存テーブルへのカラム追加（既に存在する場合は無視）
        await TryAddColumnAsync(connection,
            "ALTER TABLE pc_users ADD COLUMN pc_name TEXT DEFAULT ''");
        await TryAddColumnAsync(connection,
            "ALTER TABLE pc_users ADD COLUMN employee_id INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection,
            "ALTER TABLE pc_users ADD COLUMN is_admin INTEGER DEFAULT 0");

        // ▼▼▼ Sprint 5A：operation_logs テーブル確認（既存だが定義を保証） ▼▼▼
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS operation_logs (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                log_datetime   TEXT DEFAULT (datetime('now','localtime')),
                pc_user_id     INTEGER DEFAULT 0,
                operator_name  TEXT DEFAULT '',
                operation_type TEXT DEFAULT '',
                target_table   TEXT DEFAULT '',
                target_id      INTEGER DEFAULT 0,
                detail         TEXT DEFAULT '',
                record_count   INTEGER DEFAULT 0,
                file_path      TEXT DEFAULT '',
                import_batch   TEXT DEFAULT ''
            )");

        // ▼▼▼ 追加：既存 operation_logs への pc_user_id・import_batch カラム追加 ▼▼▼
        await TryAddColumnAsync(connection,
            "ALTER TABLE operation_logs ADD COLUMN pc_user_id INTEGER DEFAULT 0");
        await TryAddColumnAsync(connection,
            "ALTER TABLE operation_logs ADD COLUMN import_batch TEXT DEFAULT ''");

        // ▼▼▼ Sprint 5A：user_sessions テーブル確認 ▼▼▼
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS user_sessions (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                mac_address TEXT NOT NULL,
                user_name   TEXT NOT NULL,
                software    TEXT DEFAULT 'CostManager',
                status      TEXT DEFAULT 'active',
                started_at  TEXT DEFAULT (datetime('now','localtime')),
                updated_at  TEXT DEFAULT (datetime('now','localtime'))
            )");

        // ▼▼▼ [現場タブ追跡] user_sessions に current_tab カラムを追加 ▼▼▼
        // タブ切替時に「どの現場を見ているか」をDBに記録し、
        // 他ユーザーがホバー時に確認できるようにする
        await TryAddColumnAsync(connection,
            "ALTER TABLE user_sessions ADD COLUMN current_tab TEXT DEFAULT ''");
    }

    // ---- 区分コード正規化（既存DBデータを一括更新） ----
    private static async Task normalize_category_codes_async(SqliteConnection connection)
    {
        try
        {
            var dr_codes = (await connection.QueryAsync<(long id, string code)>(
                "SELECT rowid, category_code FROM daily_reports WHERE category_code IS NOT NULL AND category_code != ''"))
                .ToList();

            foreach (var (id, code) in dr_codes)
            {
                string normalized = EA_CostManager.Services.ExcelImportService.normalize_category_code(code);
                if (normalized != code)
                    await connection.ExecuteAsync(
                        "UPDATE daily_reports SET category_code = @n WHERE rowid = @id",
                        new { n = normalized, id });
            }

            var proj_codes = (await connection.QueryAsync<(int id, string code)>(
                "SELECT id, category_code FROM projects WHERE category_code IS NOT NULL AND category_code != ''"))
                .ToList();

            foreach (var (id, code) in proj_codes)
            {
                string normalized = EA_CostManager.Services.ExcelImportService.normalize_category_code(code);
                if (normalized != code)
                    await connection.ExecuteAsync(
                        "UPDATE projects SET category_code = @n WHERE id = @id",
                        new { n = normalized, id });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"区分コード正規化エラー: {ex.Message}");
        }
    }

    public static async Task TryAddColumnAsync(SqliteConnection connection, string sql)
    {
        try
        {
            await connection.ExecuteAsync(sql);
        }
        catch (SqliteException ex) when (
            ex.Message.Contains("duplicate column name"))
        {
            // 既に存在する場合は無視（冪等性確保）
        }
    }
}
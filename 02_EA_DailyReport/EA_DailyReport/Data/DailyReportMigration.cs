// ============================================================
// Data/DailyReportMigration.cs
// EA_DailyReport専用のDBマイグレーション
// ローカルDB用(run_local_async)とNAS DB用(run_nas_async)を分離
//
// ▼ v0.1.4 で追加：旧パスマイグレーション（migrate_old_db_path）
//   旧 exe隣\local_db → 新 %LOCALAPPDATA%\01_EABASE Series\02_DailyReport\local_db
// ============================================================
using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace EA_DailyReport.Data;

public static class DailyReportMigration
{
    // ────────────────────────────────────────────────────────────
    // ▼ 追加：v0.1.4 設計書1.3④・10.2
    // 旧パスマイグレーション（DBファイル）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// ▼ 追加：v0.1.4 設計書1.3④・10.2
    /// 旧パス（exe隣\local_db）から新パス（%LOCALAPPDATA%\...\local_db）への
    /// ローカルDBファイルマイグレーション
    /// 起動時に1回だけ実行する。新パスに既にあれば何もしない。
    /// 旧ファイルは削除しない（権限不足の例外回避）
    ///
    /// App.xaml.cs の起動フロー④ から呼び出すこと
    /// （③ DB初期化の前に実行する必要がある）
    ///
    /// 注：File.Copy は同期処理だが、起動時1回のみ・ファイル数最大3個
    ///     （DB本体・-wal・-shm）のためブロッキングは許容範囲
    /// </summary>
    public static void migrate_old_db_path()
    {
        try
        {
            // 新パスのDBファイル
            string new_db_path = database_manager.get_local_db_path();

            // 新パスに既に存在する場合は何もしない（マイグレーション完了済み）
            if (File.Exists(new_db_path))
                return;

            // 旧パス：exe隣の local_db フォルダ
            string old_dir = database_manager.OLD_DEFAULT_DIRECTORY;
            string old_db_path = Path.Combine(old_dir, database_manager.DEFAULT_DB_NAME);

            // 旧パスにファイル無し → マイグレーション不要（新規インストール扱い）
            if (!File.Exists(old_db_path))
                return;

            // 新パスのフォルダを作成
            string? new_dir = Path.GetDirectoryName(new_db_path);
            if (!string.IsNullOrEmpty(new_dir) && !Directory.Exists(new_dir))
                Directory.CreateDirectory(new_dir);

            // ▼ DBファイル本体をコピー（旧ファイルは削除しない）
            // overwrite: false で新パスに既存ファイルがある場合は上書きしない安全側に倒す
            // 上の File.Exists チェックで弾いているため通常は新規コピーになる
            File.Copy(old_db_path, new_db_path, overwrite: false);

            // ▼ WALファイル・SHMファイルもあればコピー（同期途中の状態保持）
            // 旧パスにWAL/SHMが残っていてDBファイル単独でコピーすると
            // 未コミット分のデータが失われる可能性があるため
            string old_wal = old_db_path + "-wal";
            string new_wal = new_db_path + "-wal";
            if (File.Exists(old_wal) && !File.Exists(new_wal))
                File.Copy(old_wal, new_wal, overwrite: false);

            string old_shm = old_db_path + "-shm";
            string new_shm = new_db_path + "-shm";
            if (File.Exists(old_shm) && !File.Exists(new_shm))
                File.Copy(old_shm, new_shm, overwrite: false);
        }
        catch (Exception ex)
        {
            // マイグレーション失敗はアプリ動作を妨げない（新規DBが作成される）
            // 旧DBが残っていれば次回起動時に再試行可能
            System.Diagnostics.Debug.WriteLine(
                $"旧パスマイグレーション失敗（新規DB作成にフォールバック）: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────────
    // 共通Migration（ローカルDB・NAS DB両方に適用）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// ▼ 追加：App.xaml.cs から呼ばれる共通エントリポイント
    /// ローカルDB用 → run_local_async() を推奨だが、
    /// 互換性のため run_async() も残しておく（ローカルDB用）
    /// </summary>
    public static async Task run_async(SqliteConnection connection)
    {
        // 互換性のためローカルDB用として扱う
        await run_local_async(connection);
    }

    // ────────────────────────────────────────────────────────────
    // ローカルDB専用Migration
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// ▼ 追加：ローカルDB（ea_daily_local.db）に対するMigration
    /// ・書き込み専用DB。各PCに配置
    /// ・sync_status テーブルを持つ（同期管理）
    /// ・daily_reports / work_details / employee_work_settings / ai_assist_patterns も持つ
    ///   （書き込み用のキャッシュ先として）
    /// ・app_settings もローカルに持つ（NAS接続設定等）
    /// </summary>
    public static async Task run_local_async(SqliteConnection connection)
    {
        // ─── 共通テーブルを作成 ───
        await create_common_tables_async(connection);

        // ─── ローカルDB専用：sync_status テーブル ───
        // ローカル→NAS同期の管理テーブル。NAS DBには存在しない
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS sync_status (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                table_name  TEXT NOT NULL,
                record_id   INTEGER NOT NULL,
                status      TEXT DEFAULT 'pending',
                created_at  TEXT DEFAULT (datetime('now','localtime')),
                synced_at   TEXT DEFAULT NULL,
                error_msg   TEXT DEFAULT '',
                UNIQUE(table_name, record_id)
            )");

        // sync_status検索用インデックス（status検索が頻繁に発生するため）
        // ▼ v0.1.4 修正：try_create_index_async 経由で安全化
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_sync_status_status
            ON sync_status(status)");

        // ─── ローカルDB専用：app_settings テーブル ───
        // NAS接続設定・最終同期日時等を保存
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS app_settings (
                key         TEXT PRIMARY KEY,
                value       TEXT DEFAULT '',
                updated_at  TEXT DEFAULT (datetime('now','localtime'))
            )");

        // 初期値投入（最終同期日時）
        await connection.ExecuteAsync(@"
            INSERT OR IGNORE INTO app_settings (key, value)
            VALUES
                ('last_sync_at', ''),
                ('nas_enabled',  '0'),
                ('nas_path',     ''),
                ('db_version',   '1')");
    }

    // ────────────────────────────────────────────────────────────
    // NAS DB専用Migration
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// ▼ 追加：NAS DB（ea_core.db）に対するMigration
    /// ・マスターDB。全PCで共有
    /// ・CostManager の既存テーブル（projects / cost_records / employees 等）は変更しない
    /// ・daily_reports 既存テーブルに日報ソフト用カラムを追加
    /// ・work_details / employee_work_settings / notification_queue / ai_assist_patterns を新設
    /// </summary>
    public static async Task run_nas_async(SqliteConnection connection)
    {
        // ─── 共通テーブルを作成 ───
        await create_common_tables_async(connection);

        // ─── NAS DB専用：notification_queue テーブル ───
        // 管理者からのプッシュ通知キュー
        // 通知プログラム（EA_DailyNotifier）が2分ごとに確認して未読通知をトースト表示
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS notification_queue (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                target_user_id  INTEGER DEFAULT NULL,
                sender_id       INTEGER NOT NULL,
                message         TEXT DEFAULT '',
                is_read         INTEGER DEFAULT 0,
                created_at      TEXT DEFAULT (datetime('now','localtime')),
                read_at         TEXT DEFAULT NULL
            )");

        // notification_queue検索用インデックス（target_user_id + is_readで検索するため）
        // ▼ v0.1.4 修正：try_create_index_async 経由で安全化
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_notification_queue_target
            ON notification_queue(target_user_id, is_read)");
    }

    // ────────────────────────────────────────────────────────────
    // 共通テーブル（ローカルDB・NAS DB両方に作成）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// ▼ 追加：共通テーブルを作成
    /// daily_reports（拡張）/ work_details / employee_work_settings / ai_assist_patterns
    /// </summary>
    private static async Task create_common_tables_async(SqliteConnection connection)
    {
        // ─── daily_reports テーブル（CostManager既存を拡張） ───
        // CostManager の既存テーブルがある場合はCREATE TABLE IF NOT EXISTSで作成されない
        // 後続のALTER TABLEで新カラムを追加する
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS daily_reports (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                report_date     TEXT NOT NULL,
                employee_id     INTEGER DEFAULT 0,
                employee_name   TEXT DEFAULT '',
                created_at      TEXT DEFAULT (datetime('now','localtime')),
                updated_at      TEXT DEFAULT (datetime('now','localtime'))
            )");

        // 日報ソフト用の新カラムを追加（既存DBとの互換性確保）
        // ▼ 追加：v0.1.4 修正
        // CostManager 由来の既存 daily_reports テーブルに employee_id /
        // employee_name カラムが無いケースに対応するため明示的に ALTER
        // （CREATE TABLE IF NOT EXISTS は既存テーブルに対して何もしないため、
        //  CREATE 文の DEFAULT 指定だけでは既存DBに追加されない）
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN employee_id INTEGER DEFAULT 0");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN employee_name TEXT DEFAULT ''");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN clock_in TEXT DEFAULT ''");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN clock_out TEXT DEFAULT ''");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN early_morning_min INTEGER DEFAULT 0");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN overtime_min INTEGER DEFAULT 0");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN late_night_min INTEGER DEFAULT 0");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN start_location TEXT DEFAULT ''");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN end_location TEXT DEFAULT ''");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN entered_by INTEGER DEFAULT 0");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN source_pc TEXT DEFAULT ''");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN source TEXT DEFAULT 'desktop'");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN gdrive_file_id TEXT DEFAULT ''");
        await try_add_column_async(connection,
            "ALTER TABLE daily_reports ADD COLUMN updated_at TEXT DEFAULT (datetime('now','localtime'))");

        // 検索用インデックス
        // 月度フィルター・氏名検索が頻繁に発生するため
        // ▼ v0.1.4 修正：try_create_index_async 経由で安全化
        // 既存DBにカラムが無い場合でも例外を吸収して動作継続する
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_daily_reports_date
            ON daily_reports(report_date)");
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_daily_reports_employee
            ON daily_reports(employee_id)");
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_daily_reports_source_pc
            ON daily_reports(source_pc)");
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_daily_reports_updated_at
            ON daily_reports(updated_at)");

        // ─── work_details テーブル（新設） ───
        // 1日報に対する複数の作業行を格納
        // is_deleted（論理削除）・is_recalc_needed（再集計フラグ）でCostManagerと連携
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS work_details (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                daily_report_id   INTEGER NOT NULL,
                category_code     TEXT DEFAULT '',
                project_name      TEXT DEFAULT '',
                detail            TEXT DEFAULT '',
                hours             REAL DEFAULT 0,
                software          TEXT DEFAULT '',
                software_cost     INTEGER DEFAULT 0,
                vehicle           TEXT DEFAULT '',
                from_location     TEXT DEFAULT '',
                to_location       TEXT DEFAULT '',
                distance          REAL DEFAULT 0,
                transport         TEXT DEFAULT '',
                note              TEXT DEFAULT '',
                sort_order        INTEGER DEFAULT 0,
                is_deleted        INTEGER DEFAULT 0,
                is_recalc_needed  INTEGER DEFAULT 0,
                deleted_at        TEXT DEFAULT NULL,
                created_at        TEXT DEFAULT (datetime('now','localtime')),
                updated_at        TEXT DEFAULT (datetime('now','localtime'))
            )");

        // 検索用インデックス
        // ▼ v0.1.4 修正：try_create_index_async 経由で安全化
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_work_details_daily_report
            ON work_details(daily_report_id)");
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_work_details_category
            ON work_details(category_code)");
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_work_details_recalc
            ON work_details(is_recalc_needed)");

        // ▼ 追加：v0.1.5 NAS（CostManager）連携対応
        // work_details に nas_source_id カラムを追加
        // CostManager の daily_reports.id を保持し、「最新を取得」時に
        // 同一レコード判定 + updated_at 比較による差分同期を可能にする
        //
        // 既存テーブルに対しては ALTER TABLE で追加（CREATE TABLE は IF NOT EXISTS のため
        // 既存テーブルには影響しない）
        await try_add_column_async(connection,
            "ALTER TABLE work_details ADD COLUMN nas_source_id INTEGER DEFAULT 0");

        // nas_source_id でレコードを高速検索するためのインデックス
        // UNIQUE 制約は付けない理由：nas_source_id=0（ローカル新規）が複数許容のため
        // 一意制約は NasDataFetchService 側でアプリ層判定にする
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_work_details_nas_source
            ON work_details(nas_source_id)");

        // ─── employee_work_settings テーブル（新設） ───
        // ユーザー個別の定時・勤務区分設定
        // 日本(8:30-17:30) / ベトナム(7:30-16:30) / カスタム
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS employee_work_settings (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                employee_id  INTEGER NOT NULL UNIQUE,
                work_style   TEXT DEFAULT 'japan',
                work_start   TEXT DEFAULT '08:30',
                work_end     TEXT DEFAULT '17:30',
                break_min    INTEGER DEFAULT 60,
                created_at   TEXT DEFAULT (datetime('now','localtime')),
                updated_at   TEXT DEFAULT (datetime('now','localtime'))
            )");

        // ─── ai_assist_patterns テーブル（新設） ───
        // AIアシスト用パターン学習データ
        // 人の紐づけなし・業務内容ベース
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS ai_assist_patterns (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                keyword         TEXT NOT NULL,
                category_code   TEXT NOT NULL,
                count           INTEGER DEFAULT 1,
                correct_count   INTEGER DEFAULT 0,
                created_at      TEXT DEFAULT (datetime('now','localtime')),
                updated_at      TEXT DEFAULT (datetime('now','localtime')),
                UNIQUE(keyword, category_code)
            )");

        // スコアリング用インデックス（keyword部分一致検索が頻繁）
        // ▼ v0.1.4 修正：try_create_index_async 経由で安全化
        await try_create_index_async(connection, @"
            CREATE INDEX IF NOT EXISTS idx_ai_patterns_keyword
            ON ai_assist_patterns(keyword)");
    }

    // ────────────────────────────────────────────────────────────
    // ユーティリティ
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// カラム追加（既に存在する場合は無視して冪等性を確保する）
    /// SQLiteは ALTER TABLE ADD COLUMN IF NOT EXISTS に対応していないため
    /// 例外をキャッチして処理する方式にしている
    /// </summary>
    private static async Task try_add_column_async(SqliteConnection connection, string sql)
    {
        try
        {
            await connection.ExecuteAsync(sql);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name"))
        {
            // 既に存在する場合は無視（冪等性確保）
        }
    }

    /// <summary>
    /// ▼ 追加：v0.1.4 修正
    /// CREATE INDEX のラッパー。対象カラムが存在しない場合は警告ログだけ出して無視する
    ///
    /// 用途：CostManager 由来の既存DBに対してインデックスを追加する際、
    /// テーブルのスキーマが想定と異なる（カラム名違い等）ケースを吸収する
    ///
    /// インデックスは性能最適化のため、作成失敗してもアプリ動作には影響しない
    /// </summary>
    private static async Task try_create_index_async(SqliteConnection connection, string sql)
    {
        try
        {
            await connection.ExecuteAsync(sql);
        }
        catch (SqliteException ex)
        {
            // 「no such column」「no such table」を想定して吸収
            // それ以外のエラー（構文エラー等）も同様に吸収（性能最適化のため）
            System.Diagnostics.Debug.WriteLine(
                $"[DailyReportMigration] インデックス作成スキップ: {ex.Message}");
        }
    }
}
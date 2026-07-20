using System.IO;
using Microsoft.Data.Sqlite;
using Dapper;

namespace EA_CostManager.Data
{
    /// <summary>
    /// SQLite データベースの初期化と接続管理
    /// WALモード + busy_timeout + 自動バックアップ対応
    /// NAS切り替え対応（設定画面からパスを変更可能）
    /// ▼▼▼ 修正：nas_path のデフォルト値にファイル名 ea_core.db を追加
    ///            nas_enabled のデフォルトを "1"（NAS使用）に設定済み
    /// </summary>
    public class database_manager
    {
        // ローカルDBのデフォルト保存先（Documents\EA_DataCore\db）
        private static readonly string _default_directory =
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments), "EA_DataCore", "db");

        private static readonly string _default_db_name = "ea_core.db";

        private static string _current_db_path = string.Empty;

        /// <summary>
        /// 現在のDBファイルパスを取得
        /// </summary>
        public static string get_db_path()
        {
            if (string.IsNullOrEmpty(_current_db_path))
            {
                _current_db_path = Path.Combine(_default_directory, _default_db_name);
            }
            return _current_db_path;
        }

        /// <summary>
        /// DBパスを変更（NAS切替時に使用）
        /// </summary>
        public static bool set_db_path(string new_path)
        {
            if (string.IsNullOrWhiteSpace(new_path))
                return false;

            // NASパスの場合はディレクトリの存在確認
            string? dir = Path.GetDirectoryName(new_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch { return false; }
            }

            _current_db_path = new_path;
            return true;
        }

        /// <summary>
        /// ローカルDBパスを取得
        /// </summary>
        public static string get_local_db_path()
        {
            return Path.Combine(_default_directory, _default_db_name);
        }

        /// <summary>
        /// 接続文字列を取得
        /// </summary>
        public static string get_connection_string()
        {
            return $"Data Source={get_db_path()}";
        }

        /// <summary>
        /// ▼ 追加 [Sprint 8 / Phase 0]：読取専用モードで書込を試みたときに投げる例外。
        /// つなぎの安全策（NAS 書込ロック）中に、業務データの保存が行われないようにする。
        /// </summary>
        public class ReadOnlyModeException : System.InvalidOperationException
        {
            public ReadOnlyModeException(string message) : base(message) { }
        }

        /// <summary>
        /// ▼ 追加 [Sprint 8 / Phase 0]：共通書込ガード。
        /// 業務データを保存する処理の入口で最初に呼ぶ。読取専用モードなら例外を投げて保存を止める。
        ///
        /// ⚠️ 注意：これは接続を読取専用にするものではない（セッションのハートビート等、
        ///    アプリが正当に書き込む処理まで壊さないため）。あくまで「業務保存の入口で止める」ガード。
        ///    取りこぼした保存経路が残りうる点は、Phase 1 の書込層集約で airtight にする。
        ///    ローカル保存方式（Phase 2）完成後は本ガードごと撤去する。
        /// </summary>
        public static void ensure_writable()
        {
            if (UserSession.is_read_only)
            {
                string reason = string.IsNullOrWhiteSpace(UserSession.read_only_reason)
                    ? "他の利用者が編集中のため" : UserSession.read_only_reason;
                throw new ReadOnlyModeException(
                    $"{reason}、現在は閲覧のみのモードで開いております。保存はできません。");
            }
        }

        /// <summary>
        /// 新しいSQLite接続を生成（WALモード + busy_timeout）
        /// </summary>
        public static SqliteConnection create_connection()
        {
            var connection = new SqliteConnection(get_connection_string());
            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                // WALモード有効化
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                cmd.ExecuteNonQuery();

                // ★v0.9.7修正：busy_timeout を30秒→5秒に短縮
                // NAS上のSQLiteでは他PCがロック中に30秒待機するとUIが固まる。
                // 日報インポート等の長時間トランザクションは独自接続（ExcelImportService内）で
                // 個別にタイムアウトを設定するため、通常接続は5秒で十分。
                cmd.CommandText = "PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();

                // 外部キー制約を有効化
                cmd.CommandText = "PRAGMA foreign_keys=ON;";
                cmd.ExecuteNonQuery();
            }

            return connection;
        }

        /// <summary>
        /// データベースとテーブルを初期化
        /// </summary>
        public void initialize_database()
        {
            // ディレクトリ作成
            string db_dir = Path.GetDirectoryName(get_db_path()) ?? _default_directory;
            if (!Directory.Exists(db_dir))
            {
                Directory.CreateDirectory(db_dir);
            }

            using var connection = create_connection();

            // === マスタ系 ===
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS employees (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    employee_name   TEXT NOT NULL,
                    job_type        TEXT NOT NULL,
                    daily_rate      REAL DEFAULT 0,
                    is_active       INTEGER DEFAULT 1,
                    created_at      TEXT DEFAULT (datetime('now','localtime')),
                    updated_at      TEXT DEFAULT (datetime('now','localtime')),
                    updated_by      TEXT DEFAULT ''
                )");

            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS projects (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    category_code   TEXT NOT NULL,
                    site_name       TEXT,
                    company_name    TEXT,
                    detail          TEXT,
                    attribute       TEXT DEFAULT '',
                    tab_color       TEXT DEFAULT '',
                    is_active       INTEGER DEFAULT 1,
                    created_at      TEXT DEFAULT (datetime('now','localtime')),
                    updated_at      TEXT DEFAULT (datetime('now','localtime')),
                    updated_by      TEXT DEFAULT ''
                )");

            // 車両マスタテーブル
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS vehicles (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    vehicle_name    TEXT NOT NULL UNIQUE,
                    is_active       INTEGER DEFAULT 1,
                    created_at      TEXT DEFAULT (datetime('now','localtime'))
                )");

            // === 日報系 ===
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS daily_reports (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    report_date     TEXT NOT NULL,
                    employee_name   TEXT NOT NULL,
                    job_type        TEXT,
                    category_code   TEXT,
                    project_name    TEXT,
                    detail          TEXT,
                    hours           REAL DEFAULT 0,
                    clock_in        TEXT,
                    clock_out       TEXT,
                    overtime        TEXT,
                    start_location  TEXT,
                    end_location    TEXT,
                    import_batch    TEXT,
                    created_at      TEXT DEFAULT (datetime('now','localtime')),
                    updated_at      TEXT DEFAULT (datetime('now','localtime')),
                    updated_by      TEXT DEFAULT ''
                )");

            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS daily_transport (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    daily_report_id INTEGER NOT NULL,
                    vehicle         TEXT,
                    departure       TEXT,
                    arrival         TEXT,
                    distance        REAL DEFAULT 0,
                    travel_method   TEXT,
                    transport_cost  REAL DEFAULT 0,
                    FOREIGN KEY (daily_report_id) REFERENCES daily_reports(id) ON DELETE CASCADE
                )");

            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS daily_equipment (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    daily_report_id INTEGER NOT NULL,
                    equipment_name  TEXT,
                    daily_rate      REAL DEFAULT 0,
                    quantity        INTEGER DEFAULT 1,
                    FOREIGN KEY (daily_report_id) REFERENCES daily_reports(id) ON DELETE CASCADE
                )");

            // === 原価集計系 ===
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS cost_records (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    category_code       TEXT NOT NULL,
                    record_date         TEXT NOT NULL,
                    fiscal_month        TEXT DEFAULT '',
                    work_content        TEXT,
                    engineer_names      TEXT,
                    engineer_hours      REAL DEFAULT 0,
                    engineer_days       REAL DEFAULT 0,
                    engineer_cost       REAL DEFAULT 0,
                    assistant_names     TEXT,
                    assistant_hours     REAL DEFAULT 0,
                    assistant_days      REAL DEFAULT 0,
                    assistant_cost      REAL DEFAULT 0,
                    equipment_names     TEXT DEFAULT '',
                    equipment_detail    TEXT DEFAULT '',
                    equipment_quantity  INTEGER DEFAULT 0,
                    equipment_cost      REAL DEFAULT 0,
                    transport_cost      REAL DEFAULT 0,
                    distance_total      REAL DEFAULT 0,
                    vehicle_count       INTEGER DEFAULT 0,
                    personnel_cost      REAL DEFAULT 0,
                    total_cost          REAL DEFAULT 0,
                    created_at          TEXT DEFAULT (datetime('now','localtime')),
                    updated_at          TEXT DEFAULT (datetime('now','localtime')),
                    updated_by          TEXT DEFAULT ''
                )");

            // === 絞り込みタブ保存 ===
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS cost_filter_tabs (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id      INTEGER NOT NULL,
                    tab_name        TEXT NOT NULL,
                    filter_month    TEXT DEFAULT '',
                    filter_content  TEXT DEFAULT '',
                    filter_match    TEXT DEFAULT 'partial',
                    filter_names    TEXT DEFAULT '',
                    is_single_mode  INTEGER DEFAULT 0,
                    created_at      TEXT DEFAULT (datetime('now','localtime')),
                    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
                )");

            // === 設定系 ===
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS app_settings (
                    key         TEXT PRIMARY KEY,
                    value       TEXT,
                    updated_at  TEXT DEFAULT (datetime('now','localtime')),
                    updated_by  TEXT DEFAULT ''
                )");

            // === ログ系 ===
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS operation_logs (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    log_datetime    TEXT DEFAULT (datetime('now','localtime')),
                    operator_name   TEXT,
                    operation_type  TEXT,
                    target_table    TEXT,
                    target_id       INTEGER,
                    detail          TEXT,
                    record_count    INTEGER,
                    file_path       TEXT
                )");

            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS error_logs (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    log_datetime    TEXT DEFAULT (datetime('now','localtime')),
                    operator_name   TEXT,
                    error_type      TEXT,
                    error_message   TEXT,
                    stack_trace     TEXT
                )");

            // ユーザー管理テーブル
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS pc_users (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    mac_address TEXT NOT NULL UNIQUE,
                    user_name   TEXT NOT NULL,
                    created_at  TEXT DEFAULT (datetime('now','localtime')),
                    updated_at  TEXT DEFAULT (datetime('now','localtime'))
                )");

            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS user_sessions (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    mac_address TEXT NOT NULL,
                    user_name   TEXT NOT NULL,
                    software    TEXT DEFAULT 'EA_CostManager',
                    status      TEXT DEFAULT 'active',
                    started_at  TEXT DEFAULT (datetime('now','localtime')),
                    updated_at  TEXT DEFAULT (datetime('now','localtime'))
                )");

            // === デフォルト設定値 ===
            insert_default_setting(connection, "engineer_rate", "34800");
            insert_default_setting(connection, "assistant_rate", "28000");
            insert_default_setting(connection, "flight_op_rate", "26000");
            insert_default_setting(connection, "profit_rate", "0.8");
            insert_default_setting(connection, "base_hours_per_day", "8");
            insert_default_setting(connection, "road_cost_per_km", "50");
            insert_default_setting(connection, "highway_cost_per_km", "100");
            insert_default_setting(connection, "db_path", "");

            // ▼▼▼ 修正：NASデフォルト接続設定（初回のみ設定・ユーザー変更を尊重）
            // INSERT OR IGNORE = レコードが既に存在する場合は何もしない
            // INSERT OR REPLACE（旧実装）は起動のたびにテストDBへの変更を上書きしてしまうバグの原因だった
            // ※ 初回起動時（レコードなし）のみデフォルト値が挿入される
            insert_default_setting(connection, "nas_enabled", "1");
            insert_default_setting(connection, "nas_path",
                @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\00_Database\ea_core.db");

            // 初期車両データ
            connection.Execute(@"
                INSERT OR IGNORE INTO vehicles (vehicle_name, is_active)
                VALUES ('ステップワゴン', 1), ('ハイエース', 1)");

            // 既存DBへのマイグレーション（新規カラム追加）
            migrate_cost_records(connection);

            // ▼ 追加 [v1.0.1] DBインデックスの作成・更新
            //   業務テーブル（daily_reports / daily_equipment / daily_transport /
            //   cost_records / operation_logs）への検索インデックスを追加する。
            //   これらのテーブルは件数が増えるとフルスキャン（全件走査）が
            //   発生して動作が重くなるため、よく使う検索キーに INDEX を貼る。
            //   CREATE INDEX IF NOT EXISTS で冪等化しているため、
            //   既存のINDEXがある場合や複数回実行しても安全。
            migrate_indexes(connection);
        }

        /// <summary>
        /// 既存 cost_records テーブルへのカラム追加マイグレーション
        /// ALTER TABLE は列が存在する場合エラーになるため try-catch で無視する
        /// </summary>
        private void migrate_cost_records(SqliteConnection conn)
        {
            try_add_column(conn, "cost_records", "fiscal_month TEXT DEFAULT ''");
            try_add_column(conn, "cost_records", "equipment_names TEXT DEFAULT ''");
            try_add_column(conn, "cost_records", "equipment_detail TEXT DEFAULT ''");
            try_add_column(conn, "cost_records", "equipment_quantity INTEGER DEFAULT 0");
            try_add_column(conn, "cost_records", "distance_total REAL DEFAULT 0");
            try_add_column(conn, "cost_records", "vehicle_count INTEGER DEFAULT 0");
            try_add_column(conn, "cost_records", "engineer_count INTEGER DEFAULT 0");
            try_add_column(conn, "cost_records", "assistant_count INTEGER DEFAULT 0");
            try_add_column(conn, "cost_filter_tabs", "is_single_mode INTEGER DEFAULT 0");
            try_add_column(conn, "cost_filter_tabs", "is_archived INTEGER DEFAULT 0");
            try_add_column(conn, "cost_filter_tabs", "filter_date_from TEXT DEFAULT ''");
            try_add_column(conn, "cost_filter_tabs", "filter_date_to TEXT DEFAULT ''");
            try_add_column(conn, "cost_filter_tabs", "use_custom_rates INTEGER DEFAULT 0");

            // ▼▼▼ 追加：pc_users テーブルへのカラム追加マイグレーション ▼▼▼
            // Sprint 5A でユーザー管理機能追加時に既存DBへのマイグレーションが漏れていたため追加
            // 他PCの既存DB（employee_id / is_admin / pc_name がない）に対して安全に追加する
            // try_add_column は列が既存の場合は無視するため重複実行しても安全
            try_add_column(conn, "pc_users", "employee_id INTEGER DEFAULT 0");
            try_add_column(conn, "pc_users", "is_admin INTEGER DEFAULT 1");
            try_add_column(conn, "pc_users", "pc_name TEXT DEFAULT ''");

            // ▼▼▼ 追加：operation_logs テーブルへの pc_user_id カラム追加 ▼▼▼
            // Sprint 5A 以降に追加されたカラムが旧DBに存在しない場合の対応
            try_add_column(conn, "operation_logs", "pc_user_id INTEGER DEFAULT 0");
            try_add_column(conn, "operation_logs", "operator_name TEXT DEFAULT ''");
            try_add_column(conn, "operation_logs", "import_batch TEXT DEFAULT ''");

            // ▼▼▼ 追加：projects テーブルへの sort_order カラム追加 ▼▼▼
            try_add_column(conn, "projects", "sort_order INTEGER DEFAULT 0");
            try_add_column(conn, "projects", "attribute TEXT DEFAULT ''");
            try_add_column(conn, "projects", "tab_color TEXT DEFAULT ''");

            // 絞り込みタブ別単価スナップショット
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS filter_tab_rates (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    filter_tab_id   INTEGER NOT NULL,
                    rate_type       TEXT NOT NULL,
                    employee_name   TEXT DEFAULT NULL,
                    daily_rate      REAL NOT NULL DEFAULT 0,
                    created_at      TEXT DEFAULT (datetime('now','localtime'))
                )");

            // ▼▼▼ 追加：既存DBの nas_path が古いフォーマット（ファイル名なし）の場合に修正
            // \00_Database のみ保存されていた場合、起動時に SQLite Error 14 が発生する
            // ea_core.db まで含めた正しいパスに更新する
            try
            {
                var current_nas_path = conn.ExecuteScalar<string>(
                    "SELECT value FROM app_settings WHERE key = 'nas_path'") ?? "";

                if (!string.IsNullOrWhiteSpace(current_nas_path)
                    && !current_nas_path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                {
                    string fixed_path = current_nas_path.TrimEnd('\\') + @"\ea_core.db";
                    conn.Execute(
                        "UPDATE app_settings SET value = @v WHERE key = 'nas_path'",
                        new { v = fixed_path });
                }
            }
            catch
            {
                // マイグレーション失敗は無視して続行
            }

            // ▼▼▼ 削除：nas_enabled の強制上書きを廃止 ▼▼▼
            // 旧実装は毎回 nas_enabled を "1" に戻してしまいテストDB切替が効かないバグの原因だった
            // 初回設定は initialize_database の insert_default_setting（INSERT OR IGNORE）で行う
        }

        /// <summary>
        /// ▼ 追加 [v1.0.1] DB インデックス作成マイグレーション
        ///
        /// 目的：
        ///   業務テーブルへの検索インデックスを起動時に確保する。
        ///   CostManager は NAS共有上のSQLite を使用しており、レコード件数の増加に
        ///   弱い構成である。インデックスを貼ることでフルスキャン（全件走査）を回避し、
        ///   件数が数万件規模に増えた将来でも検索速度を維持する。
        ///
        /// 安全性：
        ///   全て CREATE INDEX IF NOT EXISTS で実行する。既存INDEXがあれば何もせず、
        ///   無ければ作成する。何度実行しても結果は同じ（冪等）。
        ///   各テーブルごとに try-catch で囲うため、対象テーブルが存在しない
        ///   旧DB環境でも他のINDEX作成は継続される。
        ///
        /// 効果（推定）：
        ///   ・現場タブ切替時の cost_records 検索：数倍〜十倍速化
        ///   ・日報集計時の daily_reports 絞り込み：数倍速化
        ///   ・JOIN先（daily_equipment / daily_transport）の連結：大幅高速化
        ///   ・ダッシュボード操作履歴表示：数倍速化
        ///
        /// 注意：
        ///   インデックスは書き込み時にも更新されるため、INSERT/UPDATE/DELETE が
        ///   理論上わずかに遅くなる。ただしこの規模（数千〜数万件）では体感差はない。
        ///
        /// ▼ 修正 [v1.0.1] private → public static
        ///   ローカルDB / NAS DB 両方への適用が必要なため、外部（App.xaml.cs）からも
        ///   呼び出せるよう public static に変更。initialize_database() は
        ///   ローカルDB初期化時に呼ばれ、その後 NAS切替後に App.xaml.cs から
        ///   再度呼び出される（NAS DB 側のINDEX作成のため）。
        /// </summary>
        public static void migrate_indexes(SqliteConnection conn)
        {
            // ── daily_reports（日報本体）──
            // 業務でよく使う検索条件：日付・現場コード・社員名・取込バッチ
            try_create_index(conn,
                "CREATE INDEX IF NOT EXISTS idx_daily_reports_date "
                + "ON daily_reports(report_date)");
            try_create_index(conn,
                "CREATE INDEX IF NOT EXISTS idx_daily_reports_category "
                + "ON daily_reports(category_code)");
            try_create_index(conn,
                "CREATE INDEX IF NOT EXISTS idx_daily_reports_employee "
                + "ON daily_reports(employee_name)");
            try_create_index(conn,
                "CREATE INDEX IF NOT EXISTS idx_daily_reports_import_batch "
                + "ON daily_reports(import_batch)");

            // ── daily_equipment / daily_transport（日報の明細）──
            // FK列にINDEXを貼ることで JOIN（連結検索）が高速化される。
            // SQLite は FK 制約を貼っても自動で INDEX を作らないため明示的に貼る。
            try_create_index(conn,
                "CREATE INDEX IF NOT EXISTS idx_daily_equipment_report "
                + "ON daily_equipment(daily_report_id)");
            try_create_index(conn,
                "CREATE INDEX IF NOT EXISTS idx_daily_transport_report "
                + "ON daily_transport(daily_report_id)");

            // ── cost_records（集計データ・タブ表示の主軸）──
            // 「現場コード × 月度」の複合検索が原価集計タブの表示時に毎回走るため、
            // 複合インデックスを貼るとタブ切替速度が体感できるレベルで変わる。
            try_create_index(conn,
                "CREATE INDEX IF NOT EXISTS idx_cost_records_cat_fiscal "
                + "ON cost_records(category_code, fiscal_month)");

            // ── operation_logs（操作ログ）──
            // ダッシュボードの操作履歴表示で日時降順ソート・ユーザー別絞り込みが走る。
            try_create_index(conn,
                "CREATE INDEX IF NOT EXISTS idx_operation_logs_datetime "
                + "ON operation_logs(log_datetime)");
            try_create_index(conn,
                "CREATE INDEX IF NOT EXISTS idx_operation_logs_pc_user "
                + "ON operation_logs(pc_user_id)");
        }

        /// <summary>
        /// ▼ 追加 [v1.0.1] CREATE INDEX のラッパー
        ///   個別のINDEX作成失敗（カラムが旧DBに存在しない等）でも他のINDEX作成を継続するため
        ///   try-catch で囲って例外を握りつぶす。失敗内容はデバッグ出力に残す。
        ///
        /// ▼ 修正 [v1.0.1] private → private static
        ///   migrate_indexes が public static になったため、ここも static に揃える。
        /// </summary>
        private static void try_create_index(SqliteConnection conn, string sql)
        {
            try
            {
                conn.Execute(sql);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[migrate_indexes] INDEX作成スキップ: {ex.GetType().Name} : {ex.Message}");
            }
        }

        /// <summary>
        /// ALTER TABLE ADD COLUMN のラッパー（列が既存の場合は無視）
        /// </summary>
        private void try_add_column(SqliteConnection conn, string table, string column_def)
        {
            try
            {
                conn.Execute($"ALTER TABLE {table} ADD COLUMN {column_def}");
            }
            catch
            {
                // "duplicate column name" の場合は無視
            }
        }

        private void insert_default_setting(SqliteConnection conn, string key, string val)
        {
            conn.Execute(
                "INSERT OR IGNORE INTO app_settings (key, value) VALUES (@key, @val)",
                new { key, val });
        }

        /// <summary>
        /// バックアップを作成
        /// </summary>
        public static string create_backup()
        {
            string db_path = get_db_path();
            if (!File.Exists(db_path)) return string.Empty;

            const string APP_PREFIX = "EA_CostManager";
            const string BACKUP_DIR = @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\01_Backups\01_Cost Manager";

            string backup_dir = BACKUP_DIR;

            // NASに接続できない場合はDBと同じフォルダ内のbackupsにフォールバック
            if (!Directory.Exists(backup_dir))
            {
                backup_dir = Path.Combine(
                    Path.GetDirectoryName(db_path) ?? "", "backups");
            }

            if (!Directory.Exists(backup_dir))
                Directory.CreateDirectory(backup_dir);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backup_path = Path.Combine(backup_dir,
                $"{APP_PREFIX}_ea_core_{timestamp}.db");

            File.Copy(db_path, backup_path, true);
            return backup_path;
        }

        /// <summary>
        /// [Sprint 6] 1ヶ月以上前のバックアップファイルを削除する
        /// NASバックアップフォルダとローカルバックアップフォルダの両方を対象とする
        /// </summary>
        public static void cleanup_old_backups()
        {
            const string NAS_BACKUP_DIR =
                @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\01_Backups\01_Cost Manager";

            var cutoff = DateTime.Now.AddMonths(-1);

            // NASバックアップフォルダを削除
            try_cleanup_dir(NAS_BACKUP_DIR, cutoff);

            // ローカルバックアップフォルダも削除
            string db_path = get_db_path();
            string local_backup_dir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(db_path) ?? "", "backups");
            try_cleanup_dir(local_backup_dir, cutoff);
        }

        private static void try_cleanup_dir(string dir, DateTime cutoff)
        {
            try
            {
                if (!Directory.Exists(dir)) return;

                // 定期バックアップ（EA_CostManager_*.db）と退避ファイル（before_restore_*.db）を対象
                var patterns = new[] { "EA_CostManager_*.db", "before_restore_*.db" };
                foreach (var pattern in patterns)
                {
                    foreach (var file in Directory.GetFiles(dir, pattern))
                    {
                        var info = new FileInfo(file);
                        if (info.LastWriteTime < cutoff)
                        {
                            info.Delete();
                            System.Diagnostics.Debug.WriteLine($"古いバックアップ削除: {info.Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"バックアップ削除エラー（無視）: {ex.Message}");
            }
        }

        /// <summary>
        /// ローカルDBからNASへの切り替え
        /// ローカルDBが存在する場合はNASへコピーしてから切り替える
        /// </summary>
        public static bool switch_to_nas(string nas_path)
        {
            if (!set_db_path(nas_path))
                return false;

            string local_path = get_local_db_path();

            // NASにDBがまだなくてローカルにある場合はコピー
            if (!File.Exists(nas_path) && File.Exists(local_path))
            {
                try { File.Copy(local_path, nas_path, false); }
                catch { return false; }
            }

            return true;
        }

        /// <summary>
        /// ▼ 追加 [v1.0.2 DB破損ヘッダー検証] SQLite データベースのファイルヘッダーを検証
        ///
        /// 目的：
        ///   integrity_check は DB ファイルを SQLite として開けることが前提のクエリのため、
        ///   ヘッダー破損（SQLite Error 26: 'file is not a database'）は検知できない。
        ///   接続を試みる前に「ファイル先頭16バイトが SQLite フォーマットか」を
        ///   バイナリレベルで検証することで、ヘッダー破損を早期検出する。
        ///
        /// 戻り値：
        ///   true  : ファイルが存在しないか、先頭16バイトが正規の SQLite ヘッダー
        ///   false : 先頭16バイトが SQLite ヘッダーでない（破損または別形式ファイル）
        ///
        /// 設計上の注意：
        ///   ・ファイルが存在しない場合は true を返す（初回起動・新規作成シナリオに対応）
        ///   ・I/O 例外時も true を返して既存処理に委ねる
        ///     （アクセス権問題等は破損ではなく、既存の catch ブロックで扱うべきため）
        ///   ・FileShare.ReadWrite で他プロセスがDBを開いていても読み出し可能にする
        /// </summary>
        public static bool is_valid_sqlite_header(string db_path)
        {
            // SQLite ヘッダーの正規バイト列: "SQLite format 3\0" (UTF-8/ASCII)
            // 全 SQLite v3 系 DB ファイルの先頭16バイトに必ず存在する
            byte[] expected = new byte[]
            {
                0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, // "SQLite f"
                0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00  // "ormat 3\0"
            };

            try
            {
                // ファイルが存在しない場合は検証スキップ（新規作成想定）
                if (!File.Exists(db_path)) return true;

                // ファイルサイズが 16 バイト未満ならヘッダー成立不可（破損確定）
                var info = new FileInfo(db_path);
                if (info.Length < 16) return false;

                // 先頭16バイトを読み出して期待値と比較
                byte[] header = new byte[16];
                using (var fs = new FileStream(db_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read = fs.Read(header, 0, 16);
                    if (read < 16) return false;
                }

                for (int i = 0; i < 16; i++)
                {
                    if (header[i] != expected[i]) return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                // I/O 例外は破損ではなくアクセス権・ネットワーク問題等の可能性が高い
                // 既存の NAS切替 catch ブロックに委ねるため true を返す（検証スキップ扱い）
                System.Diagnostics.Debug.WriteLine($"is_valid_sqlite_header 例外（検証スキップ）: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// ▼ 追加 [v1.0.2 DB破損ヘッダー検証] 最新のバックアップファイルを検索
        ///
        /// 目的：
        ///   ヘッダー破損検知時に、復元前にユーザーへ「どのバックアップから復元するか」を
        ///   ダイアログで通知するため、最新バックアップのファイル情報（名前・日時・サイズ）を
        ///   FileInfo として取得する。
        ///
        ///   検索ロジックは restore_latest_backup() と同一ロジックで揃える：
        ///   NAS → ローカル の順で全候補を見て、最終更新日時が最新のものを返す。
        ///
        /// 戻り値：
        ///   最新バックアップの FileInfo。バックアップが1件も存在しない場合は null。
        ///
        /// 注意：
        ///   検索パターンは "EA_CostManager_ea_core_*.db" 固定。
        ///   create_backup() の保存ファイル名（EA_CostManager_ea_core_yyyyMMdd_HHmmss.db）と
        ///   restore_latest_backup() の検索パターンと完全一致させること。
        /// </summary>
        public static FileInfo? find_latest_backup()
        {
            try
            {
                const string NAS_BACKUP_DIR =
                    @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\01_Backups\01_Cost Manager";

                string db_path = get_db_path();
                string local_backup_dir = Path.Combine(
                    Path.GetDirectoryName(db_path) ?? "", "backups");

                // NAS → ローカル の順で最新バックアップを探す
                // 検索パターンは restore_latest_backup と完全一致させる
                FileInfo? latest = null;

                foreach (var dir in new[] { NAS_BACKUP_DIR, local_backup_dir })
                {
                    if (!Directory.Exists(dir)) continue;

                    var candidate = Directory.GetFiles(dir, "EA_CostManager_ea_core_*.db")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();

                    if (candidate != null &&
                        (latest == null || candidate.LastWriteTime > latest.LastWriteTime))
                        latest = candidate;
                }

                return latest;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"find_latest_backup 例外: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// ▼▼▼ 追加：最新バックアップからDBを自動復元する ▼▼▼
        /// DB破損検知時に App.xaml.cs から呼ばれる
        /// NASバックアップ → ローカルバックアップの順で最新ファイルを探して復元する
        /// </summary>
        public static bool restore_latest_backup()
        {
            try
            {
                const string NAS_BACKUP_DIR =
                    @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\01_Backups\01_Cost Manager";

                string db_path = get_db_path();
                string local_backup_dir = Path.Combine(
                    Path.GetDirectoryName(db_path) ?? "", "backups");

                // NAS → ローカルの順で最新バックアップを探す
                FileInfo? latest = null;

                foreach (var dir in new[] { NAS_BACKUP_DIR, local_backup_dir })
                {
                    if (!Directory.Exists(dir)) continue;

                    var candidate = Directory.GetFiles(dir, "EA_CostManager_ea_core_*.db")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();

                    if (candidate != null &&
                        (latest == null || candidate.LastWriteTime > latest.LastWriteTime))
                        latest = candidate;
                }

                if (latest == null) return false;

                // 破損DBをリネームして退避してからバックアップを復元
                string broken_path = db_path + ".broken_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                if (File.Exists(db_path))
                    File.Move(db_path, broken_path);

                // WALファイルも削除（破損の原因になるため）
                string wal_path = db_path + "-wal";
                string shm_path = db_path + "-shm";
                if (File.Exists(wal_path)) File.Delete(wal_path);
                if (File.Exists(shm_path)) File.Delete(shm_path);

                File.Copy(latest.FullName, db_path, true);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"自動復元エラー: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// NAS接続が有効かどうか確認
        /// </summary>
        public static bool is_nas_available(string nas_path)
        {
            if (string.IsNullOrWhiteSpace(nas_path)) return false;
            string? dir = Path.GetDirectoryName(nas_path);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        }
    }
}
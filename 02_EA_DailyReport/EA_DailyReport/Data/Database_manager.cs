using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Dapper;

namespace EA_DailyReport.Data
{
    /// <summary>
    /// SQLite データベースの初期化と接続管理（EA_DailyReport専用）
    /// WALモード + busy_timeout + 自動バックアップ対応
    ///
    /// ▼ 設計方針（v0.1.4 で更新）
    /// ・ローカルDBパス：%LOCALAPPDATA%\01_EABASE Series\02_DailyReport\local_db\ea_daily_local.db
    ///   （exe隣はProgram Files配下になり書き込み不可になるため不可。設計書10.1）
    /// ・バックアップ先：\\NAS7E6AA6\...\02_Updates\02_DailyReport
    /// ・テストDB：    \\NAS7E6AA6\...\99_Test\02_DailyReport\ea_core.db
    /// ・自動同期：    UI操作はローカルDBに即書き込み・バックグラウンドでNASへ同期
    /// ・WALモード：   ローカルDBのみON（create_connection）、NAS DBはOFF（create_nas_connection）
    ///                 SQLite公式が「ネットワークファイルシステム上のWALは非推奨」と明記（設計書4.2）
    /// </summary>
    public class database_manager
    {
        // ─────────────────────────────────────────────
        // パス定義
        // ─────────────────────────────────────────────

        // ▼ 修正：v0.1.4 設計書10.1
        // ローカルDBは %LOCALAPPDATA%\01_EABASE Series\02_DailyReport\local_db\ に配置
        // 旧仕様（exe隣 + Assembly.Location）は Inno Setup インストール後に
        // Windows保護領域となり書き込み拒否されるため廃止
        // ▼ 削除：private static readonly string _exe_directory（exe隣ベースのパス計算が不要になった）
        // ▼ 削除：using System.Reflection（Assembly.GetExecutingAssembly() を使わなくなった）
        private const string EABASE_DIR_NAME = "01_EABASE Series";
        private const string APP_DIR_NAME = "02_DailyReport";
        private const string LOCAL_DB_FOLDER = "local_db";

        private static readonly string _local_app_data =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        private static readonly string _default_directory =
            Path.Combine(_local_app_data, EABASE_DIR_NAME, APP_DIR_NAME, LOCAL_DB_FOLDER);

        private static readonly string _default_db_name = "ea_daily_local.db";

        // ▼ 追加：v0.1.7 テストモード用ローカル DB ファイル名
        // 本番モードと混在しないよう別ファイルを使用する（yori 確定方針）
        private static readonly string _test_db_name = "ea_daily_local_test.db";

        // ▼ 追加：v0.1.7 現在のモード（"production" / "test"）
        // App.xaml.cs の起動時に apply_db_mode() で設定される
        // 起動時に1回決まる（実行中の動的切替は不可・再起動が必要）
        private static string _current_db_mode = "production";

        // ▼ 追加：v0.1.4 設計書10.2 旧パスマイグレーション用
        // DailyReportMigration.migrate_old_db_path() から参照する
        // 旧仕様：exe隣の local_db フォルダ
        public static readonly string OLD_DEFAULT_DIRECTORY =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LOCAL_DB_FOLDER);

        /// <summary>DBファイル名（マイグレーション処理から参照）</summary>
        public static readonly string DEFAULT_DB_NAME = _default_db_name;

        // ▼ 修正：v0.1.5 yori指摘対応
        // バックアップ先パスを 02_Updates → 01_Backups に変更
        // 旧仕様：\\NAS7E6AA6\...\01_EABASE Series\02_Updates\02_DailyReport
        //   → 02_Updates フォルダはアップデート配布用（version.txt + インストーラー）
        //     そこに混入させると CostManager の Updates と紛らわしい
        // 新仕様：\\NAS7E6AA6\...\01_EABASE Series\01_Backups\02_DailyReport
        //   → 01_Backups\01_Cost Manager と並列にする運用ルールに合わせる
        private const string NAS_BACKUP_DIR =
            @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\01_Backups\02_DailyReport";

        // 本番NAS DBのデフォルトパス（CostManagerと同じea_core.dbを共有）
        private const string DEFAULT_NAS_PATH =
            @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\00_Database\ea_core.db";

        // バックアップファイル名のプレフィックス（CostManagerと区別するため）
        private const string BACKUP_FILE_PREFIX = "EA_DailyReport";

        private static string _current_db_path = string.Empty;

        // ─────────────────────────────────────────────
        // パス取得・設定
        // ─────────────────────────────────────────────

        /// <summary>
        /// 現在のDBファイルパスを取得
        /// ▼ 修正：v0.1.7 テストモード時は ea_daily_local_test.db を使用
        /// </summary>
        public static string get_db_path()
        {
            if (string.IsNullOrEmpty(_current_db_path))
            {
                _current_db_path = Path.Combine(_default_directory, get_db_file_name());
            }
            return _current_db_path;
        }

        /// <summary>DBパスを変更（NAS切替時に使用）</summary>
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
        /// ▼ 修正：v0.1.7 テストモード時は ea_daily_local_test.db
        /// </summary>
        public static string get_local_db_path()
        {
            return Path.Combine(_default_directory, get_db_file_name());
        }

        // ────────────────────────────────────────────────
        // ▼ 追加：v0.1.7 DB モード（本番/テスト）切替
        // ────────────────────────────────────────────────

        /// <summary>
        /// DB モードを適用する（App.xaml.cs の起動フローから呼ぶ）
        /// "production" → ea_daily_local.db
        /// "test"       → ea_daily_local_test.db
        ///
        /// 注：実行中の動的切替は不可。設定変更時は再起動が必要
        /// </summary>
        public static void apply_db_mode(string mode)
        {
            _current_db_mode = (mode == "test") ? "test" : "production";
            // 既存パスをクリアして再計算させる
            _current_db_path = string.Empty;
        }

        /// <summary>現在の DB モードを取得</summary>
        public static string get_db_mode() => _current_db_mode;

        /// <summary>現在のモードに応じた DB ファイル名を返す</summary>
        private static string get_db_file_name()
        {
            return _current_db_mode == "test" ? _test_db_name : _default_db_name;
        }

        // ────────────────────────────────────────────────
        // ▼ 追加：v0.1.7 DbConfig 内部静的クラス
        // DB モード（本番/テスト）を JSON ファイルに保存
        // 用途：DB 初期化「前」に読む必要があるため、app_settings テーブルではなく
        //       %LOCALAPPDATA%\01_EABASE Series\02_DailyReport\db_config.json に保存
        // ────────────────────────────────────────────────

        public static class DbConfig
        {
            private static readonly string CONFIG_PATH = Path.Combine(
                _local_app_data, EABASE_DIR_NAME, APP_DIR_NAME, "db_config.json");

            /// <summary>DbConfig の中身（プロパティ）</summary>
            public class Config
            {
                /// <summary>"production" or "test"</summary>
                public string db_mode { get; set; } = "production";
            }

            /// <summary>設定を読み込む（ファイル無しなら本番モードのデフォルト）</summary>
            public static Config load()
            {
                try
                {
                    if (System.IO.File.Exists(CONFIG_PATH))
                    {
                        string json = System.IO.File.ReadAllText(CONFIG_PATH);
                        var cfg = System.Text.Json.JsonSerializer.Deserialize<Config>(json);
                        return cfg ?? new Config();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DbConfig] 読込失敗: {ex.Message}");
                }
                return new Config();
            }

            /// <summary>設定を保存する</summary>
            public static void save(Config cfg)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(CONFIG_PATH);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    };
                    string json = System.Text.Json.JsonSerializer.Serialize(cfg, options);
                    System.IO.File.WriteAllText(CONFIG_PATH, json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DbConfig] 保存失敗: {ex.Message}");
                }
            }
        }

        /// <summary>接続文字列を取得</summary>
        public static string get_connection_string()
        {
            return $"Data Source={get_db_path()}";
        }

        // ─────────────────────────────────────────────
        // 接続生成
        // ─────────────────────────────────────────────

        /// <summary>
        /// 新しいローカルSQLite接続を生成（WALモードON + busy_timeout）
        /// 通常のUI操作・同期処理ともにこの接続を使用する
        /// ▼ v0.1.4：ローカルDB専用。NAS DB接続には create_nas_connection を使用すること
        /// </summary>
        public static SqliteConnection create_connection()
        {
            var connection = new SqliteConnection(get_connection_string());
            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                // ▼ v0.1.4 設計書4.2：ローカルDBのみWALモードON
                // 自分のPCしか触らないため安全。同期処理とUI操作の並行アクセスに必須
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                cmd.ExecuteNonQuery();

                // busy_timeout 5秒
                // ローカルDBは自分のPCしか触らないので5秒で十分
                cmd.CommandText = "PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();

                // 外部キー制約を有効化
                cmd.CommandText = "PRAGMA foreign_keys=ON;";
                cmd.ExecuteNonQuery();
            }

            return connection;
        }

        /// <summary>
        /// ▼ 追加：v0.1.4 設計書4.2
        /// NAS DB接続を生成（WALモード OFF + busy_timeout 長め）
        /// SQLite公式が「ネットワークファイルシステム上のWALは非推奨」と明記しているため
        /// SMB/NFS経由でmmap・ファイルロックが信頼できず、-wal/-shmファイルの破損リスクがある
        /// 必ずこのメソッドからNAS接続を取得すること（create_connection は使用しないこと）
        /// </summary>
        /// <param name="nas_path">NAS DB のフルパス（例：\\NAS7E6AA6\...\ea_core.db）</param>
        public static SqliteConnection create_nas_connection(string nas_path)
        {
            if (string.IsNullOrWhiteSpace(nas_path))
                throw new ArgumentException("NAS DBパスが空です。", nameof(nas_path));

            var connection = new SqliteConnection($"Data Source={nas_path}");
            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                // ▼ v0.1.4 設計書4.2：NAS DBはWALモードOFF
                // DELETE モード（journal方式）に明示的に設定
                // ※ 既存DBがWALモードで開かれていた場合に DELETE に切り替える効果も狙う
                // ※ NAS共有上ではWALファイル(-wal/-shm)が破損する可能性があるため
                cmd.CommandText = "PRAGMA journal_mode=DELETE;";
                cmd.ExecuteNonQuery();

                // busy_timeout を長めに（10秒）
                // NAS共有は競合発生時に待ち時間が長くなるため
                cmd.CommandText = "PRAGMA busy_timeout=10000;";
                cmd.ExecuteNonQuery();

                // 外部キー制約を有効化
                cmd.CommandText = "PRAGMA foreign_keys=ON;";
                cmd.ExecuteNonQuery();
            }

            return connection;
        }

        // ─────────────────────────────────────────────
        // DB初期化
        // ─────────────────────────────────────────────

        /// <summary>
        /// データベースとテーブルを初期化
        /// 日報ソフト用テーブルはDailyReportMigrationで作成
        /// このメソッドではマスタ系テーブル（CostManager互換）を作成する
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

            // ─── マスタ系（CostManager互換） ───
            // 日報ソフトでは employees / projects を NASから取得してキャッシュとして保持
            // ローカルDB上にも同じスキーマで作成しておく
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
                    sort_order      INTEGER DEFAULT 0,
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

            // ─── 設定系 ───
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS app_settings (
                    key         TEXT PRIMARY KEY,
                    value       TEXT,
                    updated_at  TEXT DEFAULT (datetime('now','localtime')),
                    updated_by  TEXT DEFAULT ''
                )");

            // ─── ログ系 ───
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS operation_logs (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    log_datetime    TEXT DEFAULT (datetime('now','localtime')),
                    pc_user_id      INTEGER DEFAULT 0,
                    operator_name   TEXT,
                    operation_type  TEXT,
                    target_table    TEXT,
                    target_id       INTEGER,
                    detail          TEXT,
                    record_count    INTEGER,
                    file_path       TEXT,
                    import_batch    TEXT DEFAULT ''
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

            // ─── ユーザー管理テーブル ───
            // ▼ 修正：日報ソフトでは最初から employee_id / is_admin / pc_name を含めて作成
            // CostManagerからの後方互換は不要（日報ソフトのローカルDBは新規作成のため）
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS pc_users (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    mac_address TEXT NOT NULL UNIQUE,
                    pc_name     TEXT DEFAULT '',
                    employee_id INTEGER DEFAULT 0,
                    user_name   TEXT NOT NULL,
                    is_admin    INTEGER DEFAULT 1,
                    created_at  TEXT DEFAULT (datetime('now','localtime')),
                    updated_at  TEXT DEFAULT (datetime('now','localtime'))
                )");

            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS user_sessions (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    mac_address TEXT NOT NULL,
                    user_name   TEXT NOT NULL,
                    software    TEXT DEFAULT 'EA_DailyReport',
                    status      TEXT DEFAULT 'active',
                    current_tab TEXT DEFAULT '',
                    started_at  TEXT DEFAULT (datetime('now','localtime')),
                    updated_at  TEXT DEFAULT (datetime('now','localtime'))
                )");

            // ─── デフォルト設定値 ───
            insert_default_setting(connection, "engineer_rate", "34800");
            insert_default_setting(connection, "assistant_rate", "28000");
            insert_default_setting(connection, "base_hours_per_day", "8");
            insert_default_setting(connection, "db_path", "");

            // NAS接続設定（CostManagerと同じNASパスをデフォルトに）
            // INSERT OR IGNORE で初回のみ設定（ユーザー変更を尊重）
            insert_default_setting(connection, "nas_enabled", "1");
            insert_default_setting(connection, "nas_path", DEFAULT_NAS_PATH);

            // 最終同期日時（NasSyncServiceで使用）
            insert_default_setting(connection, "last_sync_at", "");

            // ▼ 追加：v0.1.4 設計書10.4
            // マスタ・ログ系テーブルへのインデックス網羅的追加
            // CostManagerでは未対応 → データ増加時のクエリ低速化が発生していた
            create_master_indexes(connection);

            // 既存DBへの後方互換マイグレーション
            migrate_existing_columns(connection);
        }

        /// <summary>
        /// ▼ 追加：v0.1.4 設計書10.4
        /// マスタ・ログ系テーブルへのインデックス追加
        /// WHERE/JOIN/ORDER BY 頻出カラム・FK列に必ずCREATE INDEX
        /// SQLiteはFK制約を貼っても自動でINDEXを作らない点に注意
        ///
        /// ▼ v0.1.4 修正：CREATE INDEX を try_create_index 経由で実行
        /// 既存DB（CostManager由来）にカラムが存在しないケースを想定し、
        /// 失敗してもアプリ動作を妨げないよう try-catch で吸収する
        /// （インデックスは性能最適化のため、無くても機能には影響しない）
        ///
        /// 注：日報・作業詳細・同期管理・通知のインデックスは
        ///     DailyReportMigration 側で作成しているため、ここでは作成しない
        /// </summary>
        private void create_master_indexes(SqliteConnection conn)
        {
            // pc_users：mac_address 検索が頻発（ログイン認証）
            // mac_address は UNIQUE 指定だが明示的にIDXを作成（互換性確保）
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_pc_users_mac
                                     ON pc_users(mac_address)");
            // employee_id でのJOINも頻繁
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_pc_users_employee
                                     ON pc_users(employee_id)");

            // user_sessions：mac_address + status の複合検索（同時接続表示）
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_user_sessions_mac_status
                                     ON user_sessions(mac_address, status)");
            // updated_at で並び替え（ハートビート判定）
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_user_sessions_updated
                                     ON user_sessions(updated_at)");

            // employees：氏名検索・有効フラグ
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_employees_name
                                     ON employees(employee_name)");
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_employees_active
                                     ON employees(is_active)");

            // projects：区分コード検索（オートコンプリート）・有効フラグ・並び順
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_projects_category
                                     ON projects(category_code)");
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_projects_active
                                     ON projects(is_active)");
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_projects_sort
                                     ON projects(sort_order)");

            // operation_logs：日時降順検索（最新ログ表示）・PCユーザーID
            // ▼ 注意：CostManager由来の既存DBには log_datetime カラムが
            //         存在しない可能性があるため try_create_index で安全化
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_operation_logs_datetime
                                     ON operation_logs(log_datetime)");
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_operation_logs_pc_user
                                     ON operation_logs(pc_user_id)");

            // error_logs：日時降順検索
            // ▼ 注意：同上の理由で try_create_index で安全化
            try_create_index(conn, @"CREATE INDEX IF NOT EXISTS idx_error_logs_datetime
                                     ON error_logs(log_datetime)");
        }

        /// <summary>
        /// ▼ 追加：v0.1.4 修正
        /// CREATE INDEX のラッパー。対象カラムが存在しない場合は警告ログだけ出して無視する
        ///
        /// 用途：CostManager 由来の既存DBに対してインデックスを追加する際、
        /// テーブルのスキーマが想定と異なる（カラム名違い等）ケースを吸収する
        ///
        /// インデックスは性能最適化のため、作成失敗してもアプリ動作には影響しない
        /// （ただしクエリが遅くなる可能性はあるため、別途スキーマ統一の検討が必要）
        /// </summary>
        private void try_create_index(SqliteConnection conn, string sql)
        {
            try
            {
                conn.Execute(sql);
            }
            catch (SqliteException ex)
            {
                // 「no such column」「no such table」を想定して吸収
                // それ以外のエラー（構文エラー等）も同様に吸収（性能最適化のため）
                System.Diagnostics.Debug.WriteLine(
                    $"[create_master_indexes] インデックス作成スキップ: {ex.Message}");
            }
        }

        /// <summary>
        /// 既存テーブルへのカラム追加マイグレーション
        /// ALTER TABLE は列が存在する場合エラーになるため try-catch で無視する
        /// </summary>
        private void migrate_existing_columns(SqliteConnection conn)
        {
            // pc_users テーブルへのカラム追加
            try_add_column(conn, "pc_users", "employee_id INTEGER DEFAULT 0");
            try_add_column(conn, "pc_users", "is_admin INTEGER DEFAULT 1");
            try_add_column(conn, "pc_users", "pc_name TEXT DEFAULT ''");

            // operation_logs テーブルへのカラム追加
            try_add_column(conn, "operation_logs", "pc_user_id INTEGER DEFAULT 0");
            try_add_column(conn, "operation_logs", "import_batch TEXT DEFAULT ''");

            // ▼ 追加：v0.1.4 修正
            // CostManager 由来の既存DBには log_datetime カラムが無いケースに対応
            // ※ ALTER TABLE ADD COLUMN では関数 DEFAULT (datetime(...)) が
            //    SQLite 旧バージョンで非対応のため、空文字 DEFAULT で安全側に倒す
            //    （新規 INSERT 時には呼び出し側で明示的に日時を渡す想定）
            try_add_column(conn, "operation_logs", "log_datetime TEXT DEFAULT ''");
            try_add_column(conn, "error_logs", "log_datetime TEXT DEFAULT ''");

            // user_sessions テーブルへのカラム追加
            try_add_column(conn, "user_sessions", "current_tab TEXT DEFAULT ''");

            // projects テーブルへのカラム追加
            try_add_column(conn, "projects", "sort_order INTEGER DEFAULT 0");
            try_add_column(conn, "projects", "attribute TEXT DEFAULT ''");
            try_add_column(conn, "projects", "tab_color TEXT DEFAULT ''");
        }

        /// <summary>ALTER TABLE ADD COLUMN のラッパー（列が既存の場合は無視）</summary>
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

        // ─────────────────────────────────────────────
        // バックアップ
        // ─────────────────────────────────────────────

        /// <summary>
        /// バックアップを作成
        /// 日報ソフト用に NAS_BACKUP_DIR（02_DailyReport）に保存する
        /// </summary>
        public static string create_backup()
        {
            string db_path = get_db_path();
            if (!File.Exists(db_path)) return string.Empty;

            string backup_dir = NAS_BACKUP_DIR;

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
                $"{BACKUP_FILE_PREFIX}_ea_core_{timestamp}.db");

            File.Copy(db_path, backup_path, true);
            return backup_path;
        }

        /// <summary>
        /// 1ヶ月以上前のバックアップファイルを削除する
        /// NASバックアップフォルダとローカルバックアップフォルダの両方を対象とする
        /// </summary>
        public static void cleanup_old_backups()
        {
            var cutoff = DateTime.Now.AddMonths(-1);

            // NASバックアップフォルダを削除
            try_cleanup_dir(NAS_BACKUP_DIR, cutoff);

            // ローカルバックアップフォルダも削除
            string db_path = get_db_path();
            string local_backup_dir = Path.Combine(
                Path.GetDirectoryName(db_path) ?? "", "backups");
            try_cleanup_dir(local_backup_dir, cutoff);
        }

        private static void try_cleanup_dir(string dir, DateTime cutoff)
        {
            try
            {
                if (!Directory.Exists(dir)) return;

                // 定期バックアップと退避ファイルを対象
                var patterns = new[] { $"{BACKUP_FILE_PREFIX}_*.db", "before_restore_*.db" };
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

        // ─────────────────────────────────────────────
        // NAS切替・復元
        // ─────────────────────────────────────────────

        /// <summary>
        /// ローカルDBからNASへの切り替え
        /// ▼ 修正：日報ソフトではローカルDBは「自分の入力データ」だけを持つため
        /// ローカル→NASのコピーは行わない（CostManagerとは別の挙動）
        /// </summary>
        public static bool switch_to_nas(string nas_path)
        {
            // パスを切り替えるだけ（ローカルからのコピーはしない）
            return set_db_path(nas_path);
        }

        /// <summary>
        /// 最新バックアップからDBを自動復元する
        /// DB破損検知時に App.xaml.cs から呼ばれる
        /// NASバックアップ → ローカルバックアップの順で最新ファイルを探して復元する
        /// </summary>
        public static bool restore_latest_backup()
        {
            try
            {
                string db_path = get_db_path();
                string local_backup_dir = Path.Combine(
                    Path.GetDirectoryName(db_path) ?? "", "backups");

                // NAS → ローカルの順で最新バックアップを探す
                FileInfo? latest = null;

                foreach (var dir in new[] { NAS_BACKUP_DIR, local_backup_dir })
                {
                    if (!Directory.Exists(dir)) continue;

                    var candidate = Directory.GetFiles(dir, $"{BACKUP_FILE_PREFIX}_ea_core_*.db")
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

        /// <summary>NAS接続が有効かどうか確認</summary>
        public static bool is_nas_available(string nas_path)
        {
            if (string.IsNullOrWhiteSpace(nas_path)) return false;
            string? dir = Path.GetDirectoryName(nas_path);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        }
    }
}
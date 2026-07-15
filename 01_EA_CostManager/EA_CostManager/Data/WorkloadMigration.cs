using Dapper;
using Microsoft.Data.Sqlite;

namespace EA_CostManager.Data
{
    /// <summary>
    /// ▼ 追加 [Sprint 7A]：工数表機能用テーブルのマイグレーション
    ///
    /// 【追加テーブル（5つ）】
    ///   workload_categories        … 業務区分（案件ごと・工数表のタブ）
    ///   workload_category_keywords … 業務区分の検出キーワード（detail への部分一致）
    ///   workload_subgroups         … サブ分類（category_id NULL=案件共通セット／値あり=区分個別セット）
    ///   workload_subgroup_keywords … サブ分類の検出キーワード
    ///   workload_subgroup_modes    … 区分ごとのサブ分類モード（common/custom/none。行が無ければ common）
    ///
    /// 【運用】
    /// ・全DDLは CREATE TABLE IF NOT EXISTS / CREATE INDEX IF NOT EXISTS のため冪等
    ///   （何度実行しても安全）。既存テーブルへの変更は一切行わない。
    /// ・既存の Migration（V2Migration / ErrorLogMigration / CategoryGroupMigration）と同様、
    ///   App.xaml.cs の起動フローで「ローカルDB初期化直後」と「NAS切替直後」の
    ///   2箇所から呼び出すこと（DB接続先ごとに実行が必要）。
    ///   呼び出し例：WorkloadMigration.migrate(conn);
    /// </summary>
    public static class WorkloadMigration
    {
        /// <summary>
        /// 工数表用テーブル・インデックスを作成する（冪等）。
        /// </summary>
        /// <param name="conn">対象DBへのオープン済み接続（ローカル/NAS どちらも可）</param>
        public static void migrate(SqliteConnection conn)
        {
            // ---- 業務区分（案件ごと。工数表画面のタブとして表示） ----
            // sort_order はタブの表示順であり、キーワード判定の評価順も兼ねる
            //（例：「詳細」を「現況」より先に評価したい場合は sort_order を小さくする）
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS workload_categories (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id  INTEGER NOT NULL,
                    name        TEXT    NOT NULL,
                    sort_order  INTEGER NOT NULL DEFAULT 0,
                    is_active   INTEGER NOT NULL DEFAULT 1,
                    created_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_by  TEXT    DEFAULT ''
                );");

            // ---- 業務区分の検出キーワード ----
            // daily_reports.detail（正規化後）への部分一致で判定する文字列。
            // priority は区分内での評価順（小さいほど先に評価）
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS workload_category_keywords (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    category_id INTEGER NOT NULL,
                    keyword     TEXT    NOT NULL,
                    priority    INTEGER NOT NULL DEFAULT 0,
                    created_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_by  TEXT    DEFAULT ''
                );");

            // ---- サブ分類（橋名など。区分の中をさらに分ける単位） ----
            // category_id が NULL の行 … 案件共通セット（modeが common の全区分で使用）
            // category_id に値がある行 … その区分専用セット（modeが custom の区分で使用）
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS workload_subgroups (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id  INTEGER NOT NULL,
                    category_id INTEGER,
                    name        TEXT    NOT NULL,
                    sort_order  INTEGER NOT NULL DEFAULT 0,
                    is_active   INTEGER NOT NULL DEFAULT 1,
                    created_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_by  TEXT    DEFAULT ''
                );");

            // ---- サブ分類の検出キーワード ----
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS workload_subgroup_keywords (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    subgroup_id INTEGER NOT NULL,
                    keyword     TEXT    NOT NULL,
                    priority    INTEGER NOT NULL DEFAULT 0,
                    created_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_by  TEXT    DEFAULT ''
                );");

            // ---- 区分ごとのサブ分類モード ----
            // mode: 'common'（案件共通セットを使用・既定）／'custom'（区分個別セット）／'none'（サブ分類なし）
            // (project_id, category_id) で一意。行が存在しない区分は common として扱う
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS workload_subgroup_modes (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id  INTEGER NOT NULL,
                    category_id INTEGER NOT NULL,
                    mode        TEXT    NOT NULL DEFAULT 'common',
                    created_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_by  TEXT    DEFAULT ''
                );");

            // ---- インデックス ----
            // 参照は常に project_id / 親ID 起点のため、それぞれに索引を張る
            conn.Execute("CREATE INDEX IF NOT EXISTS idx_workload_categories_project ON workload_categories(project_id);");
            conn.Execute("CREATE INDEX IF NOT EXISTS idx_workload_cat_kw_category    ON workload_category_keywords(category_id);");
            conn.Execute("CREATE INDEX IF NOT EXISTS idx_workload_subgroups_project  ON workload_subgroups(project_id);");
            conn.Execute("CREATE INDEX IF NOT EXISTS idx_workload_sub_kw_subgroup    ON workload_subgroup_keywords(subgroup_id);");
            // モードは (project_id, category_id) で一意（重複行の混入をDBレベルで防止）
            conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS uq_workload_modes ON workload_subgroup_modes(project_id, category_id);");
        }
    }
}

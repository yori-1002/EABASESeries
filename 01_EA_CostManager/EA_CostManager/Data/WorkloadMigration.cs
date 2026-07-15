using Dapper;
using Microsoft.Data.Sqlite;

namespace EA_CostManager.Data
{
    /// <summary>
    /// ▼ 追加 [Sprint 7A]：工数表機能用テーブルのマイグレーション
    ///
    /// 【追加テーブル（6つ）】
    ///   workload_categories        … 業務区分（案件ごと・工数表のタブ）
    ///   workload_category_keywords … 業務区分の検出キーワード（detail への部分一致）
    ///   workload_subgroups         … サブ分類（案件単位で管理。適用先は links で決まる）
    ///   workload_subgroup_keywords … サブ分類の検出キーワード
    ///   workload_subgroup_links    … ▼ 追加 [Sprint 7D]：サブ分類 × 業務区分の適用先（多対多）
    ///   workload_subgroup_modes    … 区分ごとのサブ分類モード（common/none。行が無ければ common）
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
            // サブ分類は案件単位で管理し、どの区分タブに出すかは workload_subgroup_links で決める。
            // category_id は [Sprint 7D] 以前の名残（NULL=案件共通／値あり=区分専用）。
            // 現行コードは参照せず、新規行には NULL を入れる。
            // （SQLite は列削除が面倒なため、旧データ保全も兼ねて列自体は残している）
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

            // ---- サブ分類の適用先（サブ分類 × 業務区分の多対多） ----
            // ▼ 追加 [Sprint 7D]
            // 「この行があるサブ分類だけが、その区分タブに出る」というのが唯一のルール。
            // 区分をまたいで同じサブ分類を使いたい場合は、区分の数だけ行を作る。
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS workload_subgroup_links (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    subgroup_id INTEGER NOT NULL,
                    category_id INTEGER NOT NULL,
                    created_at  TEXT    DEFAULT (datetime('now','localtime')),
                    updated_by  TEXT    DEFAULT ''
                );");

            // ---- 区分ごとのサブ分類モード ----
            // mode: 'common'（サブ分類で分ける・既定）／'none'（サブ分類で分けない）
            // ▼ 修正 [Sprint 7D]：'custom' は廃止（適用先は links で区分ごとに指定するため、
            //   共通／専用という区別自体が不要になった）。旧データに残る 'custom' は
            //   'none' 以外＝「分ける」として扱われるため、読み込み側の互換性は保たれる。
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
            // 適用先は (subgroup_id, category_id) で一意（同じ組み合わせの二重登録をDBレベルで防止）
            conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS uq_workload_sub_links ON workload_subgroup_links(subgroup_id, category_id);");
            conn.Execute("CREATE INDEX IF NOT EXISTS idx_workload_sub_links_category ON workload_subgroup_links(category_id);");
            // モードは (project_id, category_id) で一意（重複行の混入をDBレベルで防止）
            conn.Execute("CREATE UNIQUE INDEX IF NOT EXISTS uq_workload_modes ON workload_subgroup_modes(project_id, category_id);");

            backfill_subgroup_links(conn);   // ▼ 追加 [Sprint 7D]
        }

        /// <summary>
        /// ▼ 追加 [Sprint 7D]：旧構造（category_id による共通／専用セット）から
        /// 適用先リンク（workload_subgroup_links）へ一度だけ変換する。
        ///
        /// 【変換ルール】旧構造で実際に表示されていた組み合わせをそのまま行にする。
        ///   ・category_id に値がある（区分専用）サブ分類 … その区分にリンク
        ///   ・category_id が NULL（案件共通）のサブ分類  … 同一案件で mode が
        ///     'custom' / 'none' 以外（＝common）の区分すべてにリンク
        ///   これにより、変換の前後で工数表の集計結果は一致する。
        ///
        /// 【1回だけ実行する理由】
        /// 変換後にユーザーがチェックを外して適用先を減らすことは正常な操作である。
        /// 毎回流すと、その「外した」設定を旧データから復活させてしまうため、
        /// app_settings のフラグで実行済みを記録し、2回目以降はスキップする。
        /// </summary>
        private static void backfill_subgroup_links(SqliteConnection conn)
        {
            const string FLAG_KEY = "workload_subgroup_links_migrated";

            // app_settings は database_manager が作成するが、NAS DB では未作成のことがある。
            // 実行済みフラグの置き場所として必要なため、ここでも冪等に用意する。
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS app_settings (
                    key         TEXT PRIMARY KEY,
                    value       TEXT,
                    updated_at  TEXT DEFAULT (datetime('now','localtime')),
                    updated_by  TEXT DEFAULT ''
                )");

            string? done = conn.ExecuteScalar<string>(
                "SELECT value FROM app_settings WHERE key = @k", new { k = FLAG_KEY });
            if (done == "1") return;

            using var tx = conn.BeginTransaction();

            // 旧「区分専用」サブ分類 → その区分へのリンク
            conn.Execute(@"
                INSERT OR IGNORE INTO workload_subgroup_links (subgroup_id, category_id)
                SELECT s.id, s.category_id
                  FROM workload_subgroups s
                  JOIN workload_categories c ON c.id = s.category_id
                 WHERE s.category_id IS NOT NULL", transaction: tx);

            // 旧「案件共通」サブ分類 → 同一案件の common モードの区分すべてへリンク
            conn.Execute(@"
                INSERT OR IGNORE INTO workload_subgroup_links (subgroup_id, category_id)
                SELECT s.id, c.id
                  FROM workload_subgroups s
                  JOIN workload_categories c ON c.project_id = s.project_id
                  LEFT JOIN workload_subgroup_modes m
                         ON m.project_id = c.project_id AND m.category_id = c.id
                 WHERE s.category_id IS NULL
                   AND COALESCE(m.mode, 'common') = 'common'", transaction: tx);

            // 廃止した 'custom' を 'common' に寄せる（以降 mode は common / none のみ）。
            // 'custom' の区分が使っていた専用サブ分類は上の1本目で既にリンク済みのため、
            // ここで common にしても表示内容は変わらない。
            conn.Execute("UPDATE workload_subgroup_modes SET mode = 'common' WHERE mode = 'custom'",
                transaction: tx);

            conn.Execute(@"
                INSERT INTO app_settings (key, value) VALUES (@k, '1')
                ON CONFLICT(key) DO UPDATE SET value = '1',
                    updated_at = datetime('now','localtime')",
                new { k = FLAG_KEY }, tx);

            tx.Commit();
        }
    }
}

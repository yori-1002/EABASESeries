using Dapper;
using Microsoft.Data.Sqlite;

namespace EA_CostManager.Data
{
    /// <summary>
    /// ▼ 追加 [Sprint 7E]：画面の表示状態（折りたたみ）を覚えておくためのテーブル
    ///
    /// 【追加テーブル（2つ）】
    ///   month_collapse_states    … 原価集計・月度の折りたたみ状態（ユーザー×絞り込みタブ×月度）
    ///   workload_collapse_states … 工数表・サブ分類グループの折りたたみ状態（ユーザー×案件×区分タブ）
    ///
    /// 【month_collapse_states を今になって作る理由】
    /// 読み書きのコード（filter_tab_view_model の restore_collapse_states_async /
    /// save_collapse_state_async / save_all_collapse_states_async）は以前から存在したが、
    /// CREATE TABLE がどこにも無く、実DBにもテーブルが存在しなかった。
    /// 両メソッドは例外を握り潰す（Debug.WriteLine のみ）ため、
    /// 「月度を折りたたんで再起動すると展開に戻る」状態に無言でなっていた。
    /// ここで作ることで、既存の読み書きコードがそのまま機能するようになる。
    ///
    /// 【設計】
    /// ・いずれも「利用者ごとの見た目の好み」であり、業務データではない。
    ///   ただし pc_user_id を持たせることで、同じ利用者が別PCでも同じ状態で開ける
    ///  （＝共有DB上に置く。user_settings.json はPC単位のためこの用途には使わない）。
    /// ・既定は「展開」。折りたたんだものだけを is_collapsed=1 として持つ。
    ///
    /// 【運用】
    /// ・CREATE TABLE IF NOT EXISTS のため冪等（何度実行しても安全）。
    /// ・折りたたみ状態は database_manager.create_connection() 経由で読み書きする。
    ///   この接続先は NAS 有効時に NAS DB を指すため、ローカルDB・NAS DB の両方で
    ///   実行する必要がある（App.xaml.cs の2箇所から呼ぶこと）。
    ///   ※ V2Migration はローカルDBにしか走らないため、そちらには置けない。
    /// </summary>
    public static class ViewStateMigration
    {
        /// <summary>表示状態テーブルを作成する（冪等）。</summary>
        /// <param name="conn">対象DBへのオープン済み接続（ローカル/NAS どちらも可）</param>
        public static void migrate(SqliteConnection conn)
        {
            // ---- 原価集計：月度の折りたたみ ----
            // UNIQUE は INSERT OR REPLACE（UPSERT）が成立するために必須。
            // 無いと同じ月度の行が増え続け、復元時に重複する。
            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS month_collapse_states (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    pc_user_id    INTEGER NOT NULL,
                    filter_tab_id INTEGER NOT NULL,
                    fiscal_month  TEXT    NOT NULL,
                    is_collapsed  INTEGER NOT NULL DEFAULT 0,
                    updated_at    TEXT    DEFAULT (datetime('now','localtime'))
                );");
            conn.Execute(@"
                CREATE UNIQUE INDEX IF NOT EXISTS uq_month_collapse
                    ON month_collapse_states(pc_user_id, filter_tab_id, fiscal_month);");

            // ---- 工数表：サブ分類グループの折りたたみ ----
            // 「案件 × 区分タブ × サブ分類」の1グループごとに持つ。
            // category_id は「全体」「未分類」タブでは、subgroup_id は「（サブ未分類）」の
            // グループでは、それぞれ実IDを持たない。SQLite の UNIQUE は NULL 同士を
            // 別物として扱い重複を防げないため、NULL の代わりに -1 を入れる
            //（実在のIDは常に正の値のため衝突しない）。
            //
            // ▼ 修正 [Sprint 7E-2]：subgroup_id を追加した。
            //   初版は区分タブ単位（サブ分類ごとの区別なし）だったため、
            //   グループ見出しを個別に開閉した状態を覚えられなかった。
            //   旧定義のテーブルが残っている場合は作り直す。未リリースであり、
            //   保持しているのが折りたたみの好みだけなので作り直して差し支えない。
            bool table_exists = conn.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='workload_collapse_states'") > 0;
            if (table_exists)
            {
                bool has_subgroup_id = conn.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM pragma_table_info('workload_collapse_states') WHERE name='subgroup_id'") > 0;
                if (!has_subgroup_id)
                    conn.Execute("DROP TABLE workload_collapse_states");
            }

            conn.Execute(@"
                CREATE TABLE IF NOT EXISTS workload_collapse_states (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    pc_user_id   INTEGER NOT NULL,
                    project_id   INTEGER NOT NULL,
                    category_id  INTEGER NOT NULL,
                    subgroup_id  INTEGER NOT NULL,
                    is_collapsed INTEGER NOT NULL DEFAULT 0,
                    updated_at   TEXT    DEFAULT (datetime('now','localtime'))
                );");
            conn.Execute(@"
                CREATE UNIQUE INDEX IF NOT EXISTS uq_workload_collapse
                    ON workload_collapse_states(pc_user_id, project_id, category_id, subgroup_id);");
        }

        /// <summary>
        /// 工数表の「全体」「未分類」タブのように区分IDを持たないタブを表す値。
        /// UNIQUE 制約を効かせるため NULL の代わりに用いる。
        /// </summary>
        public const int NO_CATEGORY = -1;

        /// <summary>
        /// 「（サブ未分類）」グループのようにサブ分類IDを持たないグループを表す値。
        /// NO_CATEGORY と同じ理由で NULL の代わりに用いる。
        /// </summary>
        public const int NO_SUBGROUP = -1;
    }
}

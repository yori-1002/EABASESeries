using System.Data;
using System.Threading.Tasks;
using Dapper;

namespace EA_CostManager.Data
{
    /// <summary>
    /// Sprint 5E: category_groupsテーブルのマイグレーション
    /// 区分コードの結合設定（親→子の関係）を保存する
    /// </summary>
    public static class CategoryGroupMigration
    {
        public static async Task RunAsync(IDbConnection conn)
        {
            // ── category_groupsテーブル作成 ──────────────────────────────────────
            // parent_code : 原価集計画面に表示される代表区分コード
            // child_code  : 合算対象の区分コード（元データは変更しない）
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS category_groups (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    parent_code TEXT NOT NULL,
                    child_code  TEXT NOT NULL,
                    created_at  TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                    UNIQUE(parent_code, child_code)
                )");

            // 親コード検索用インデックス
            await conn.ExecuteAsync(
                "CREATE INDEX IF NOT EXISTS idx_category_groups_parent ON category_groups(parent_code)");
        }
    }
}

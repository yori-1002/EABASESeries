using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.Services
{
    /// <summary>区分結合設定の1レコード（UI表示用）</summary>
    public class category_group_item
    {
        public int    id          { get; set; }
        public string parent_code { get; set; } = "";
        public string child_code  { get; set; } = "";
        public string created_at  { get; set; } = "";
    }

    /// <summary>
    /// Sprint 5E: 区分コード結合設定の管理サービス
    /// ・元データ（daily_reports）は変更しない
    /// ・表示・集計レイヤーでのみ合算を行う
    /// </summary>
    public static class CategoryGroupService
    {
        // ── 参照系 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 全結合設定を取得する（管理画面の一覧表示用）
        /// </summary>
        public static async Task<IEnumerable<category_group_item>> get_all_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                return await conn.QueryAsync<category_group_item>(@"
                    SELECT id, parent_code, child_code, created_at
                    FROM   category_groups
                    ORDER  BY parent_code, child_code");
            }
            catch { return Array.Empty<category_group_item>(); }
        }

        /// <summary>
        /// 指定した親コードの子コード一覧を取得する
        /// project_cost_view_model.load_records_async() から呼び出す
        /// </summary>
        public static async Task<IEnumerable<string>> get_child_codes_async(string parent_code)
        {
            try
            {
                using var conn = database_manager.create_connection();
                return await conn.QueryAsync<string>(@"
                    SELECT child_code
                    FROM   category_groups
                    WHERE  parent_code = @parent",
                    new { parent = parent_code });
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>
        /// 指定コードが子コードとして登録されているかチェックする
        /// （子コードは独立タブとして表示しないためのチェック用）
        /// </summary>
        public static async Task<bool> is_child_code_async(string code)
        {
            try
            {
                using var conn = database_manager.create_connection();
                var count = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM category_groups WHERE child_code = @code",
                    new { code });
                return count > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 全ての子コードセットを取得する（cost_view_modelのタブ構築用・一括取得で効率化）
        /// </summary>
        public static async Task<System.Collections.Generic.HashSet<string>> get_all_child_codes_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var codes = await conn.QueryAsync<string>(
                    "SELECT child_code FROM category_groups");
                return new System.Collections.Generic.HashSet<string>(codes,
                    StringComparer.OrdinalIgnoreCase);
            }
            catch { return new System.Collections.Generic.HashSet<string>(); }
        }

        // ── 更新系 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 結合設定を追加する
        /// </summary>
        public static async Task<(bool success, string error)> add_async(
            string parent_code, string child_code)
        {
            // バリデーション
            if (string.IsNullOrWhiteSpace(parent_code))
                return (false, "親区分コードを入力してください");
            if (string.IsNullOrWhiteSpace(child_code))
                return (false, "子区分コードを入力してください");
            if (parent_code.Trim() == child_code.Trim())
                return (false, "親コードと子コードが同じです");

            try
            {
                using var conn = database_manager.create_connection();

                // 循環参照チェック：追加しようとする子が既に親として登録されていないか
                var child_as_parent = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM category_groups WHERE parent_code = @child",
                    new { child = child_code.Trim() });
                if (child_as_parent > 0)
                    return (false, $"「{child_code}」は既に親コードとして登録されています。循環参照になるため追加できません");

                // 追加
                await conn.ExecuteAsync(@"
                    INSERT OR IGNORE INTO category_groups (parent_code, child_code)
                    VALUES (@parent, @child)",
                    new { parent = parent_code.Trim(), child = child_code.Trim() });

                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// 結合設定を削除する
        /// </summary>
        public static async Task<(bool success, string error)> delete_async(int id)
        {
            try
            {
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(
                    "DELETE FROM category_groups WHERE id = @id",
                    new { id });
                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}

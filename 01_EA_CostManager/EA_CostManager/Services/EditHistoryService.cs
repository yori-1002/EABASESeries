using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.Services
{
    /// <summary>
    /// Undo/Redo の対象となる1件の編集スナップショット
    /// 属性変更・区分コード変更・現場名/詳細変更を記録する
    /// </summary>
    public class project_edit_snapshot
    {
        public int project_id { get; set; }
        public string tab_name { get; set; } = "";  // 表示用（"EA-001_○○工事" 等）

        // 変更前の値
        public string old_category_code { get; set; } = "";
        public string old_site_name { get; set; } = "";
        public string old_detail { get; set; } = "";
        public string old_attribute { get; set; } = "";
        public string old_tab_color { get; set; } = "";

        // 変更後の値
        public string new_category_code { get; set; } = "";
        public string new_site_name { get; set; } = "";
        public string new_detail { get; set; } = "";
        public string new_attribute { get; set; } = "";
        public string new_tab_color { get; set; } = "";
    }

    /// <summary>
    /// Sprint 5G: 現場編集の Undo/Redo 履歴を管理する静的サービス
    /// ・対象操作：属性変更・区分コード変更・現場名/詳細変更
    /// ・履歴上限：10件（MAX_HISTORY）
    /// ・DBへの書き込みは Undo/Redo 実行時に行う
    /// </summary>
    public static class EditHistoryService
    {
        private const int MAX_HISTORY = 10;

        // Undo スタック（新しい変更が先頭）
        private static readonly Stack<project_edit_snapshot> _undo_stack = new();
        // Redo スタック（Undo した変更が積まれる）
        private static readonly Stack<project_edit_snapshot> _redo_stack = new();

        // ── 状態プロパティ ────────────────────────────────────────────────────

        public static bool can_undo => _undo_stack.Count > 0;
        public static bool can_redo => _redo_stack.Count > 0;

        // ── 操作記録 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 編集操作をUndoスタックに積む
        /// 新しい操作が発生したらRedoスタックはクリアする
        /// </summary>
        public static void push(project_edit_snapshot snapshot)
        {
            _undo_stack.Push(snapshot);

            // 上限を超えたら古いものを捨てる（Stackは直接削除できないのでリスト変換）
            if (_undo_stack.Count > MAX_HISTORY)
            {
                var items = new List<project_edit_snapshot>(_undo_stack);
                _undo_stack.Clear();
                // 新しい方から MAX_HISTORY 件だけ積み直す
                for (int i = MAX_HISTORY - 1; i >= 0; i--)
                    _undo_stack.Push(items[i]);
            }

            // 新しい操作が来たらRedoはクリア
            _redo_stack.Clear();
        }

        // ── Undo ────────────────────────────────────────────────────────────

        /// <summary>
        /// Undo（元に戻す）：最新のスナップショットの old 値をDBに書き戻す
        /// </summary>
        /// <returns>影響した project_id（呼び出し元でリロードに使用）/ -1=失敗</returns>
        public static async Task<int> undo_async()
        {
            // ▼ 追加 [Sprint 8 / Phase 0]：読取専用モードでは Undo（projects/cost_records の書き戻し）を行わない
            if (UserSession.is_read_only) return -1;
            if (!can_undo) return -1;

            var snap = _undo_stack.Pop();
            bool ok = await apply_to_db_async(snap, use_old: true);
            if (!ok) { _undo_stack.Push(snap); return -1; } // 失敗したら戻す

            _redo_stack.Push(snap);
            return snap.project_id;
        }

        // ── Redo ────────────────────────────────────────────────────────────

        /// <summary>
        /// Redo（やり直し）：スナップショットの new 値をDBに書き戻す
        /// </summary>
        public static async Task<int> redo_async()
        {
            // ▼ 追加 [Sprint 8 / Phase 0]：読取専用モードでは Redo（projects/cost_records の書き戻し）を行わない
            if (UserSession.is_read_only) return -1;
            if (!can_redo) return -1;

            var snap = _redo_stack.Pop();
            bool ok = await apply_to_db_async(snap, use_old: false);
            if (!ok) { _redo_stack.Push(snap); return -1; }

            _undo_stack.Push(snap);
            return snap.project_id;
        }

        // ── DB書き込み ────────────────────────────────────────────────────────

        private static async Task<bool> apply_to_db_async(
            project_edit_snapshot snap, bool use_old)
        {
            try
            {
                string cat = use_old ? snap.old_category_code : snap.new_category_code;
                string site = use_old ? snap.old_site_name : snap.new_site_name;
                string det = use_old ? snap.old_detail : snap.new_detail;
                string attr = use_old ? snap.old_attribute : snap.new_attribute;
                string color = use_old ? snap.old_tab_color : snap.new_tab_color;

                using var conn = database_manager.create_connection();

                // projects テーブルを更新
                // ★v0.9.7修正：updated_at カラムが存在しない可能性があるため
                //   COALESCE+IFNULL は使わず、updated_at を含むSETと含まないSETを切り替える
                //   テストDBクリーン後にMigrationが完全実行されていない環境への保険
                int affected = await conn.ExecuteAsync(@"
                    UPDATE projects
                    SET category_code = @cat,
                        site_name     = @site,
                        detail        = @det,
                        attribute     = @attr,
                        tab_color     = @color
                    WHERE id = @id",
                    new { cat, site, det, attr, color, id = snap.project_id });

                // 影響行数0件は警告だが致命的エラーではない
                if (affected == 0)
                    System.Diagnostics.Debug.WriteLine(
                        $"[EditHistory] 警告：UPDATE対象なし project_id={snap.project_id}");

                // 区分コードが変わった場合は cost_records も連動更新
                string old_c = use_old ? snap.new_category_code : snap.old_category_code;
                string new_c = cat;
                if (old_c != new_c && !string.IsNullOrEmpty(old_c))
                {
                    await conn.ExecuteAsync(@"
                        UPDATE cost_records
                        SET category_code = @new_cat
                        WHERE category_code = @old_cat",
                        new { new_cat = new_c, old_cat = old_c });
                }

                return true;
            }
            catch (Exception ex)
            {
                // ★v0.9.7修正：例外を握りつぶさずデバッグ出力する
                //   旧版では catch{} で完全に隠蔽されていたためUndo失敗の原因が追えなかった
                //   System.Diagnostics.Debug.WriteLine は Visual Studio の出力ウィンドウに表示される
                System.Diagnostics.Debug.WriteLine(
                    $"[EditHistory] Undo/Redo DB更新失敗: {ex.GetType().Name} : {ex.Message}");
                return false;
            }
        }

        // ── クリア ─────────────────────────────────────────────────────────

        /// <summary>履歴を全クリア（現場アーカイブ等の操作後に呼ぶ）</summary>
        public static void clear_all()
        {
            _undo_stack.Clear();
            _redo_stack.Clear();
        }
    }
}
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.ViewModels
{
    // ---- アーカイブ済み現場の表示用行モデル ----
    public class archived_project_row
    {
        public int id { get; set; }
        public string category_code { get; set; } = "";
        public string site_name { get; set; } = "";
        public string detail { get; set; } = "";
        public string attribute { get; set; } = "";
        // タブ表示名（重複なら category_code のみ）
        public string display_name =>
            string.IsNullOrWhiteSpace(site_name) || site_name == category_code
                ? category_code
                : $"{category_code}_{site_name}";
    }

    /// <summary>
    /// 設定画面 > アーカイブ済み現場 ViewModel
    /// is_active=0 の現場一覧を表示し、復元・完全削除を行う
    /// 完全削除は関連する cost_records も含めて全件削除する（不可逆）
    /// </summary>
    public class master_archived_projects_view_model : base_view_model
    {
        public ObservableCollection<archived_project_row> archived_projects { get; } = new();

        private string _status = "";
        public string status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        private archived_project_row? _selected_row;
        public archived_project_row? selected_row
        {
            get => _selected_row;
            set => SetProperty(ref _selected_row, value);
        }

        public ICommand restore_command { get; }
        public ICommand delete_permanent_command { get; }

        public master_archived_projects_view_model()
        {
            restore_command = new RelayCommand<archived_project_row?>(
                async row => await restore_async(row));
            delete_permanent_command = new RelayCommand<archived_project_row?>(
                async row => await delete_permanent_async(row));
            _ = load_async();
        }

        // ---- アーカイブ済み現場を全件読み込み ----
        public async Task load_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var rows = await conn.QueryAsync<archived_project_row>(@"
                    SELECT id, category_code, site_name, detail, attribute
                    FROM projects
                    WHERE is_active = 0
                    ORDER BY id");
                archived_projects.Clear();
                foreach (var r in rows)
                    archived_projects.Add(r);
                status = archived_projects.Count == 0
                    ? "アーカイブ済みの現場はありません"
                    : "";
            }
            catch (Exception ex)
            {
                status = $"❌ 読み込みエラー：{ex.Message}";
            }
        }

        // ---- 現場を復元（is_active=1 に戻す） ----
        private async Task restore_async(archived_project_row? row)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (row == null) return;

            var result = MessageBox.Show(
                $"「{row.display_name}」を復元します。\n原価集計画面に再表示されます。",
                "復元確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;

            try
            {
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(
                    "UPDATE projects SET is_active = 1 WHERE id = @id",
                    new { row.id });
                archived_projects.Remove(row);
                status = $"✅ 「{row.display_name}」を復元しました";
            }
            catch (Exception ex)
            {
                status = $"❌ 復元エラー：{ex.Message}";
            }
        }

        // ---- 現場を完全削除（projects + 関連cost_records を削除、不可逆） ----
        private async Task delete_permanent_async(archived_project_row? row)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (row == null) return;

            // 2段階確認（完全削除は慎重に）
            var first = MessageBox.Show(
                $"「{row.display_name}」を完全に削除します。\n関連する原価データもすべて削除されます。\n\nこの操作は元に戻せません。続けますか？",
                "完全削除 - 確認①",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (first != MessageBoxResult.OK) return;

            var second = MessageBox.Show(
                $"本当に削除しますか？\n「{row.display_name}」のすべてのデータが失われます。",
                "完全削除 - 最終確認②",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (second != MessageBoxResult.OK) return;

            try
            {
                using var conn = database_manager.create_connection();
                using var tx = conn.BeginTransaction();
                try
                {
                    // 関連する原価レコードを削除
                    await conn.ExecuteAsync(
                        "DELETE FROM cost_records WHERE category_code = @cat",
                        new { cat = row.category_code }, tx);

                    // 関連する絞り込みタブを削除
                    await conn.ExecuteAsync(
                        "DELETE FROM cost_filter_tabs WHERE project_id = @id",
                        new { row.id }, tx);

                    // 現場本体を削除
                    await conn.ExecuteAsync(
                        "DELETE FROM projects WHERE id = @id",
                        new { row.id }, tx);

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }

                archived_projects.Remove(row);
                status = $"✅ 「{row.display_name}」を完全に削除しました";
            }
            catch (Exception ex)
            {
                status = $"❌ 削除エラー：{ex.Message}";
            }
        }
    }
}
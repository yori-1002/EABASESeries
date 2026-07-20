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
    // ---- 旧形式氏名変換マスタ編集用行モデル ----
    public class legacy_name_row
    {
        public int id { get; set; }
        public string legacy_name { get; set; } = "";    // 旧形式フルネーム（Excelに記載の表記）
        public string display_name { get; set; } = "";  // 変換後の略称
        public bool is_new => id == 0;
    }

    /// <summary>
    /// 設定画面 > 旧形式氏名変換 ViewModel
    /// legacy_name_map テーブルの一覧・追加・削除を管理する
    /// ベトナム人社員など、旧形式日報でのフルネーム → 現行略称の変換に使用
    /// </summary>
    public class master_legacy_name_view_model : base_view_model
    {
        public ObservableCollection<legacy_name_row> legacy_names { get; } = new();

        private string _status = "";
        public string status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        // ---- 新規追加用入力フィールド ----
        private string _new_legacy_name = "";
        public string new_legacy_name
        {
            get => _new_legacy_name;
            set => SetProperty(ref _new_legacy_name, value);
        }

        private string _new_display_name = "";
        public string new_display_name
        {
            get => _new_display_name;
            set => SetProperty(ref _new_display_name, value);
        }

        public ICommand add_command { get; }
        public ICommand delete_command { get; }

        public master_legacy_name_view_model()
        {
            add_command = new RelayCommand(async () => await add_async());
            delete_command = new RelayCommand<legacy_name_row?>(async row => await delete_async(row));
            _ = load_async();
        }

        public async Task load_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var rows = await conn.QueryAsync<legacy_name_row>(
                    "SELECT id, legacy_name, display_name FROM legacy_name_map ORDER BY id");
                legacy_names.Clear();
                foreach (var r in rows)
                    legacy_names.Add(r);
                status = "";
            }
            catch (Exception ex)
            {
                status = $"❌ 読み込みエラー：{ex.Message}";
            }
        }

        // ---- 新規マッピングを追加 ----
        private async Task add_async()
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (string.IsNullOrWhiteSpace(new_legacy_name) || string.IsNullOrWhiteSpace(new_display_name))
            {
                status = "⚠️ 旧形式氏名と略称の両方を入力してください";
                return;
            }
            try
            {
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(@"
                    INSERT OR IGNORE INTO legacy_name_map (legacy_name, display_name)
                    VALUES (@legacy_name, @display_name)",
                    new { legacy_name = new_legacy_name.Trim(), display_name = new_display_name.Trim() });

                new_legacy_name = "";
                new_display_name = "";
                await load_async();
                status = "✅ 追加しました";
            }
            catch (Exception ex)
            {
                status = $"❌ 追加エラー：{ex.Message}";
            }
        }

        // ---- マッピングを削除 ----
        private async Task delete_async(legacy_name_row? row)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (row == null) return;

            var result = MessageBox.Show(
                $"「{row.legacy_name} → {row.display_name}」の変換設定を削除します。",
                "削除確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK) return;

            try
            {
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync("DELETE FROM legacy_name_map WHERE id = @id", new { row.id });
                legacy_names.Remove(row);
                status = "✅ 削除しました";
            }
            catch (Exception ex)
            {
                status = $"❌ 削除エラー：{ex.Message}";
            }
        }
    }
}
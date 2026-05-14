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
    // ---- 機材マスタ編集用行モデル ----
    public class equipment_row
    {
        public int id { get; set; }
        public string equipment_name { get; set; } = "";
        public decimal daily_rate { get; set; } = 0;
        public bool is_active { get; set; } = true;
        public string note { get; set; } = "";
        public bool is_new => id == 0;
    }

    /// <summary>
    /// 設定画面 > 機材マスタ ViewModel
    /// equipment_rates テーブルの一覧・追加・編集・削除を管理する
    /// daily_rate=0 の旧形式日報損料計算のマスタとして機能する
    /// </summary>
    public class master_equipment_view_model : base_view_model
    {
        public ObservableCollection<equipment_row> equipment_list { get; } = new();

        private string _status = "";
        public string status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        private equipment_row? _selected_row;
        public equipment_row? selected_row
        {
            get => _selected_row;
            set => SetProperty(ref _selected_row, value);
        }

        public ICommand add_command { get; }
        public ICommand save_command { get; }
        public ICommand delete_command { get; }
        public ICommand toggle_active_command { get; }

        public master_equipment_view_model()
        {
            add_command = new RelayCommand(add_new_row);
            save_command = new RelayCommand(async () => await save_async());
            delete_command = new RelayCommand<equipment_row?>(async row => await delete_async(row));
            toggle_active_command = new RelayCommand<equipment_row?>(async row =>
            {
                if (row == null || row.is_new) return;
                await toggle_active_async(row);
            });
            _ = load_async();
        }

        public async Task load_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var rows = await conn.QueryAsync<equipment_row>(
                    "SELECT id, equipment_name, daily_rate, is_active, note FROM equipment_rates ORDER BY id");
                equipment_list.Clear();
                foreach (var r in rows)
                    equipment_list.Add(r);
                status = "";
            }
            catch (Exception ex)
            {
                status = $"❌ 読み込みエラー：{ex.Message}";
            }
        }

        private void add_new_row()
        {
            equipment_list.Add(new equipment_row { id = 0, is_active = true });
        }

        private async Task save_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                int saved = 0;
                foreach (var row in equipment_list)
                {
                    if (string.IsNullOrWhiteSpace(row.equipment_name)) continue;
                    if (row.is_new)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT OR IGNORE INTO equipment_rates (equipment_name, daily_rate, is_active, note)
                            VALUES (@equipment_name, @daily_rate, @is_active_int, @note)",
                            new { row.equipment_name, row.daily_rate, is_active_int = row.is_active ? 1 : 0, row.note });
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE equipment_rates SET
                                equipment_name = @equipment_name,
                                daily_rate     = @daily_rate,
                                is_active      = @is_active_int,
                                note           = @note
                            WHERE id = @id",
                            new { row.equipment_name, row.daily_rate, is_active_int = row.is_active ? 1 : 0, row.note, row.id });
                    }
                    saved++;
                }
                await load_async();
                status = $"✅ {saved}件を保存しました";
            }
            catch (Exception ex)
            {
                status = $"❌ 保存エラー：{ex.Message}";
            }
        }

        private async Task delete_async(equipment_row? row)
        {
            if (row == null) return;

            // 新規行はDBに存在しないのでコレクションから除くだけ
            if (row.is_new)
            {
                equipment_list.Remove(row);
                return;
            }

            var result = MessageBox.Show(
                $"「{row.equipment_name}」を削除します。\nこの操作は元に戻せません。",
                "削除確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK) return;

            try
            {
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync("DELETE FROM equipment_rates WHERE id = @id", new { row.id });
                equipment_list.Remove(row);
                status = $"✅ {row.equipment_name} を削除しました";
            }
            catch (Exception ex)
            {
                status = $"❌ 削除エラー：{ex.Message}";
            }
        }

        private async Task toggle_active_async(equipment_row row)
        {
            try
            {
                row.is_active = !row.is_active;
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(
                    "UPDATE equipment_rates SET is_active = @v WHERE id = @id",
                    new { v = row.is_active ? 1 : 0, row.id });
                status = $"✅ {row.equipment_name} の有効状態を変更しました";
            }
            catch (Exception ex)
            {
                status = $"❌ エラー：{ex.Message}";
            }
        }
    }
}
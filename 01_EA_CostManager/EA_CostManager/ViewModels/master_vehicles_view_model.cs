using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.ViewModels
{
    // ---- 車両マスタ編集用行モデル ----
    public class vehicle_row
    {
        public int id { get; set; }
        public string vehicle_name { get; set; } = "";
        public bool is_active { get; set; } = true;
        public bool is_new => id == 0;
    }

    /// <summary>
    /// 設定画面 > 車両マスタ ViewModel
    /// vehicles テーブルの一覧・追加・有効切替を管理する
    /// ※「なし」は日報入力の固定値として使用するため非表示（WHERE除外）
    /// </summary>
    public class master_vehicles_view_model : base_view_model
    {
        public ObservableCollection<vehicle_row> vehicles { get; } = new();

        private string _status = "";
        public string status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public ICommand add_command { get; }
        public ICommand save_command { get; }
        public ICommand toggle_active_command { get; }

        public master_vehicles_view_model()
        {
            add_command = new RelayCommand(add_new_row);
            save_command = new RelayCommand(async () => await save_async());
            toggle_active_command = new RelayCommand<vehicle_row?>(async row =>
            {
                if (row == null || row.is_new) return;
                await toggle_active_async(row);
            });
            _ = load_async();
        }

        // ---- DBから読み込み（「なし」は除外して表示） ----
        public async Task load_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                // 「なし」は日報の固定値として内部使用するため設定画面には表示しない
                var rows = await conn.QueryAsync<vehicle_row>(
                    "SELECT id, vehicle_name, is_active FROM vehicles WHERE vehicle_name != 'なし' ORDER BY id");
                vehicles.Clear();
                foreach (var r in rows)
                    vehicles.Add(r);
                status = "";
            }
            catch (Exception ex)
            {
                status = $"❌ 読み込みエラー：{ex.Message}";
            }
        }

        private void add_new_row()
        {
            vehicles.Add(new vehicle_row { id = 0, is_active = true });
        }

        private async Task save_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                int saved = 0;
                foreach (var row in vehicles)
                {
                    if (string.IsNullOrWhiteSpace(row.vehicle_name)) continue;
                    if (row.is_new)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO vehicles (vehicle_name, is_active)
                            VALUES (@vehicle_name, @is_active_int)",
                            new { row.vehicle_name, is_active_int = row.is_active ? 1 : 0 });
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE vehicles SET vehicle_name = @vehicle_name, is_active = @is_active_int
                            WHERE id = @id",
                            new { row.vehicle_name, is_active_int = row.is_active ? 1 : 0, row.id });
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

        private async Task toggle_active_async(vehicle_row row)
        {
            try
            {
                row.is_active = !row.is_active;
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(
                    "UPDATE vehicles SET is_active = @v WHERE id = @id",
                    new { v = row.is_active ? 1 : 0, row.id });
                status = $"✅ {row.vehicle_name} の有効状態を変更しました";
            }
            catch (Exception ex)
            {
                status = $"❌ エラー：{ex.Message}";
            }
        }
    }
}
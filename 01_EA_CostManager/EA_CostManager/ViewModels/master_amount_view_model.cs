using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.ViewModels
{
    /// <summary>
    /// 設定画面 > 金額設定 ViewModel
    /// app_settings テーブルの単価・距離設定を管理する
    /// </summary>
    public class master_amount_view_model : base_view_model
    {
        // ---- 技師単価 ----
        private decimal _engineer_daily_rate = 34800;
        public decimal engineer_daily_rate
        {
            get => _engineer_daily_rate;
            set => SetProperty(ref _engineer_daily_rate, value);
        }

        // ---- 助手単価 ----
        private decimal _assistant_daily_rate = 28000;
        public decimal assistant_daily_rate
        {
            get => _assistant_daily_rate;
            set => SetProperty(ref _assistant_daily_rate, value);
        }

        // ---- 1日基準時間（人工計算用） ----
        private decimal _base_hours_per_day = 8;
        public decimal base_hours_per_day
        {
            get => _base_hours_per_day;
            set => SetProperty(ref _base_hours_per_day, value);
        }

        // ---- 下道交通費単価（円/km） ----
        private decimal _road_cost_per_km = 50;
        public decimal road_cost_per_km
        {
            get => _road_cost_per_km;
            set => SetProperty(ref _road_cost_per_km, value);
        }

        // ---- 高速交通費単価（円/km） ----
        private decimal _highway_cost_per_km = 100;
        public decimal highway_cost_per_km
        {
            get => _highway_cost_per_km;
            set => SetProperty(ref _highway_cost_per_km, value);
        }

        // ---- 高速→下道切替の往復距離上限（km） ----
        private decimal _highway_distance_cap = 250;
        public decimal highway_distance_cap
        {
            get => _highway_distance_cap;
            set => SetProperty(ref _highway_distance_cap, value);
        }

        // ---- ステータス ----
        private string _status = "";
        public string status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public ICommand save_command { get; }

        public master_amount_view_model()
        {
            save_command = new RelayCommand(async () => await save_async());
            _ = load_async();
        }

        // ---- app_settings から読み込み ----
        public async Task load_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                var settings = await conn.QueryAsync<(string key, string value)>(
                    "SELECT key, value FROM app_settings");
                var dict = new Dictionary<string, string>();
                foreach (var (key, value) in settings)
                    dict[key] = value;

                engineer_daily_rate = parse(dict, "engineer_daily_rate", 34800m);
                assistant_daily_rate = parse(dict, "assistant_daily_rate", 28000m);
                base_hours_per_day = parse(dict, "base_hours_per_day", 8m);
                road_cost_per_km = parse(dict, "road_cost_per_km", 50m);
                highway_cost_per_km = parse(dict, "highway_cost_per_km", 100m);
                highway_distance_cap = parse(dict, "highway_distance_cap", 250m);
                status = "";
            }
            catch (Exception ex)
            {
                status = $"❌ 読み込みエラー：{ex.Message}";
            }
        }

        // ---- app_settings に保存 ----
        private async Task save_async()
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            try
            {
                using var conn = database_manager.create_connection();

                var items = new Dictionary<string, string>
                {
                    { "engineer_daily_rate",  engineer_daily_rate.ToString() },
                    { "assistant_daily_rate", assistant_daily_rate.ToString() },
                    { "base_hours_per_day",   base_hours_per_day.ToString() },
                    { "road_cost_per_km",     road_cost_per_km.ToString() },
                    { "highway_cost_per_km",  highway_cost_per_km.ToString() },
                    { "highway_distance_cap", highway_distance_cap.ToString() },
                };

                foreach (var kv in items)
                {
                    await conn.ExecuteAsync(
                        "INSERT OR REPLACE INTO app_settings (key, value) VALUES (@k, @v)",
                        new { k = kv.Key, v = kv.Value });
                }

                status = "✅ 金額設定を保存しました（次回集計から反映されます）";
            }
            catch (Exception ex)
            {
                status = $"❌ 保存エラー：{ex.Message}";
            }
        }

        private static decimal parse(Dictionary<string, string> dict, string key, decimal fallback)
        {
            if (dict.TryGetValue(key, out string? val) && decimal.TryParse(val, out decimal d))
                return d;
            return fallback;
        }
    }
}
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
    // ---- 人員マスタ編集用行モデル ----
    // ★v0.9.7修正：INotifyPropertyChanged対応に変更し、job_type変更時に daily_rate を自動連動させる
    //   - 旧版：単純なPOCO（プロパティ変更通知なし）→ プルダウンで職種を切り替えても単価が変わらない
    //   - 新版：base_view_model継承＋setterで連動 → 「助手」選択で28000、「技師」選択で34800に自動切替
    //   ※ user_engineer_rate / user_assistant_rate はVMから注入される（app_settingsの値）
    public class employee_row : base_view_model
    {
        public int id { get; set; }

        // 氏名（バインディングはUpdateSourceTrigger=PropertyChangedで即時反映）
        private string _employee_name = "";
        public string employee_name
        {
            get => _employee_name;
            set => SetProperty(ref _employee_name, value);
        }

        // ★v0.9.7修正：デフォルトを「測量技師」→「技師」に変更（DBの集計ロジックに統一）
        // 値変更時に daily_rate を職種別のデフォルト値に自動更新する
        private string _job_type = "技師";
        public string job_type
        {
            get => _job_type;
            set
            {
                if (SetProperty(ref _job_type, value))
                {
                    // ▼ 職種変更時：daily_rateを職種別デフォルトに連動
                    //   親VMから設定された参照単価（_default_engineer_rate / _default_assistant_rate）を反映
                    //   静的フィールド経由で受け取るため、複数行のうちどの行でも最新の値が使われる
                    if (_rate_provider != null)
                    {
                        decimal new_rate = value.Contains("技師")
                            ? _rate_provider.engineer_rate
                            : _rate_provider.assistant_rate;
                        daily_rate = new_rate;
                    }
                }
            }
        }

        // 日額単価（プルダウン連動・手動編集の両方に対応）
        private decimal _daily_rate = 34800m;
        public decimal daily_rate
        {
            get => _daily_rate;
            set => SetProperty(ref _daily_rate, value);
        }

        // 在籍中フラグ
        private bool _is_active = true;
        public bool is_active
        {
            get => _is_active;
            set => SetProperty(ref _is_active, value);
        }

        // id=0 は新規追加行
        public bool is_new => id == 0;

        // ▼ ★v0.9.7追加：職種別デフォルト単価を提供するインターフェース（全行共有）
        // master_employees_view_model がコンストラクタで設定する
        // 静的にしているのは、DataGridでDapperが employee_row を生成するときも参照可能にするため
        public static i_rate_provider? _rate_provider { get; set; }
    }

    // ★v0.9.7追加：employee_row が職種別デフォルト単価を取得するためのインターフェース
    // master_employees_view_model 自身が実装し、自身のフィールドを返す
    public interface i_rate_provider
    {
        decimal engineer_rate { get; }
        decimal assistant_rate { get; }
    }

    /// <summary>
    /// 設定画面 > 人員マスタ ViewModel
    /// employees テーブルの一覧・追加・編集・在籍切替を管理する
    /// ★v0.9.7修正：i_rate_providerを実装し、employee_row の職種変更時に単価を連動させる
    /// </summary>
    public class master_employees_view_model : base_view_model, i_rate_provider
    {
        // ---- 職種選択肢（ComboBox用） ----
        // ▼▼▼ 修正（v0.9.6）：既存データの職種名と一致させる ▼▼▼
        // 旧実装：「測量技師」「測量助手」→ 集計時に「技師」「助手」で判定するため不一致だった
        // DB上の employees.job_type と cost_records の集計ロジック（Contains("技師")）に合わせる
        public string[] job_type_options { get; } = { "技師", "助手", "フライトオペレータ", "その他" };

        // ---- 一覧 ----
        public ObservableCollection<employee_row> employees { get; } = new();

        // ---- ステータス表示 ----
        private string _status = "";
        public string status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        // ---- 選択行（編集・削除対象） ----
        private employee_row? _selected_row;
        public employee_row? selected_row
        {
            get => _selected_row;
            set => SetProperty(ref _selected_row, value);
        }

        // ---- コマンド ----
        public ICommand add_command { get; }
        public ICommand save_command { get; }
        public ICommand toggle_active_command { get; }

        // ---- デフォルト単価（app_settingsから読み込み） ----
        private decimal _default_engineer_rate = 34800m;
        private decimal _default_assistant_rate = 28000m;

        // ★v0.9.7追加：i_rate_provider実装（employee_rowの職種変更時に参照される）
        // app_settingsからロードされた最新のデフォルト単価を返す
        public decimal engineer_rate => _default_engineer_rate;
        public decimal assistant_rate => _default_assistant_rate;

        public ICommand reset_to_default_command { get; }

        public master_employees_view_model()
        {
            // ★v0.9.7追加：employee_row が職種変更時に参照するデフォルト単価提供者を設定
            // 静的フィールド経由なので、全インスタンス共通で使われる（人員マスタは1画面につき1VMのため問題なし）
            employee_row._rate_provider = this;

            add_command = new RelayCommand(add_new_row);
            save_command = new RelayCommand(async () => await save_async());
            toggle_active_command = new RelayCommand<employee_row?>(async row =>
            {
                if (row == null || row.is_new) return;
                await toggle_active_async(row);
            });
            // デフォルトに戻すボタン：全員を app_settings の単価にリセットして保存
            reset_to_default_command = new RelayCommand(async () => await reset_to_default_async());
            _ = load_async();
        }

        // ---- DBから全件読み込み ----
        public async Task load_async()
        {
            try
            {
                using var conn = database_manager.create_connection();

                // app_settings からデフォルト単価を取得
                var settings = await conn.QueryAsync<(string key, string value)>(
                    "SELECT key, value FROM app_settings WHERE key IN ('engineer_daily_rate','assistant_daily_rate')");
                foreach (var (key, value) in settings)
                {
                    if (key == "engineer_daily_rate" && decimal.TryParse(value, out var er))
                        _default_engineer_rate = er;
                    if (key == "assistant_daily_rate" && decimal.TryParse(value, out var ar))
                        _default_assistant_rate = ar;
                }

                var rows = await conn.QueryAsync<employee_row>(@"
                    SELECT id, employee_name, job_type, daily_rate, is_active
                    FROM employees
                    WHERE employee_name NOT LIKE '% %'   -- 半角スペースを含む旧形式フルネームを除外
                      AND employee_name NOT LIKE '%　%'  -- 全角スペースを含む旧形式フルネームを除外
                    ORDER BY id");

                employees.Clear();
                foreach (var r in rows)
                {
                    // daily_rate = 0 の場合は app_settings の値を表示用に補完する
                    // （DBには0が入ったままで、保存ボタンで確定する）
                    if (r.daily_rate == 0)
                        r.daily_rate = r.job_type.Contains("技師") ? _default_engineer_rate : _default_assistant_rate;
                    employees.Add(r);
                }
                status = "";
            }
            catch (Exception ex)
            {
                status = $"❌ 読み込みエラー：{ex.Message}";
            }
        }

        // ---- 新規行を末尾に追加 ----
        // ★v0.9.7修正：デフォルトを「技師・34800」に統一
        //   - 旧版：employee_rowの初期値「測量技師」「34800」のまま追加 → 集計時に職種不一致でカウントされない
        //   - 新版：明示的に「技師」をセット → daily_rateも自動的にデフォルト値（_default_engineer_rate）になる
        private void add_new_row()
        {
            var new_row = new employee_row { id = 0, is_active = true };
            // job_type を明示セットすることで setter が走り daily_rate も連動更新される
            new_row.job_type = "技師";
            employees.Add(new_row);
        }

        // ---- 全行を保存（INSERT or UPDATE） ----
        private async Task save_async()
        {
            try
            {
                using var conn = database_manager.create_connection();
                int saved = 0;

                foreach (var row in employees)
                {
                    if (string.IsNullOrWhiteSpace(row.employee_name)) continue;

                    if (row.is_new)
                    {
                        // 新規追加
                        await conn.ExecuteAsync(@"
                            INSERT INTO employees (employee_name, job_type, daily_rate, is_active, updated_at)
                            VALUES (@employee_name, @job_type, @daily_rate, @is_active_int,
                                    datetime('now','localtime'))",
                            new
                            {
                                row.employee_name,
                                row.job_type,
                                row.daily_rate,
                                is_active_int = row.is_active ? 1 : 0
                            });
                    }
                    else
                    {
                        // 既存更新
                        await conn.ExecuteAsync(@"
                            UPDATE employees SET
                                employee_name = @employee_name,
                                job_type      = @job_type,
                                daily_rate    = @daily_rate,
                                is_active     = @is_active_int,
                                updated_at    = datetime('now','localtime')
                            WHERE id = @id",
                            new
                            {
                                row.employee_name,
                                row.job_type,
                                row.daily_rate,
                                is_active_int = row.is_active ? 1 : 0,
                                row.id
                            });
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

        // ---- 全員の単価を app_settings のデフォルト値にリセットして保存 ----
        private async Task reset_to_default_async()
        {
            var result = System.Windows.MessageBox.Show(
                $"全員の日額単価をデフォルト値に戻します。\n技師：{_default_engineer_rate:#,##0}円 / 助手：{_default_assistant_rate:#,##0}円\n\n個別に設定した単価もリセットされます。",
                "デフォルトに戻す",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.OK) return;

            foreach (var row in employees)
            {
                row.daily_rate = row.job_type.Contains("技師")
                    ? _default_engineer_rate
                    : _default_assistant_rate;
            }
            await save_async();
            status = $"✅ 全員の単価をデフォルト値に戻しました";
        }

        // ---- 在籍状態のON/OFF切り替え ----
        private async Task toggle_active_async(employee_row row)
        {
            try
            {
                row.is_active = !row.is_active;
                using var conn = database_manager.create_connection();
                await conn.ExecuteAsync(
                    "UPDATE employees SET is_active = @v WHERE id = @id",
                    new { v = row.is_active ? 1 : 0, row.id });
                status = $"✅ {row.employee_name} の在籍状態を変更しました";
            }
            catch (Exception ex)
            {
                status = $"❌ エラー：{ex.Message}";
            }
        }
    }
}
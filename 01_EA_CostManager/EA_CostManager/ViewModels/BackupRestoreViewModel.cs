using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using EA_CostManager.Data;

namespace EA_CostManager.ViewModels
{
    /// <summary>バックアップ一覧の1件分</summary>
    public class backup_item
    {
        public string file_path { get; set; } = "";
        public string file_name { get; set; } = "";
        public string file_size { get; set; } = "";
        public string created_at { get; set; } = "";
        public string location { get; set; } = "";  // "NAS" / "ローカル"
        /// <summary>復元前の自動バックアップかどうか（.bakファイル）</summary>
        public bool is_safety_backup { get; set; } = false;
        /// <summary>表示用の種別テキスト</summary>
        public string kind_text => is_safety_backup ? "⚠ 復元前退避" : "定期";
    }

    /// <summary>
    /// Sprint 5F: バックアップ復元UIのViewModel
    /// NASバックアップ + ローカルバックアップの一覧表示・復元操作
    /// </summary>
    public class backup_restore_view_model : base_view_model
    {
        // ---- NASバックアップディレクトリ ----
        private const string NAS_BACKUP_DIR =
            @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\01_Backups\01_Cost Manager";

        // ---- バインディングプロパティ ----

        public ObservableCollection<backup_item> backup_items { get; } = new();

        private backup_item? _selected_item;
        public backup_item? selected_item
        {
            get => _selected_item;
            set
            {
                if (SetProperty(ref _selected_item, value))
                {
                    OnPropertyChanged(nameof(can_restore));
                    // ▼▼▼ RelayCommandのCanExecuteを手動で更新 ▼▼▼
                    (_restore_relay as CommunityToolkit.Mvvm.Input.RelayCommand)?.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>復元ボタンの活性判定</summary>
        public bool can_restore => _selected_item != null && !is_busy;

        // ---- コマンド ----
        public ICommand reload_command { get; }
        public ICommand restore_command { get; }
        private readonly CommunityToolkit.Mvvm.Input.RelayCommand _restore_relay;

        public backup_restore_view_model()
        {
            reload_command = new RelayCommand(async () => await load_async());

            // ▼▼▼ CanExecuteを別途保持してNotifyCanExecuteChangedで手動更新 ▼▼▼
            _restore_relay = new CommunityToolkit.Mvvm.Input.RelayCommand(
                async () => await restore_async(),
                () => can_restore);
            restore_command = _restore_relay;

            _ = load_async();
        }

        // ---- バックアップ一覧ロード ----

        /// <summary>NAS + ローカルのバックアップファイルを一覧取得する</summary>
        public async Task load_async()
        {
            is_busy = true;
            status_message = "";
            try
            {
                var items = await Task.Run(() =>
                {
                    var result = new System.Collections.Generic.List<backup_item>();

                    // NASバックアップを取得
                    if (Directory.Exists(NAS_BACKUP_DIR))
                    {
                        foreach (var f in Directory.GetFiles(NAS_BACKUP_DIR, "EA_CostManager_*.db")
                            .OrderByDescending(x => x))
                        {
                            var info = new FileInfo(f);
                            result.Add(make_item(info, "NAS"));
                        }
                    }

                    // ローカルバックアップを取得（DBと同フォルダの backups\）
                    string db_path = database_manager.get_db_path();
                    string local_backup_dir = Path.Combine(
                        Path.GetDirectoryName(db_path) ?? "", "backups");

                    if (Directory.Exists(local_backup_dir))
                    {
                        foreach (var f in Directory.GetFiles(local_backup_dir, "EA_CostManager_*.db")
                            .OrderByDescending(x => x))
                        {
                            var info = new FileInfo(f);
                            if (!result.Any(r => r.file_name == info.Name))
                                result.Add(make_item(info, "ローカル"));
                        }
                    }

                    // ▼▼▼ [Sprint 5F] 復元前の自動バックアップ（.bak）も一覧に追加 ▼▼▼
                    // バックアップフォルダ内の before_restore_ ファイルを検索
                    // （NAS・ローカル両方のバックアップフォルダを検索）
                    var search_dirs = new System.Collections.Generic.List<(string dir, string loc)>();
                    if (Directory.Exists(NAS_BACKUP_DIR))
                        search_dirs.Add((NAS_BACKUP_DIR, "NAS"));
                    if (Directory.Exists(local_backup_dir))
                        search_dirs.Add((local_backup_dir, "ローカル"));

                    foreach (var (dir, loc) in search_dirs)
                    {
                        foreach (var f in Directory.GetFiles(dir, "before_restore_*.db")
                            .OrderByDescending(x => x))
                        {
                            var info = new FileInfo(f);
                            result.Add(make_item(info, loc, is_safety: true));
                        }
                    }

                    // ▼▼▼ 全アイテムを作成日時の降順でソート（定期・退避混在で時系列に並べる） ▼▼▼
                    return result
                        .OrderByDescending(x => x.created_at)
                        .ToList();
                });

                backup_items.Clear();
                foreach (var item in items)
                    backup_items.Add(item);

                status_message = backup_items.Count == 0
                    ? "バックアップが見つかりませんでした"
                    : $"{backup_items.Count}件のバックアップ";
            }
            catch (Exception ex)
            {
                status_message = $"読み込みエラー：{ex.Message}";
            }
            finally
            {
                is_busy = false;
                _restore_relay.NotifyCanExecuteChanged();
            }
        }

        // ---- 復元 ----

        /// <summary>選択したバックアップファイルを現在のDBに上書き復元する</summary>
        private async Task restore_async()
        {
            if (_selected_item == null) return;

            // 確認ダイアログ
            var confirm = System.Windows.MessageBox.Show(
                $"以下のバックアップに復元しますか？\n\n" +
                $"ファイル：{_selected_item.file_name}\n" +
                $"作成日時：{_selected_item.created_at}\n" +
                $"保存場所：{_selected_item.location}\n\n" +
                "⚠ 現在のデータは上書きされます。\n" +
                "復元後アプリを再起動してください。",
                "バックアップから復元",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.OK) return;

            is_busy = true;
            status_message = "復元中...";
            try
            {
                string source = _selected_item.file_path;
                string dest = database_manager.get_db_path();

                await Task.Run(() =>
                {
                    // ▼▼▼ 退避ファイルをバックアップフォルダに保存（検索できるよう統一） ▼▼▼
                    string backup_dir = Directory.Exists(NAS_BACKUP_DIR)
                        ? NAS_BACKUP_DIR
                        : Path.Combine(Path.GetDirectoryName(dest) ?? "", "backups");

                    if (!Directory.Exists(backup_dir))
                        Directory.CreateDirectory(backup_dir);

                    string safety_name = $"before_restore_{DateTime.Now:yyyyMMdd_HHmmss}.db";
                    string safety_path = Path.Combine(backup_dir, safety_name);

                    if (File.Exists(dest))
                        File.Copy(dest, safety_path, true);

                    // 復元
                    File.Copy(source, dest, overwrite: true);
                });

                status_message = "✅ 復元完了。アプリを再起動してください。";

                // 再起動確認
                var restart = System.Windows.MessageBox.Show(
                    "復元が完了しました。\n今すぐ再起動しますか？",
                    "復元完了",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information);

                if (restart == System.Windows.MessageBoxResult.Yes)
                    app_restart_helper.restart();
            }
            catch (Exception ex)
            {
                status_message = $"❌ 復元エラー：{ex.Message}";
                System.Windows.MessageBox.Show(
                    $"復元に失敗しました。\n\n{ex.Message}",
                    "エラー",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                is_busy = false;
                _restore_relay.NotifyCanExecuteChanged();
            }
        }

        // ---- ヘルパー ----

        private static backup_item make_item(FileInfo info, string location, bool is_safety = false)
        {
            // ファイル名からタイムスタンプを解析
            // 通常: EA_CostManager_ea_core_yyyyMMdd_HHmmss.db
            // 退避: ea_core.db.before_restore_yyyyMMdd_HHmmss.bak
            string created_at = "";
            try
            {
                string stem = Path.GetFileNameWithoutExtension(info.Name);
                var parts = stem.Split('_');
                if (parts.Length >= 2)
                {
                    string date_str = parts[^2];
                    string time_str = parts[^1];
                    if (date_str.Length == 8 && time_str.Length == 6
                        && DateTime.TryParseExact(
                            date_str + time_str, "yyyyMMddHHmmss",
                            null, System.Globalization.DateTimeStyles.None,
                            out var dt))
                    {
                        created_at = dt.ToString("yyyy/MM/dd HH:mm:ss");
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(created_at))
                created_at = info.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss");

            double mb = info.Length / 1024.0 / 1024.0;
            string size = mb >= 1.0 ? $"{mb:0.0} MB" : $"{info.Length / 1024.0:0.0} KB";

            return new backup_item
            {
                file_path = info.FullName,
                file_name = info.Name,
                file_size = size,
                created_at = created_at,
                location = location,
                is_safety_backup = is_safety,
            };
        }
    }
}
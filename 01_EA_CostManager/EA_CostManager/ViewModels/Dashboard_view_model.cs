using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using EA_CostManager.Data;

namespace EA_CostManager.ViewModels
{
    // ---- お知らせ1件分のモデル ----
    // NASの news.json から読み込む（管理者がファイルを編集して更新）
    public class news_item
    {
        public string date { get; set; } = "";
        public string title { get; set; } = "";
        public string body { get; set; } = "";

        // 7日以内の項目は「NEW」バッジを表示
        public bool has_new_badge =>
            DateTime.TryParse(date, out var dt) && (DateTime.Today - dt.Date).Days <= 7;

        // 表示用日付（yyyy/MM/dd形式）
        public string display_date =>
            DateTime.TryParse(date, out var dt) ? dt.ToString("yyyy/MM/dd") : date;
    }

    // ---- 操作ログ1行分のモデル ----
    public class operation_log_item
    {
        public string log_datetime { get; set; } = "";
        public string operator_name { get; set; } = "";
        public string operation_type { get; set; } = "";
        public string detail { get; set; } = "";

        // 操作種別ごとのアイコン文字
        public string icon => operation_type switch
        {
            "単価変更" => "💴",
            "日報読込" => "📥",
            "現場アーカイブ" => "📦",
            "並び順リセット" => "🔄",
            "ユーザー登録" => "👤",
            _ => "📋"
        };

        // 表示用日時（例：04/10 14:32）
        public string display_datetime =>
            DateTime.TryParse(log_datetime, out var dt)
                ? dt.ToString("MM/dd HH:mm")
                : log_datetime;
    }

    // ---- 日報読込バッチ内の個別ファイル情報 ----
    public class import_file_entry
    {
        // ファイルのフルパス
        public string file_path { get; set; } = "";
        // そのファイルの取込件数
        public int record_count { get; set; } = 0;

        // ファイル名のみ取り出す（パスからファイル名部分を抽出）
        public string file_name =>
            string.IsNullOrWhiteSpace(file_path)
                ? "（ファイル名不明）"
                : System.IO.Path.GetFileName(file_path);

        // 表示用テキスト（「・250321_作業日報.xlsx  358件」の形式）
        public string display_text => $"・{file_name}　{record_count} 件";
    }

    // ---- 日報読込バッチ1グループ分のモデル ----
    // ★v0.9.7修正：複数ファイルを1つの枠にまとめて表示する構造に変更
    // 1ファイルの場合は従来通り、複数ファイルの場合はファイルごとに件数を表示
    public class import_batch_item
    {
        public string import_batch { get; set; } = "";
        public string operator_name { get; set; } = "";
        public string log_datetime { get; set; } = "";

        // ★v0.9.7追加：バッチ内の全ファイル情報（GROUP_CONCATからパースして格納）
        public List<import_file_entry> file_items { get; set; } = new();

        // 合計件数（全ファイルの件数合算）
        public int total_count => file_items.Sum(f => f.record_count);

        // ファイル数
        public int file_count => file_items.Count;

        // 複数ファイルかどうか（表示切替用：XAMLのVisibility判定に使用）
        public bool is_multi_file => file_items.Count > 1;

        // 1ファイル時のファイル名（従来互換）
        public string file_name =>
            file_items.Count == 1 ? file_items[0].file_name : "";

        // 1ファイル時の件数（従来互換）
        public int record_count =>
            file_items.Count == 1 ? file_items[0].record_count : total_count;

        // ★v0.9.7追加：複数ファイル時の表示用テキスト（改行区切り）
        // 「・250321_作業日報.xlsx  358件\n・250621_作業日報.xlsx  420件」の形式
        public string file_list_text =>
            string.Join("\n", file_items.Select(f => f.display_text));

        // ★v0.9.7追加：合計行テキスト（複数ファイル時のみ表示）
        public string total_text => $"合計 {total_count:#,##0} 件";

        // 表示用日時
        public string display_datetime =>
            DateTime.TryParse(log_datetime, out var dt)
                ? dt.ToString("yyyy/MM/dd HH:mm")
                : log_datetime;
    }

    /// <summary>
    /// ダッシュボードページのViewModel
    /// 操作ログ（直近20件）と日報読込履歴（バッチグループ）を取得して表示する
    /// </summary>
    public class dashboard_view_model : base_view_model
    {
        // ---- 操作ログ（単価変更・現場アーカイブ・並び順リセット等）----
        public ObservableCollection<operation_log_item> operation_logs { get; } = new();

        // ---- 日報読込履歴（バッチごとグループ化）----
        public ObservableCollection<import_batch_item> import_batches { get; } = new();

        // ---- お知らせ（NAS の news.json から読み込む）----
        public ObservableCollection<news_item> news_items { get; } = new();

        // お知らせが1件以上あるときのみセクションを表示
        // CollectionChanged で更新通知を発行する
        private bool _has_news = false;
        public bool has_news
        {
            get => _has_news;
            private set => SetProperty(ref _has_news, value);
        }

        private string _status = "";
        public string status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        // ▼▼▼ [Sprint 6] アップデート情報 ▼▼▼
        private string _latest_version = "";
        public string latest_version
        {
            get => _latest_version;
            private set
            {
                if (SetProperty(ref _latest_version, value))
                    OnPropertyChanged(nameof(has_pending_update));
            }
        }

        /// <summary>未適用のアップデートがあるか（バナー表示フラグ）</summary>
        public bool has_pending_update =>
            !string.IsNullOrEmpty(_latest_version)
            && AppVersionInfo.is_newer(AppVersionInfo.CURRENT_VERSION, _latest_version);

        /// <summary>アップデートバナーのメッセージ</summary>
        public string update_banner_text =>
            $"🎉 新しいバージョン v{_latest_version} が利用できます（現在：v{AppVersionInfo.CURRENT_VERSION}）";

        public dashboard_view_model()
        {
            // ▼▼▼ 修正：load_asyncはViewのIsVisibleChangedから呼ぶ ▼▼▼
            // コンストラクタ内でfire-and-forgetすると、DataContext設定＝バインド確立前に
            // is_busy変化が起きてローディングバーが表示されない問題を防ぐ
        }

        /// <summary>DB からログ・読込履歴を取得する</summary>
        public async Task load_async()
        {
            is_busy = true;
            try
            {
                using var conn = database_manager.create_connection();

                // ---- お知らせ（NAS の news.json）----
                await load_news_async();

                // ▼▼▼ [Sprint 6] バージョンチェック（NAS接続時のみ・サイレント） ▼▼▼
                _ = check_update_async();

                // ---- 操作ログ：対象操作種別のみ直近20件 ----
                // pc_users と LEFT JOIN して最新の user_name を取得
                // pc_user_id=0 の古いログは operator_name をそのまま使用
                var logs = (await conn.QueryAsync<dynamic>(@"
                    SELECT
                        ol.log_datetime,
                        COALESCE(pu.user_name, ol.operator_name) AS operator_name,
                        ol.operation_type,
                        ol.detail
                    FROM operation_logs ol
                    LEFT JOIN pc_users pu ON pu.id = ol.pc_user_id AND ol.pc_user_id > 0
                    WHERE ol.operation_type IN ('単価変更', '現場アーカイブ', '並び順リセット', 'ユーザー登録')
                      AND ol.log_datetime >= datetime('now', '-1 months', 'localtime')
                    ORDER BY ol.id DESC
                    LIMIT 50")).ToList();

                operation_logs.Clear();
                foreach (var r in logs)
                {
                    operation_logs.Add(new operation_log_item
                    {
                        log_datetime = (string)(r.log_datetime ?? ""),
                        operator_name = (string)(r.operator_name ?? ""),
                        operation_type = (string)(r.operation_type ?? ""),
                        detail = (string)(r.detail ?? ""),
                    });
                }

                // ★v0.9.7修正：日報読込履歴をバッチ単位でまとめ、中身はファイルごとに表示
                // GROUP_CONCATでファイルパスと件数のペアを「;;」区切りで1フィールドに格納
                // 1ファイル取込の場合は従来通り、複数ファイルの場合は「・ファイル名 件数」形式で一覧表示
                var batches = (await conn.QueryAsync<dynamic>(@"
                    SELECT
                        ol.import_batch,
                        MIN(COALESCE(pu.user_name, ol.operator_name)) AS operator_name,
                        MAX(ol.log_datetime) AS log_datetime,
                        GROUP_CONCAT(ol.file_path || '|' || ol.record_count, ';;') AS file_details
                    FROM operation_logs ol
                    LEFT JOIN pc_users pu ON pu.id = ol.pc_user_id AND ol.pc_user_id > 0
                    WHERE ol.operation_type = '日報読込'
                      AND ol.import_batch IS NOT NULL
                      AND ol.import_batch != ''
                      AND ol.log_datetime >= datetime('now', '-1 months', 'localtime')
                    GROUP BY ol.import_batch
                    ORDER BY MAX(ol.log_datetime) DESC
                    LIMIT 50")).ToList();

                import_batches.Clear();
                foreach (var r in batches)
                {
                    // GROUP_CONCATの結果をパースしてファイル一覧を構築
                    // 形式：「パス1|件数1;;パス2|件数2;;...」
                    var item = new import_batch_item
                    {
                        import_batch = (string)(r.import_batch ?? ""),
                        operator_name = (string)(r.operator_name ?? ""),
                        log_datetime = (string)(r.log_datetime ?? ""),
                    };

                    // file_details をパースして file_items リストに変換
                    string details = (string)(r.file_details ?? "");
                    if (!string.IsNullOrWhiteSpace(details))
                    {
                        // 「;;」で分割 → 各要素を「|」で分割してファイル名と件数を取得
                        foreach (var pair in details.Split(";;", StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = pair.Split('|');
                            item.file_items.Add(new import_file_entry
                            {
                                file_path = parts.Length > 0 ? parts[0] : "",
                                record_count = parts.Length > 1 && int.TryParse(parts[1], out int cnt) ? cnt : 0,
                            });
                        }
                    }

                    import_batches.Add(item);
                }
            }
            catch (Exception ex)
            {
                status = $"読み込みエラー：{ex.Message}";
            }
            finally
            {
                is_busy = false;
            }
        }

        // ▼▼▼ 追加：NAS の news.json からお知らせを読み込む ▼▼▼
        // NAS接続時のみ読み込む。ファイルが存在しない・読み込み失敗の場合はセクションを非表示にする
        // news.json フォーマット：
        // [ { "date": "2026-04-10", "title": "タイトル", "body": "本文" }, ... ]
        // ▼▼▼ [Sprint 6] バージョンチェック ▼▼▼
        private async Task check_update_async()
        {
            try
            {
                string? latest = await EA_CostManager.Services.UpdateCheckerService
                    .get_latest_version_async();
                if (latest == null) return;
                latest_version = latest;
                OnPropertyChanged(nameof(update_banner_text));
            }
            catch { }
        }

        /// <summary>ダッシュボードの「今すぐ更新」ボタンから呼ばれる</summary>
        public async Task install_update_async()
        {
            if (!has_pending_update) return;
            await EA_CostManager.Services.UpdateCheckerService
                .download_and_run_async(_latest_version);
        }

        private async Task load_news_async()
        {
            try
            {
                // ▼▼▼ [Sprint 6] news.json パスを直接指定 ▼▼▼
                // DBパスから派生させると NAS 切替前後でパスがずれる問題を解消
                const string NEWS_PATH =
                    @"\\NAS7E6AA6\Public\000_事務関係\100_SE管理\01_EABASE Series\00_Database\01_Cost Manager\news.json";

                if (!File.Exists(NEWS_PATH))
                {
                    news_items.Clear();
                    has_news = false;
                    return;
                }

                // 非同期でファイル読み込み
                string json = await File.ReadAllTextAsync(NEWS_PATH);
                var items = JsonSerializer.Deserialize<List<news_item>>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new List<news_item>();

                news_items.Clear();
                // 日付降順（新しい順）で追加
                foreach (var item in items.OrderByDescending(n => n.date))
                    news_items.Add(item);

                has_news = news_items.Count > 0;
            }
            catch (Exception ex)
            {
                // NAS未接続・JSON不正等はサイレントに処理（ユーザーにエラー表示しない）
                System.Diagnostics.Debug.WriteLine($"お知らせ読込エラー: {ex.Message}");
                news_items.Clear();
                has_news = false;
            }
        }
    }
}
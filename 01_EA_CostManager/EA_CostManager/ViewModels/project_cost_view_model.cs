using CommunityToolkit.Mvvm.Input;
using Dapper;
using EA_CostManager.Data;
using EA_CostManager.Models;
using EA_CostManager.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace EA_CostManager.ViewModels
{
    /// <summary>
    /// 現場タブ1つ分のViewModel
    /// </summary>
    public class project_cost_view_model : base_view_model
    {
        // ---- 現場情報（読み取り専用） ----
        public int project_id { get; }
        public string category_code { get; }
        public string site_name { get; }
        public string company_name { get; }
        public string tab_color { get; }

        // ▼▼▼ 追加：大区分内での表示番号（rebuild_filtered_tabs で設定） ▼▼▼
        private int _tab_number = 0;
        public int tab_number
        {
            get => _tab_number;
            set => SetProperty(ref _tab_number, value);
        }

        // ▼▼▼ 追加：ドラッグ並び替え用の表示順序 ▼▼▼
        // 0=自動並び順（属性カラー順）/ 1以上=手動並び順（ドラッグで確定した順序）
        // ドラッグ後は属性を変更しても位置が変わらない
        public int sort_order { get; set; } = 0;

        // ▼▼▼ ★v0.9.7追加：複数選択フラグ ▼▼▼
        // CostPageでCtrl+クリック / Shift+クリックで選択された状態を保持する
        // XAML側のDataTriggerでこのフラグがtrueのとき背景色をハイライト表示する
        // 一括属性変更・一括アーカイブ機能で使用される
        private bool _is_multi_selected = false;
        public bool is_multi_selected
        {
            get => _is_multi_selected;
            set => SetProperty(ref _is_multi_selected, value);
        }
        // ▼▼▼ 修正：private set を追加（WPFバインドのTwoWayエラー回避） ▼▼▼
        public string detail { get; private set; } = "";

        /// <summary>タブ表示名：区分コード_現場名</summary>
        // ▼▼▼ 修正：category_code と site_name が同じ場合は重複表示しない（例：「事務_事務」→「事務」）
        public string tab_name =>
            string.IsNullOrWhiteSpace(site_name) || site_name == category_code
                ? category_code
                : $"{category_code}_{site_name}";

        // ---- 集計レコード ----
        private ObservableCollection<cost_record> _cost_records = new();
        public ObservableCollection<cost_record> cost_records
        {
            get => _cost_records;
            private set
            {
                if (SetProperty(ref _cost_records, value))
                {
                    notify_totals();
                    // ▼▼▼ 追加：表示用リスト（月末小計行込み）を再構築 ▼▼▼
                    rebuild_display_rows();
                }
            }
        }

        // ▼▼▼ 追加：DataGrid にバインドするコレクション（月末小計行込み） ▼▼▼
        private ObservableCollection<cost_row_item> _display_rows = new();
        public ObservableCollection<cost_row_item> display_rows
        {
            get => _display_rows;
            private set => SetProperty(ref _display_rows, value);
        }

        // ---- 月次合計（cost_records から都度計算） ----
        public decimal total_engineer_cost => _cost_records.Sum(r => r.engineer_cost);
        public decimal total_assistant_cost => _cost_records.Sum(r => r.assistant_cost);
        public decimal total_personnel_cost => _cost_records.Sum(r => r.personnel_cost);
        public decimal total_transport_cost => _cost_records.Sum(r => r.transport_cost);
        public decimal total_equipment_cost => _cost_records.Sum(r => r.equipment_cost);
        public decimal grand_total => _cost_records.Sum(r => r.total_cost);

        // ---- 月次合計 表示用（XAMLのRun.TextはStringFormat+decimalが不安定なため） ----
        public string display_total_engineer_cost => total_engineer_cost.ToString("#,##0");
        public string display_total_assistant_cost => total_assistant_cost.ToString("#,##0");
        public string display_total_transport_cost => total_transport_cost.ToString("#,##0");
        public string display_total_equipment_cost => total_equipment_cost.ToString("#,##0");
        public string display_grand_total => grand_total.ToString("#,##0");

        // ---- 絞り込みタブ ----
        public ObservableCollection<filter_tab_view_model> filter_tabs { get; } = new();

        // 全件タブ＋絞り込みタブをまとめた表示用コレクション
        public ObservableCollection<filter_tab_view_model> display_tabs { get; } = new();

        // 全件タブ（常に先頭に固定）
        private filter_tab_view_model? _all_records_tab;
        // ▼▼▼ 追加：印刷サービス向けに公開（PrintServiceから参照する） ▼▼▼
        public filter_tab_view_model? all_records_tab => _all_records_tab;

        // 月度リスト（FilterTabDialog に渡す用）
        public System.Collections.Generic.List<string> available_months =>
            _cost_records
                .Where(r => !string.IsNullOrWhiteSpace(r.fiscal_month))
                .Select(r => r.fiscal_month)
                .Distinct()
                .OrderBy(m => m)
                .ToList();

        // コマンド
        public ICommand add_filter_tab_command { get; }
        public ICommand delete_filter_tab_command { get; }
        private bool _is_loading;
        public bool is_loading
        {
            get => _is_loading;
            set
            {
                if (SetProperty(ref _is_loading, value))
                {
                    // ▼▼▼ 追加：ローディング状態変化時にバナー表示も更新 ▼▼▼
                    OnPropertyChanged(nameof(show_custom_rate_banner));
                    OnPropertyChanged(nameof(custom_rate_banner_text));
                }
            }
        }

        // ---- コンストラクタ ----
        public project_cost_view_model(
            int id,
            string cat,
            string site,
            string company,
            string color,
            string det = "",
            string agg_mode = "daily")   // ▼追加(B)：現場(全件タブ)の既定集計モード
        {
            project_id = id;
            category_code = cat;
            site_name = site;
            company_name = company;
            tab_color = color;
            detail = det;

            add_filter_tab_command = new RelayCommand(() => { }); // CostPage.xaml.cs で処理
            delete_filter_tab_command = new RelayCommand<filter_tab_view_model?>(async ft =>
            {
                if (ft == null) return;
                await delete_filter_tab_async(ft);
            });
            // ▼▼▼ 追加：アーカイブコマンド ▼▼▼
            archive_filter_tab_command = new RelayCommand<filter_tab_view_model?>(async ft =>
            {
                if (ft == null) return;
                await archive_filter_tab_async(ft);
            });

            // 全件タブを作成してdisplay_tabsに追加
            _all_records_tab = new filter_tab_view_model(new cost_filter_tab
            {
                tab_name = "全件",
                filter_month = "",
                filter_content = "",
                filter_match = "partial",
                filter_names = "",
                agg_mode = agg_mode   // ▼追加(B)：現場の既定モードを全件タブに反映
            });
            display_tabs.Add(_all_records_tab);

            // ▼▼▼ 追加：コンストラクタでも初期値をセット（起動直後のnull状態を防ぐ） ▼▼▼
            selected_display_tab = _all_records_tab;
        }

        // ---- 絞り込みタブ操作 ----

        /// <summary>絞り込みタブをDBからロード</summary>
        /// <param name="preserve_tab_id">ロード後に選択を維持するタブID（0=全件タブに戻す）</param>
        public async Task load_filter_tabs_async(int preserve_tab_id = 0)
        {
            using var conn = database_manager.create_connection();
            var tabs = (await conn.QueryAsync<cost_filter_tab>(
                "SELECT * FROM cost_filter_tabs WHERE project_id = @pid AND is_archived = 0 ORDER BY id",
                new { pid = project_id })).ToList();

            // ▼▼▼ 修正：子コードを一括取得（IN句）→ 個別SELECT廃止 ▼▼▼
            // 変更前：get_child_codes_async が category_groups を1現場ずつSELECT
            // 変更後：まとめて取得済みのコードリストを使う（ここでは個別取得だが1回のみ）
            var child_codes = await EA_CostManager.Services.CategoryGroupService
                .get_child_codes_async(category_code);

            filter_tabs.Clear();

            // display_tabs は全件タブ（index=0）だけ残してクリア
            while (display_tabs.Count > 1)
                display_tabs.RemoveAt(1);

            // ▼▼▼ 修正：絞り込みタブのload_records_asyncを並列化 ▼▼▼
            // 変更前：foreach で1タブずつ直列にload_records_async（タブ数分DB呼び出し）
            // 変更後：全タブのrecordsを一括取得してから各VMに配る
            // 絞り込みタブで使うコードセット（親＋子）
            var all_codes = new List<string> { category_code };
            all_codes.AddRange(child_codes);

            IEnumerable<cost_record> tab_records_raw = new List<cost_record>();
            if (all_codes.Count > 0)
            {
                tab_records_raw = await conn.QueryAsync<cost_record>(@"
                    SELECT * FROM cost_records
                    WHERE category_code IN @codes
                    ORDER BY record_date",
                    new { codes = all_codes });
            }

            // コードごとにグループ化して配布
            var tab_records_map = tab_records_raw
                .GroupBy(r => r.category_code ?? "")
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var t in tabs)
            {
                var vm = new filter_tab_view_model(t);
                // ▼修正(B)：業務単位/1人ずつ/現場別単価タブは cost_records(日単位)では表せないため、
                //   daily_reports から都度集計する load_records_async を使う。
                //   通常モードのみ一括取得データ(set_records_from_map)を使用する。
                if (vm.is_task_mode || vm.is_single_mode || vm.use_custom_rates)
                    await vm.load_records_async(category_code, child_codes.ToList());
                else
                    vm.set_records_from_map(category_code, child_codes.ToList(), tab_records_map);
                filter_tabs.Add(vm);
                display_tabs.Add(vm);
            }

            // 全件タブにも同じデータを反映
            if (_all_records_tab != null)
                _all_records_tab.set_records_from_map(category_code, child_codes.ToList(), tab_records_map);

            // ▼▼▼ 修正：preserve_tab_id が指定されている場合は選択を維持する ▼▼▼
            if (preserve_tab_id != 0)
            {
                var preserved = display_tabs.FirstOrDefault(t => t.filter_tab_id == preserve_tab_id);
                selected_display_tab = preserved ?? _all_records_tab;
            }
            else
            {
                selected_display_tab = _all_records_tab;
            }
        }


        // ▼▼▼ 追加(B)：選んだモードを表示中タブに適用（その場切替・非破壊） ▼▼▼
        public async Task apply_mode_to_current_async(filter_tab_view_model? tab, string mode)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (tab == null) return;
            mode = (mode == "task") ? "task" : "daily";
            if (tab.agg_mode == mode) return;   // 同一モードなら無駄な再集計をしない

            using (var conn = database_manager.create_connection())
            {
                if (tab.filter_tab_id == 0)
                {
                    // 全件タブ → 現場の既定モードとして保存
                    await conn.ExecuteAsync(
                        "UPDATE projects SET agg_mode = @m WHERE id = @pid",
                        new { m = mode, pid = project_id });
                }
                else
                {
                    try { await conn.ExecuteAsync("ALTER TABLE cost_filter_tabs ADD COLUMN agg_mode TEXT DEFAULT 'daily'"); }
                    catch { /* 既に存在する場合は無視 */ }
                    await conn.ExecuteAsync(
                        "UPDATE cost_filter_tabs SET agg_mode = @m WHERE id = @id",
                        new { m = mode, id = tab.filter_tab_id });
                }
            }

            tab.agg_mode = mode;

            // 再読込：task は daily_reports から都度集計、daily は cost_records 参照
            var child_codes = await EA_CostManager.Services.CategoryGroupService
                .get_child_codes_async(category_code);
            await tab.load_records_async(category_code, child_codes);
        }

        // ▼▼▼ 追加(B)：選んだモードで現在タブを複製し、新しい絞り込みタブを作る ▼▼▼
        public async Task duplicate_with_mode_async(filter_tab_view_model? tab, string mode)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            if (tab == null) return;
            mode = (mode == "task") ? "task" : "daily";
            string suffix = mode == "task" ? "（業務単位）" : "（日単位）";

            var model = new Models.cost_filter_tab
            {
                project_id = project_id,
                tab_name = (string.IsNullOrWhiteSpace(tab.tab_name) ? "全件" : tab.tab_name) + suffix,
                filter_month = tab.filter_month,
                filter_content = tab.filter_content,
                filter_match = tab.filter_match,
                filter_names = tab.filter_names,
                is_single_mode = tab.is_single_mode ? 1 : 0,
                filter_date_from = tab.filter_date_from,
                filter_date_to = tab.filter_date_to,
                use_custom_rates = tab.use_custom_rates ? 1 : 0,
                agg_mode = mode,
            };

            using (var conn = database_manager.create_connection())
            {
                try { await conn.ExecuteAsync("ALTER TABLE cost_filter_tabs ADD COLUMN agg_mode TEXT DEFAULT 'daily'"); }
                catch { /* 既に存在する場合は無視 */ }

                var new_id = await conn.QuerySingleAsync<int>(@"
                    INSERT INTO cost_filter_tabs
                        (project_id, tab_name, filter_month, filter_content, filter_match,
                         filter_names, is_single_mode, filter_date_from, filter_date_to,
                         use_custom_rates, agg_mode)
                    VALUES
                        (@project_id, @tab_name, @filter_month, @filter_content, @filter_match,
                         @filter_names, @is_single_mode, @filter_date_from, @filter_date_to,
                         @use_custom_rates, @agg_mode);
                    SELECT last_insert_rowid();", model);
                model.id = new_id;
            }

            await add_filter_tab_async(model);

            // ▼修正(あ)：複製は「複製元（大元/現在タブ）」を業務単位のまま残さない。
            //   複製で業務単位ビューを別タブに切り出したら、元タブは日単位に戻す。
            //   （apply_mode_to_current_async は同一モードなら何もしないので、元々dailyなら無害）
            await apply_mode_to_current_async(tab, "daily");
        }


        // ▼▼▼ 追加(B)：この現場の cost_records を全期間で作り直し、表示中の全タブ（複製含む）を再読込 ▼▼▼
        // ・取込で日報が増えても集計が古いままだと daily 表示がズレるため、ワンクリックで揃える。
        // ・daily タブ → 作り直した cost_records から再読込 / task タブ → daily_reports から（常に最新）。
        public async Task reaggregate_all_async()
        {
            if (is_reaggregating) return;   // 二重実行防止
            is_reaggregating = true;
            try
            {
                var child_codes = (await EA_CostManager.Services.CategoryGroupService
                    .get_child_codes_async(category_code)).ToList();

                // 1) cost_records を全期間で作り直す（親＋子コード）
                var codes = new List<string> { category_code };
                codes.AddRange(child_codes);
                var svc = new CostAggregationService();
                foreach (var c in codes.Distinct())
                    await svc.aggregate_async(c, "2000-01-01", "2099-12-31");

                // 2) 親の cost_records コレクションも作り直したデータで更新
                using (var conn = database_manager.create_connection())
                {
                    var all_rows = (await conn.QueryAsync<cost_record>(
                        "SELECT * FROM cost_records WHERE category_code IN @codes ORDER BY record_date",
                        new { codes })).ToList();
                    foreach (var r in all_rows)
                        if (r.category_code != category_code) r.category_code = category_code;
                    cost_records = new ObservableCollection<cost_record>(all_rows);
                }

                // 3) 表示中の全タブ（全件＋絞り込み＋複製）を再読込
                foreach (var tab in display_tabs.ToList())
                    await tab.load_records_async(category_code, child_codes);
            }
            finally
            {
                is_reaggregating = false;
            }
        }

        /// <summary>絞り込みタブを追加</summary>
        public async Task add_filter_tab_async(Models.cost_filter_tab model)
        {
            var child_codes = await EA_CostManager.Services.CategoryGroupService
                .get_child_codes_async(category_code);
            var vm = new filter_tab_view_model(model);
            await vm.load_records_async(category_code, child_codes);
            filter_tabs.Add(vm);
            display_tabs.Add(vm); // UIに反映
        }

        /// <summary>絞り込みタブを削除</summary>
        private async Task delete_filter_tab_async(filter_tab_view_model ft)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            using var conn = database_manager.create_connection();
            await conn.ExecuteAsync(
                "DELETE FROM cost_filter_tabs WHERE id = @id",
                new { id = ft.filter_tab_id });

            // ▼修正(B)：削除対象が選択中だと、TabControl が削除の最中に別タブへ
            //   再選択しようとして IndexOutOfRangeException になる（WPFの定番の罠）。
            //   先に全件タブ（必ず存在・index 0）へ選択を逃がしてから Remove する。
            if (ReferenceEquals(selected_display_tab, ft))
                selected_display_tab = _all_records_tab;

            filter_tabs.Remove(ft);
            display_tabs.Remove(ft);
        }

        // ---- データロード ----

        /// <summary>
        /// 全期間の cost_records を DB からロードして一覧を更新する
        /// fiscal_month でグループ化して表示するため日付フィルターなし
        /// ▼▼▼ [Sprint 5E] 区分結合設定がある場合は子コードの records も合算して表示 ▼▼▼
        /// 元データ（daily_reports）は変更しない。表示・集計レイヤーのみで合算する。
        /// ▼▼▼ 修正：子コードのSELECTをIN句で一括取得に変更（ループ内個別SELECT廃止）▼▼▼
        /// </summary>
        public async Task load_records_async(string start_date = "", string end_date = "")
        {
            is_loading = true;
            try
            {
                using var conn = database_manager.create_connection();

                // 子コードを取得（1回のSELECT）
                var child_codes = (await EA_CostManager.Services.CategoryGroupService
                    .get_child_codes_async(category_code)).ToList();

                // ▼▼▼ 修正：親コード＋子コードをIN句で一括取得 ▼▼▼
                // 変更前：親コードSELECT → foreach(子コード)でSELECT（子コード数分追加）
                // 変更後：IN句で1回にまとめる
                var all_codes = new List<string> { category_code };
                all_codes.AddRange(child_codes);

                var all_rows = (await conn.QueryAsync<cost_record>(@"
                    SELECT * FROM cost_records
                    WHERE category_code IN @codes
                    ORDER BY record_date",
                    new { codes = all_codes })).ToList();

                // 子コードのレコードは表示上親コードとして扱う（元データは変更しない）
                foreach (var r in all_rows)
                {
                    if (r.category_code != category_code)
                        r.category_code = category_code;
                }

                cost_records = new ObservableCollection<cost_record>(all_rows);

                // 全件タブにも同じデータを反映（子コードも渡す）
                if (_all_records_tab != null)
                    await _all_records_tab.load_records_async(category_code, child_codes);
            }
            finally
            {
                is_loading = false;
            }
        }

        // ---- プライベート ----

        /// <summary>合計プロパティの変更通知をまとめて発行</summary>
        private void notify_totals()
        {
            OnPropertyChanged(nameof(total_engineer_cost));
            OnPropertyChanged(nameof(total_assistant_cost));
            OnPropertyChanged(nameof(total_personnel_cost));
            OnPropertyChanged(nameof(total_transport_cost));
            OnPropertyChanged(nameof(total_equipment_cost));
            OnPropertyChanged(nameof(grand_total));
            OnPropertyChanged(nameof(display_total_engineer_cost));
            OnPropertyChanged(nameof(display_total_assistant_cost));
            OnPropertyChanged(nameof(display_total_transport_cost));
            OnPropertyChanged(nameof(display_total_equipment_cost));
            OnPropertyChanged(nameof(display_grand_total));
        }

        // ▼▼▼ 追加：月末小計行込みの display_rows を構築 ▼▼▼
        private void rebuild_display_rows()
        {
            var result = new ObservableCollection<cost_row_item>();

            var groups = _cost_records
                .GroupBy(r => r.fiscal_month ?? "")
                .OrderBy(g => g.Key);

            foreach (var group in groups)
            {
                foreach (var rec in group.OrderBy(r => r.record_date))
                    result.Add(cost_row_item.from_record(rec));

                result.Add(cost_row_item.make_subtotal(group.Key, group));
            }

            display_rows = result;
        }

        // ▼▼▼ 追加：絞り込みタブのアーカイブ（非表示）コマンド ▼▼▼
        public ICommand archive_filter_tab_command { get; }

        /// <summary>絞り込みタブをアーカイブ（is_archived=1に更新・画面から非表示）</summary>
        private async Task archive_filter_tab_async(filter_tab_view_model ft)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            using var conn = database_manager.create_connection();
            await conn.ExecuteAsync(
                "UPDATE cost_filter_tabs SET is_archived = 1 WHERE id = @id",
                new { id = ft.filter_tab_id });

            // ▼修正(B)：削除（非表示化）対象が選択中だと TabControl が落ちるため、先に全件タブへ選択を逃がす
            if (ReferenceEquals(selected_display_tab, ft))
                selected_display_tab = _all_records_tab;

            filter_tabs.Remove(ft);
            display_tabs.Remove(ft);
        }

        // ▼▼▼ 追加：アーカイブ済みタブ一覧を取得 ▼▼▼
        public async Task<List<cost_filter_tab>> get_archived_tabs_async()
        {
            using var conn = database_manager.create_connection();
            var tabs = await conn.QueryAsync<cost_filter_tab>(
                "SELECT * FROM cost_filter_tabs WHERE project_id = @pid AND is_archived = 1 ORDER BY id",
                new { pid = project_id });
            return tabs.ToList();
        }

        // ▼▼▼ 追加：アーカイブ済みタブを復元（is_archived=0に戻して再表示） ▼▼▼
        public async Task restore_filter_tab_async(cost_filter_tab model)
        {
            if (ReadOnlyGuard.block_if_read_only()) return;   // ▼ 追加 [Sprint 8 / Phase 0]
            using var conn = database_manager.create_connection();
            await conn.ExecuteAsync(
                "UPDATE cost_filter_tabs SET is_archived = 0 WHERE id = @id",
                new { id = model.id });

            // 復元したタブをVMとして生成してdisplay_tabsに追加
            var vm = new filter_tab_view_model(model);
            await vm.load_records_async(category_code);
            filter_tabs.Add(vm);
            display_tabs.Add(vm);
        }

        // ▼▼▼ 修正②：属性カラーマッピング更新 ▼▼▼
        // 変更内容：
        //   契約前  : オレンジ → ピンク  (#E91E8C)
        //   契約済  : 紫       → オレンジ (#E67E22)
        //   自社案件: 青のまま  (#2E75B6)  ※ 右クリックメニュー表記「自社案件」と一致させる
        //   第一土木: 緑のまま  (#27AE60)
        //   セミナー: ピンク   → 紫      (#8E44AD)
        public static string get_tab_color(string attribute) => attribute switch
        {
            "契約前" => "#E91E8C",  // ピンク（変更：旧オレンジ #E67E22）
            "契約済" => "#E67E22",  // オレンジ（変更：旧紫 #8E44AD）
            "自社案件" => "#2E75B6",  // 青（変更なし）
            "第一土木" => "#27AE60",  // 緑（変更なし）
            "セミナー" => "#8E44AD",  // 紫（変更：旧ピンク #E91E8C）
            "追加" => "#E8543A",  // コーラル（変更なし）
            "その他" => "#7F8C8D",  // グレー（変更なし）
            _ => "#95A5A6",  // 不明はグレー
        };

        // ▼▼▼ 追加：この現場に専用単価が設定されているか（UI表示用） ▼▼▼
        private bool _has_custom_rates = false;
        public bool has_custom_rates
        {
            get => _has_custom_rates;
            private set
            {
                if (SetProperty(ref _has_custom_rates, value))
                {
                    OnPropertyChanged(nameof(show_custom_rate_banner));
                    OnPropertyChanged(nameof(custom_rate_banner_text));
                }
            }
        }

        /// <summary>project_rates テーブルにこの現場の単価設定があるか確認してフラグを更新</summary>
        public async Task check_custom_rates_async()
        {
            using var conn = database_manager.create_connection();
            var count = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM project_rates WHERE project_id = @id",
                new { id = project_id });
            has_custom_rates = count > 0;
        }

        // ▼▼▼ 追加：現在選択中の絞り込みタブ追跡（Sprint 3.7） ▼▼▼
        // CostPage.xaml の filter TabControl.SelectedItem とTwoWayバインドして選択タブを追跡する
        private filter_tab_view_model? _selected_display_tab;

        // ▼▼▼ 追加(B)：再集計中フラグ（ローディングバー表示用） ▼▼▼
        private bool _is_reaggregating;
        public bool is_reaggregating
        {
            get => _is_reaggregating;
            set => SetProperty(ref _is_reaggregating, value);
        }

        public filter_tab_view_model? selected_display_tab
        {
            get => _selected_display_tab;
            set
            {
                if (SetProperty(ref _selected_display_tab, value))
                {
                    // 選択タブが変わるたびにバナー表示を更新する
                    OnPropertyChanged(nameof(selected_tab_uses_custom_rates));
                    OnPropertyChanged(nameof(show_custom_rate_banner));
                    OnPropertyChanged(nameof(custom_rate_banner_text));
                    // ▼削除(B)：タブ選択のたびにコンボを書き換える自動同期は、
                    //   コンボ(TwoWay)↔タブ選択の連動でフリーズの原因になり、かつ要望には不要なので撤去。
                    //   現在モードは枠色（is_task_mode）で表示。複製後の大元リセットは duplicate_with_mode_async 側で実施。
                }
            }
        }

        /// <summary>
        /// 現在選択中のタブが現場別単価（use_custom_rates=1）を使用しているか
        /// </summary>
        public bool selected_tab_uses_custom_rates =>
            _selected_display_tab?.use_custom_rates == true;

        /// <summary>
        /// 「単価変更適用中」バナーを表示するか
        /// ローディング中はチラつき防止のため非表示にする
        /// use_custom_rates=1 のタブを見ているとき、または project_rates が存在するとき表示
        /// </summary>
        public bool show_custom_rate_banner =>
            !is_loading && (_selected_display_tab?.use_custom_rates == true || _rates_applied_to_all);

        /// <summary>
        /// バナーに表示するテキスト
        /// - タブ固有の単価：「⚠ 単価変更適用中」
        /// - すべてのタブで再集計済み：「💴 カスタム単価設定済み」
        /// </summary>
        public string custom_rate_banner_text =>
            _selected_display_tab?.use_custom_rates == true
                ? "⚠ 単価変更適用中"
                : "💴 カスタム単価設定済み";

        // ▼▼▼ 追加：「すべてのタブで再集計」後のフラグ ▼▼▼
        // true のとき = cost_records にカスタム単価が適用済み → 全件タブにもバナーを表示
        // false のとき = 新規タブ作成のみ → 全件タブには表示しない
        private bool _rates_applied_to_all = false;
        public void set_rates_applied_to_all(bool value)
        {
            _rates_applied_to_all = value;
            OnPropertyChanged(nameof(show_custom_rate_banner));
            OnPropertyChanged(nameof(custom_rate_banner_text));
        }

        // ▼▼▼ 追加：起動時一括取得データを受け取るメソッド（DB呼び出しなし） ▼▼▼
        // cost_view_model.load_projects_async() から呼ばれる
        // 一括取得済みの records を直接セットすることで起動時のDB呼び出し回数を削減する

        /// <summary>
        /// 一括取得済みの cost_records を直接セットする（起動時専用）
        /// DB呼び出しは cost_view_model 側で一括実施済みのため、ここではDB接続しない
        /// </summary>
        public void set_records_bulk(
            List<cost_record> my_records,
            List<string> child_codes,
            List<cost_record> child_records)
        {
            is_loading = true;
            try
            {
                // 子コードのレコードは表示上親コードとして扱う
                foreach (var r in child_records)
                    r.category_code = category_code;

                var merged = my_records
                    .Concat(child_records)
                    .OrderBy(r => r.record_date)
                    .ToList();

                cost_records = new ObservableCollection<cost_record>(merged);

                // 全件タブにも同じデータを反映
                if (_all_records_tab != null)
                    _all_records_tab.set_records_from_map(
                        category_code, child_codes,
                        merged.GroupBy(r => r.category_code ?? "")
                              .ToDictionary(g => g.Key, g => g.ToList()));
            }
            finally
            {
                is_loading = false;
            }
        }

        /// <summary>
        /// 一括取得済みの cost_filter_tabs を直接セットする（起動時専用）
        /// フィルタタブのレコードも records_map から取得してDB呼び出しを省略する
        /// </summary>
        public async Task set_filter_tabs_bulk_async(
            List<cost_filter_tab> tabs,
            string parent_code,
            List<string> child_codes,
            Dictionary<string, List<cost_record>> records_map)
        {
            filter_tabs.Clear();

            // display_tabs は全件タブ（index=0）だけ残してクリア
            while (display_tabs.Count > 1)
                display_tabs.RemoveAt(1);

            foreach (var t in tabs)
            {
                var vm = new filter_tab_view_model(t);

                // ▼▼▼ is_single_mode / use_custom_rates タブは daily_reports から直接集計が必要 ▼▼▼
                // これらは records_map（cost_records）では対応できないため load_records_async にフォールバック
                // 通常モードのみ set_records_from_map で一括取得データを使用する
                if (vm.is_task_mode || vm.is_single_mode || vm.use_custom_rates)   // ▼修正(B)：task追加
                    await vm.load_records_async(parent_code, child_codes);
                else
                    vm.set_records_from_map(parent_code, child_codes, records_map);

                filter_tabs.Add(vm);
                display_tabs.Add(vm);
            }

            selected_display_tab = _all_records_tab;

            // ※ awaitが必要な後続処理の互換性維持のためasyncメソッドとして定義
            await System.Threading.Tasks.Task.CompletedTask;
        }
    }
}